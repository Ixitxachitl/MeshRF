// SPDX-License-Identifier: GPL-3.0-or-later
#include "Sx126x.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

namespace mrf::hal {
namespace {

// --- Command opcodes (datasheet table 11-1) ----------------------------
enum : std::uint8_t {
    kCmdSetStandby            = 0x80,
    kCmdSetRx                 = 0x82,
    kCmdSetTx                 = 0x83,
    kCmdGetRxBufferStatus     = 0x13,
    kCmdGetPacketStatus       = 0x14,
    kCmdReadBuffer            = 0x1E,
    kCmdSetRfFrequency        = 0x86,
    kCmdSetPacketType         = 0x8A,
    kCmdSetModulationParams   = 0x8B,
    kCmdSetPacketParams       = 0x8C,
    kCmdSetTxParams           = 0x8E,
    kCmdSetBufferBaseAddress  = 0x8F,
    kCmdSetPaConfig           = 0x95,
    kCmdSetDio3AsTcxoCtrl     = 0x97,
    kCmdCalibrate             = 0x89,
    kCmdCalibrateImage        = 0x98,
    kCmdSetDio2AsRfSwitchCtrl = 0x9D,
    kCmdSetDioIrqParams       = 0x08,
    kCmdGetIrqStatus          = 0x12,
    kCmdClearIrqStatus        = 0x02,
    kCmdWriteRegister         = 0x0D,
    kCmdReadRegister          = 0x1D,
    kCmdWriteBuffer           = 0x0E,
    kCmdGetDeviceErrors       = 0x17,
    kCmdClearDeviceErrors     = 0x07,
    kNop                      = 0x00,
};

// --- Registers ---------------------------------------------------------
enum : std::uint16_t {
    kRegSyncWordMsb   = 0x0740,
    kRegTxModulation  = 0x0889, // datasheet 15.1 (500 kHz modulation quality)
    kRegTxClampConfig = 0x08D8, // datasheet 15.2 (antenna-mismatch protection)
    kRegOcpConfig     = 0x08E7,
};

// --- IRQ bits ----------------------------------------------------------
enum : std::uint16_t {
    kIrqTxDone    = 0x0001,
    kIrqRxDone    = 0x0002,
    kIrqHeaderErr = 0x0020,
    kIrqCrcErr    = 0x0040,
    kIrqTimeout   = 0x0200,
};

// SetRx timeout value meaning "stay in receive until told otherwise".
constexpr std::uint32_t kRxContinuous = 0xFFFFFFu;

enum : std::uint8_t {
    kPacketTypeLora = 0x01,
    kStandbyRc      = 0x00,
    kPaRamp200Us    = 0x04,
    kOcp140Ma       = 0x38, // required by the +22 dBm PA configuration
};

// One LSB of the SetTx timeout and the TCXO startup delay.
constexpr double kTickSeconds = 15.625e-6;

std::uint32_t frequency_to_pll(std::uint64_t hz) {
    // freq_reg = hz * 2^25 / 32 MHz, computed in 64-bit to avoid overflow.
    return static_cast<std::uint32_t>((hz << 25) / 32'000'000ull);
}

// Image-calibration bands (datasheet table 9-2). Picking the wrong band only
// costs sensitivity, but the sticks are single-band anyway.
void calibration_band(std::uint64_t hz, std::uint8_t& lo, std::uint8_t& hi) {
    const double mhz = static_cast<double>(hz) / 1e6;
    if (mhz >= 902.0 && mhz <= 928.0)      { lo = 0xE1; hi = 0xE9; }
    else if (mhz >= 863.0 && mhz <= 870.0) { lo = 0xD7; hi = 0xDB; }
    else if (mhz >= 779.0 && mhz <= 787.0) { lo = 0xC1; hi = 0xC5; }
    else if (mhz >= 470.0 && mhz <= 510.0) { lo = 0x75; hi = 0x81; }
    else if (mhz >= 430.0 && mhz <= 440.0) { lo = 0x6B; hi = 0x6F; }
    else                                   { lo = 0xE1; hi = 0xE9; }
}

// The Custom SPI board's declaration. Guarded because the UI can rewrite it
// from the settings dialog while a poll of the power range reads it.
std::mutex           g_custom_mu;
Sx126xCustomSpiBoard g_custom{};

// Built on first use rather than as a namespace-scope array: the SPI entries
// carry std::string device names, so this is dynamically initialized, and a
// function-local static is the one form of that with no initialization-order
// question to answer.
const std::vector<Sx126xBoardProfile>& profiles() {
    static const std::vector<Sx126xBoardProfile> kProfiles = [] {
        std::vector<Sx126xBoardProfile> v;

        // No selection. Deliberately first so it is also the lookup fallback:
        // an unrecognised board value must land on something that cannot
        // transmit, not on a real profile that would quietly pick a power
        // model for the user.
        v.push_back({
            Sx126xBoard::Unspecified, "(no board selected)",
            Sx126xTransport::Ch341Usb, /*spi*/ {},
            /*has_rxen*/ true, /*dio2_as_rf_switch*/ true, /*dio3_tcxo*/ true,
            /*tcxo_voltage*/ 0x02, /*max_chip_dbm*/ -9, /*pa_gain_db*/ 0,
            /*min_out_dbm*/ 0, /*max_out_dbm*/ 0,
        });

        // Elecrow MeshStick: bare SX1262, so what the chip puts out is what
        // reaches the antenna.
        v.push_back({
            Sx126xBoard::MeshStick, "MeshStick (SX1262)",
            Sx126xTransport::Ch341Usb, /*spi*/ {},
            /*has_rxen*/ true, /*dio2_as_rf_switch*/ true, /*dio3_tcxo*/ true,
            /*tcxo_voltage*/ 0x02, /*max_chip_dbm*/ 22, /*pa_gain_db*/ 0,
            /*min_out_dbm*/ -9, /*max_out_dbm*/ 22,
        });

        // NullHop/muzi MeshToad V3: SX1262 driving an E22P-915M30S, which adds
        // roughly 8 dB. Note the module can pull ~900 mA on TX at full power,
        // over what a USB 2.0 port must supply — see the power warning
        // surfaced in the UI.
        v.push_back({
            Sx126xBoard::MeshToad, "MeshToad V3 (SX1262 + E22P-915M30S)",
            Sx126xTransport::Ch341Usb, /*spi*/ {},
            /*has_rxen*/ true, /*dio2_as_rf_switch*/ true, /*dio3_tcxo*/ true,
            /*tcxo_voltage*/ 0x02, /*max_chip_dbm*/ 22, /*pa_gain_db*/ 8,
            /*min_out_dbm*/ -1, /*max_out_dbm*/ 30,
        });

        // uConsole AIO V2: bare SX1262 on SPI1, DIO2 running the RF switch, so
        // no RXEN line. Wiring taken from the board's meshtasticd config;
        // being a bare chip with no front end, its power model is the
        // MeshStick's. GPIO 16 gates the radio's power on this board and is
        // the AIO's own to drive — MeshRF does not touch it, so a radio that
        // has not been enabled simply is not there.
        Sx126xSpiPins aio;
        aio.spidev   = "spidev1.0"; // SPI1, CS on the controller's CE0 (GPIO 18)
        aio.gpiochip = "gpiochip0";
        aio.cs       = -1;          // hardware chip select
        aio.busy     = 24;
        aio.reset    = 25;
        aio.dio1     = 26;
        aio.rxen     = -1;
        v.push_back({
            Sx126xBoard::UConsoleAio, "uConsole AIO V2 (SX1262)",
            Sx126xTransport::LinuxSpi, aio,
            /*has_rxen*/ false, /*dio2_as_rf_switch*/ true, /*dio3_tcxo*/ true,
            /*tcxo_voltage*/ 0x02, /*max_chip_dbm*/ 22, /*pa_gain_db*/ 0,
            /*min_out_dbm*/ -9, /*max_out_dbm*/ 22,
        });

        // Custom SPI. Placeholder only: every field is replaced from the
        // operator's declaration in sx126x_profile(), and the empty pin map
        // left here refuses to open until they have made one.
        v.push_back({
            Sx126xBoard::CustomSpi, "Custom SX1262 (SPI)",
            Sx126xTransport::LinuxSpi, /*spi*/ {},
            /*has_rxen*/ false, /*dio2_as_rf_switch*/ true, /*dio3_tcxo*/ true,
            /*tcxo_voltage*/ 0x02, /*max_chip_dbm*/ 22, /*pa_gain_db*/ 0,
            /*min_out_dbm*/ -9, /*max_out_dbm*/ 22,
        });

        return v;
    }();
    return kProfiles;
}

} // namespace

void set_custom_spi_board(const Sx126xCustomSpiBoard& board) {
    std::lock_guard<std::mutex> lk(g_custom_mu);
    g_custom = board;
}

Sx126xCustomSpiBoard custom_spi_board() {
    std::lock_guard<std::mutex> lk(g_custom_mu);
    return g_custom;
}

Sx126xBoardProfile sx126x_profile(Sx126xBoard board) {
    const auto& all = profiles();
    const Sx126xBoardProfile* found = nullptr;
    for (const auto& p : all)
        if (p.board == board) { found = &p; break; }
    // Fail safe, not open: an unknown value gets the Unspecified profile,
    // which cannot transmit, rather than a real board's power model.
    if (found == nullptr) return all.front();

    if (board != Sx126xBoard::CustomSpi) return *found;

    // The custom entry is whatever the operator last declared. Everything
    // about it is theirs to state, the power model included: MeshRF cannot
    // probe a front end, and assuming there is none would under-report the
    // output of every board that has one.
    const Sx126xCustomSpiBoard decl = custom_spi_board();
    Sx126xBoardProfile p = *found;
    p.spi               = decl.pins;
    p.has_rxen          = decl.has_rxen;
    p.dio2_as_rf_switch = decl.dio2_as_rf_switch;
    p.dio3_tcxo         = decl.dio3_tcxo;
    p.tcxo_voltage      = decl.tcxo_voltage;
    p.max_chip_dbm      = decl.max_chip_dbm;
    p.pa_gain_db        = decl.pa_gain_db;
    p.min_out_dbm       = decl.min_out_dbm;
    p.max_out_dbm       = decl.max_out_dbm;
    return p;
}

std::uint8_t sx126x_bandwidth_code(std::uint32_t bandwidth_hz) {
    struct Entry { std::uint32_t hz; std::uint8_t code; };
    static constexpr Entry kTable[] = {
        {   7810, 0x00 }, {  10420, 0x08 }, {  15630, 0x01 }, {  20830, 0x09 },
        {  31250, 0x02 }, {  41670, 0x0A }, {  62500, 0x03 }, { 125000, 0x04 },
        { 250000, 0x05 }, { 500000, 0x06 },
    };
    std::uint8_t best = 0x04;
    std::uint32_t best_delta = 0xFFFFFFFFu;
    for (const auto& e : kTable) {
        const std::uint32_t delta = e.hz > bandwidth_hz ? e.hz - bandwidth_hz
                                                        : bandwidth_hz - e.hz;
        if (delta < best_delta) { best_delta = delta; best = e.code; }
    }
    return best;
}

void sx126x_sync_word_bytes(std::uint8_t sync_word, std::uint8_t& msb, std::uint8_t& lsb) {
    msb = static_cast<std::uint8_t>((sync_word & 0xF0) | 0x04);
    lsb = static_cast<std::uint8_t>(((sync_word & 0x0F) << 4) | 0x04);
}

std::int8_t sx126x_chip_power_dbm(const Sx126xBoardProfile& profile,
                                  std::int8_t requested_dbm) {
    const int chip = static_cast<int>(requested_dbm) - profile.pa_gain_db;
    return static_cast<std::int8_t>(
        std::clamp(chip, -9, static_cast<int>(profile.max_chip_dbm)));
}

bool sx126x_tx_frequency_permitted(std::uint64_t freq_hz,
                                   std::uint64_t band_min_hz,
                                   std::uint64_t band_max_hz,
                                   std::string& reason) {
    const auto mhz = [](std::uint64_t hz) {
        std::string s = std::to_string(static_cast<double>(hz) / 1e6);
        // to_string gives six decimals; three keeps the kHz digit that slot
        // frequencies land on (913.125) without the trailing noise.
        if (const auto dot = s.find('.'); dot != std::string::npos && s.size() > dot + 4)
            s.erase(dot + 4);
        return s + " MHz";
    };

    if (freq_hz < kSx126xMinFreqHz || freq_hz > kSx126xMaxFreqHz) {
        // Outside this the PLL simply will not lock, so the failure would
        // otherwise be a silent one: the radio reports TX_DONE having radiated
        // nothing. The 2.4 GHz region is the way to reach it by accident — that
        // band belongs to the SX1280, and no SX1262 stick can serve it.
        reason = mhz(freq_hz) + " is outside the SX1262's " +
                 mhz(kSx126xMinFreqHz) + " to " + mhz(kSx126xMaxFreqHz) +
                 " range — no SX1262 stick can transmit there";
        return false;
    }

    // A zero band is "not declared", not "nothing permitted": the chip range
    // above still applies, and a caller that never learned the region should
    // not be silently unable to transmit.
    if (band_min_hz == 0 && band_max_hz == 0) return true;

    if (freq_hz < band_min_hz || freq_hz > band_max_hz) {
        reason = mhz(freq_hz) + " is outside the selected region's " +
                 mhz(band_min_hz) + " to " + mhz(band_max_hz) +
                 " band — a stick's front end is built for one band, and "
                 "driving its PA outside it can damage the radio";
        return false;
    }
    return true;
}

double lora_airtime_seconds(const modem::LoraParams& params, std::size_t payload_len) {
    const double sf = params.spreading_factor;
    const double bw = params.bandwidth_hz > 0 ? params.bandwidth_hz : 125000.0;
    const double t_sym = std::pow(2.0, sf) / bw;
    const double de = params.low_data_rate_optimize ? 1.0 : 0.0;
    const double ih = params.explicit_header ? 0.0 : 1.0;
    const double crc = params.crc_enabled ? 1.0 : 0.0;
    const double cr = static_cast<double>(params.coding_rate) - 4.0;

    const double numerator =
        8.0 * static_cast<double>(payload_len) - 4.0 * sf + 28.0 + 16.0 * crc - 20.0 * ih;
    const double denominator = 4.0 * (sf - 2.0 * de);
    const double symbols =
        8.0 + std::max(0.0, std::ceil(numerator / denominator) * (cr + 4.0));

    const double preamble = (static_cast<double>(params.preamble_symbols) + 4.25) * t_sym;
    return preamble + symbols * t_sym;
}

// ---------------------------------------------------------------------------
// Transport helpers
// ---------------------------------------------------------------------------

bool Sx126xRadio::wait_busy(int timeout_ms, std::string& error) {
    const auto deadline =
        std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    for (;;) {
        bool busy = false;
        if (!bus_.read_pin(kSx126xPinBusy, busy)) {
            error = "SPI bus: could not read BUSY";
            return false;
        }
        if (!busy) return true;
        if (std::chrono::steady_clock::now() >= deadline) {
            error = "SX1262: BUSY stuck high for " + std::to_string(timeout_ms) + " ms";
            return false;
        }
        std::this_thread::sleep_for(std::chrono::microseconds(200));
    }
}

bool Sx126xRadio::command(std::uint8_t opcode, std::span<const std::uint8_t> params,
                          std::string& error) {
    if (!wait_busy(100, error)) return false;
    std::vector<std::uint8_t> tx;
    tx.reserve(params.size() + 1);
    tx.push_back(opcode);
    tx.insert(tx.end(), params.begin(), params.end());

    if (!bus_.write_pin(kSx126xPinCs, false)) { error = "SPI bus: CS assert failed"; return false; }
    const bool ok = bus_.transfer(tx, {});
    if (!bus_.write_pin(kSx126xPinCs, true)) { error = "SPI bus: CS release failed"; return false; }
    if (!ok) {
        error = "SPI bus: SPI write failed (opcode 0x" +
                std::string(1, "0123456789ABCDEF"[opcode >> 4]) +
                std::string(1, "0123456789ABCDEF"[opcode & 0x0F]) + ")";
        return false;
    }
    return true;
}

bool Sx126xRadio::command_read(std::uint8_t opcode, std::span<std::uint8_t> out,
                               std::string& error) {
    if (!wait_busy(100, error)) return false;
    // opcode, then one NOP that clocks out the status byte, then one NOP per
    // byte of payload.
    const std::size_t total = out.size() + 2;
    std::vector<std::uint8_t> tx(total, kNop);
    std::vector<std::uint8_t> rx(total, 0);
    tx[0] = opcode;

    if (!bus_.write_pin(kSx126xPinCs, false)) { error = "SPI bus: CS assert failed"; return false; }
    const bool ok = bus_.transfer(tx, rx);
    if (!bus_.write_pin(kSx126xPinCs, true)) { error = "SPI bus: CS release failed"; return false; }
    if (!ok) { error = "SPI bus: SPI read failed"; return false; }

    std::copy(rx.begin() + 2, rx.end(), out.begin());
    return true;
}

bool Sx126xRadio::write_register(std::uint16_t addr, std::span<const std::uint8_t> data,
                                 std::string& error) {
    std::vector<std::uint8_t> params;
    params.reserve(data.size() + 2);
    params.push_back(static_cast<std::uint8_t>(addr >> 8));
    params.push_back(static_cast<std::uint8_t>(addr & 0xFF));
    params.insert(params.end(), data.begin(), data.end());
    return command(kCmdWriteRegister, params, error);
}

bool Sx126xRadio::read_register(std::uint16_t addr, std::span<std::uint8_t> out,
                                std::string& error) {
    if (!wait_busy(100, error)) return false;
    // opcode, address high, address low, one NOP for the status byte, then one
    // NOP per byte read.
    const std::size_t total = out.size() + 4;
    std::vector<std::uint8_t> tx(total, kNop);
    std::vector<std::uint8_t> rx(total, 0);
    tx[0] = kCmdReadRegister;
    tx[1] = static_cast<std::uint8_t>(addr >> 8);
    tx[2] = static_cast<std::uint8_t>(addr & 0xFF);

    if (!bus_.write_pin(kSx126xPinCs, false)) { error = "SPI bus: CS assert failed"; return false; }
    const bool ok = bus_.transfer(tx, rx);
    if (!bus_.write_pin(kSx126xPinCs, true)) { error = "SPI bus: CS release failed"; return false; }
    if (!ok) { error = "SPI bus: register read failed"; return false; }

    std::copy(rx.begin() + 4, rx.end(), out.begin());
    return true;
}

bool Sx126xRadio::write_buffer(std::uint8_t offset, std::span<const std::uint8_t> data,
                               std::string& error) {
    std::vector<std::uint8_t> params;
    params.reserve(data.size() + 1);
    params.push_back(offset);
    params.insert(params.end(), data.begin(), data.end());
    return command(kCmdWriteBuffer, params, error);
}

bool Sx126xRadio::read_buffer(std::uint8_t offset, std::span<std::uint8_t> out,
                              std::string& error) {
    if (!wait_busy(100, error)) return false;
    // opcode, offset, one NOP that clocks out the status byte, then one NOP
    // per byte read.
    const std::size_t total = out.size() + 3;
    std::vector<std::uint8_t> tx(total, kNop);
    std::vector<std::uint8_t> rx(total, 0);
    tx[0] = kCmdReadBuffer;
    tx[1] = offset;

    if (!bus_.write_pin(kSx126xPinCs, false)) { error = "SPI bus: CS assert failed"; return false; }
    const bool ok = bus_.transfer(tx, rx);
    if (!bus_.write_pin(kSx126xPinCs, true)) { error = "SPI bus: CS release failed"; return false; }
    if (!ok) { error = "SPI bus: buffer read failed"; return false; }

    std::copy(rx.begin() + 3, rx.end(), out.begin());
    return true;
}

bool Sx126xRadio::modify_register(std::uint16_t addr, std::uint8_t clear_mask,
                                  std::uint8_t set_mask, std::string& error) {
    std::uint8_t value = 0;
    if (!read_register(addr, {&value, 1}, error)) return false;
    value = static_cast<std::uint8_t>((value & ~clear_mask) | set_mask);
    return write_register(addr, {&value, 1}, error);
}

// ---------------------------------------------------------------------------
// Radio helpers
// ---------------------------------------------------------------------------

bool Sx126xRadio::reset(std::string& error) {
    if (!bus_.write_pin(kSx126xPinCs, true) ||
        !bus_.write_pin(kSx126xPinReset, false)) {
        error = "SPI bus: could not drive NRST";
        return false;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(2));
    if (!bus_.write_pin(kSx126xPinReset, true)) {
        error = "SPI bus: could not release NRST";
        return false;
    }
    // The datasheet allows up to 3.5 ms for the internal boot sequence.
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    return wait_busy(200, error);
}

bool Sx126xRadio::set_standby(std::string& error) {
    const std::uint8_t p[] = {kStandbyRc};
    return command(kCmdSetStandby, p, error);
}

bool Sx126xRadio::get_irq_status(std::uint16_t& irq, std::string& error) {
    std::array<std::uint8_t, 2> buf{};
    if (!command_read(kCmdGetIrqStatus, buf, error)) return false;
    irq = static_cast<std::uint16_t>((buf[0] << 8) | buf[1]);
    return true;
}

bool Sx126xRadio::clear_irq_status(std::string& error) {
    const std::uint8_t p[] = {0xFF, 0xFF};
    return command(kCmdClearIrqStatus, p, error);
}

bool Sx126xRadio::check_device_errors(std::string& error) {
    std::array<std::uint8_t, 2> buf{};
    if (!command_read(kCmdGetDeviceErrors, buf, error)) return false;
    const std::uint16_t errs = static_cast<std::uint16_t>((buf[0] << 8) | buf[1]);
    if (errs == 0) return true;

    error = "SX1262 device errors 0x";
    for (int shift = 12; shift >= 0; shift -= 4)
        error += "0123456789ABCDEF"[(errs >> shift) & 0x0F];

    // Datasheet table 13-30. XOSC_START_ERR is the one that actually bites on
    // these sticks: it means DIO3 is not powering the TCXO at the right
    // voltage, or calibration ran before it had started.
    struct Bit { std::uint16_t mask; const char* name; };
    static constexpr Bit kBits[] = {
        {0x0001, "RC64K calibration"}, {0x0002, "RC13M calibration"},
        {0x0004, "PLL calibration"},   {0x0008, "ADC calibration"},
        {0x0010, "image calibration"}, {0x0020, "XOSC did not start (TCXO)"},
        {0x0040, "PLL did not lock"},  {0x0100, "PA ramp"},
    };
    const char* sep = " (";
    for (const auto& b : kBits) {
        if (!(errs & b.mask)) continue;
        error += sep;
        error += b.name;
        sep = ", ";
    }
    if (sep[0] == ',') error += ")";
    const std::uint8_t clear[] = {0x00, 0x00};
    std::string ignored;
    command(kCmdClearDeviceErrors, clear, ignored);
    return false;
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

bool Sx126xRadio::begin(std::string& error) {
    // Park the RF switch before anything else so a half-configured radio can
    // never key the PA into an unselected path.
    if (profile_.has_rxen && !bus_.write_pin(kSx126xPinRxen, false)) {
        error = "SPI bus: could not drive RXen";
        return false;
    }
    if (!reset(error)) return false;
    if (!set_standby(error)) return false;

    // Presence check: write the Meshtastic sync word and read it back. This
    // doubles as configuration, and distinguishes "no radio on the SPI bus"
    // (MISO stuck high or low) from a bridge that opened fine.
    std::uint8_t sync[2]{};
    sx126x_sync_word_bytes(0x2B, sync[0], sync[1]); // Meshtastic public network
    if (!write_register(kRegSyncWordMsb, sync, error)) return false;
    std::array<std::uint8_t, 2> readback{};
    if (!read_register(kRegSyncWordMsb, readback, error)) return false;
    if (readback[0] != sync[0] || readback[1] != sync[1]) {
        error = "SX1262 did not respond on SPI (sync word read back as 0x" +
                std::string(1, "0123456789ABCDEF"[readback[0] >> 4]) +
                std::string(1, "0123456789ABCDEF"[readback[0] & 0x0F]) +
                std::string(1, "0123456789ABCDEF"[readback[1] >> 4]) +
                std::string(1, "0123456789ABCDEF"[readback[1] & 0x0F]) +
                ") \xE2\x80\x94 wrong board selected, or not an SX1262 stick";
        return false;
    }

    if (profile_.dio3_tcxo) {
        // Startup delay is expressed in 15.625 us ticks; 5 ms is the value
        // every SX1262 module reference design uses.
        const std::uint32_t ticks = static_cast<std::uint32_t>(0.005 / kTickSeconds);
        const std::uint8_t p[] = {
            profile_.tcxo_voltage,
            static_cast<std::uint8_t>((ticks >> 16) & 0xFF),
            static_cast<std::uint8_t>((ticks >> 8) & 0xFF),
            static_cast<std::uint8_t>(ticks & 0xFF),
        };
        if (!command(kCmdSetDio3AsTcxoCtrl, p, error)) return false;

        // The chip tries to start its oscillator at reset, before DIO3 is
        // configured to power the TCXO, and latches XOSC_START_ERR (0x0020)
        // when that fails. On these sticks it always fails, because the TCXO
        // has no supply until the command above. Clear the stale error before
        // recalibrating, or the check below rejects a perfectly healthy radio.
        const std::uint8_t clear[] = {0x00, 0x00};
        if (!command(kCmdClearDeviceErrors, clear, error)) return false;

        // Everything calibrated before the TCXO was running has to be redone,
        // otherwise the first transmit lands off-frequency.
        const std::uint8_t cal[] = {0x7F};
        if (!command(kCmdCalibrate, cal, error)) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        if (!wait_busy(200, error)) return false;
        // Anything still set now is real: the TCXO genuinely did not come up.
        if (!check_device_errors(error)) return false;
    }

    if (profile_.dio2_as_rf_switch) {
        const std::uint8_t p[] = {0x01};
        if (!command(kCmdSetDio2AsRfSwitchCtrl, p, error)) return false;
    }

    const std::uint8_t pt[] = {kPacketTypeLora};
    if (!command(kCmdSetPacketType, pt, error)) return false;

    // Datasheet 15.2: raise the PA clamp threshold so an antenna mismatch
    // cannot damage the output stage. Applied once, survives standby.
    return modify_register(kRegTxClampConfig, 0x00, 0x1E, error);
}

bool Sx126xRadio::configure_phy(const PacketRadioConfig& cfg, std::uint8_t payload_len,
                                bool for_transmit, std::string& error) {
    if (!set_standby(error)) return false;

    // 1. Frequency, then image calibration for its band.
    {
        const std::uint32_t pll = frequency_to_pll(cfg.center_freq_hz);
        const std::uint8_t p[] = {
            static_cast<std::uint8_t>((pll >> 24) & 0xFF),
            static_cast<std::uint8_t>((pll >> 16) & 0xFF),
            static_cast<std::uint8_t>((pll >> 8) & 0xFF),
            static_cast<std::uint8_t>(pll & 0xFF),
        };
        std::uint8_t lo = 0, hi = 0;
        calibration_band(cfg.center_freq_hz, lo, hi);
        const std::uint8_t band[] = {lo, hi};
        if (!command(kCmdCalibrateImage, band, error)) return false;
        if (!command(kCmdSetRfFrequency, p, error)) return false;
    }

    // 2/3. PA and output power — transmit only. The +22 dBm PA setting is used
    //      at every power level and the output is trimmed with SetTxParams,
    //      which the datasheet permits; OCP has to be restored afterwards
    //      because SetPaConfig resets it. The power itself is converted from
    //      antenna dBm by subtracting the module's PA gain and clamping to
    //      what the SX1262 can actually be asked for.
    if (for_transmit) {
        const std::uint8_t pa[] = {0x04, 0x07, 0x00, 0x01};
        if (!command(kCmdSetPaConfig, pa, error)) return false;
        const std::uint8_t ocp[] = {kOcp140Ma};
        if (!write_register(kRegOcpConfig, ocp, error)) return false;

        const std::uint8_t p[] = {
            static_cast<std::uint8_t>(sx126x_chip_power_dbm(profile_, cfg.power_dbm)),
            kPaRamp200Us,
        };
        if (!command(kCmdSetTxParams, p, error)) return false;
    }

    // 4. Modulation, plus the 500 kHz workaround from datasheet 15.1.
    {
        const std::uint8_t bw = sx126x_bandwidth_code(cfg.params.bandwidth_hz);
        const std::uint8_t p[] = {
            cfg.params.spreading_factor,
            bw,
            static_cast<std::uint8_t>(cfg.params.coding_rate - 4),
            static_cast<std::uint8_t>(cfg.params.low_data_rate_optimize ? 1 : 0),
        };
        if (!command(kCmdSetModulationParams, p, error)) return false;
        // Bit 2 must be cleared for 500 kHz and set for every other bandwidth.
        const bool bw500 = (bw == 0x06);
        if (!modify_register(kRegTxModulation, bw500 ? 0x04 : 0x00,
                             bw500 ? 0x00 : 0x04, error))
            return false;
    }

    // 5. Packet layout.
    {
        const std::uint8_t p[] = {
            static_cast<std::uint8_t>((cfg.params.preamble_symbols >> 8) & 0xFF),
            static_cast<std::uint8_t>(cfg.params.preamble_symbols & 0xFF),
            static_cast<std::uint8_t>(cfg.params.explicit_header ? 0x00 : 0x01),
            payload_len,
            static_cast<std::uint8_t>(cfg.params.crc_enabled ? 0x01 : 0x00),
            0x00, // standard IQ; Meshtastic does not invert
        };
        if (!command(kCmdSetPacketParams, p, error)) return false;
    }

    // 6. Sync word. Re-applied per burst so a preset change cannot leave a
    //    stale value behind.
    {
        std::uint8_t p[2]{};
        sx126x_sync_word_bytes(cfg.params.sync_word, p[0], p[1]);
        if (!write_register(kRegSyncWordMsb, p, error)) return false;
    }

    const std::uint8_t base[] = {0x00, 0x00};
    return command(kCmdSetBufferBaseAddress, base, error);
}

bool Sx126xRadio::transmit(const PacketRadioConfig& cfg,
                           std::span<const std::uint8_t> payload,
                           std::string& error) {
    if (payload.empty()) { error = "empty payload"; return false; }
    if (payload.size() > 255) { error = "payload exceeds the 255-byte LoRa limit"; return false; }

    // Last gate before the PA is keyed, and deliberately here rather than in
    // configure_phy(): that is shared with enter_rx(), and off-band receive is
    // both harmless and useful. Checked in the driver rather than only in the
    // caller so no other transmit path — including the auto-reply hook, which
    // reaches Core::transmit() directly — can get around it.
    if (!sx126x_tx_frequency_permitted(cfg.center_freq_hz, cfg.tx_band_min_hz,
                                       cfg.tx_band_max_hz, error))
        return false;

    if (!configure_phy(cfg, static_cast<std::uint8_t>(payload.size()), true, error))
        return false;

    // 7. Payload.
    {
        if (!write_buffer(0x00, payload, error)) return false;
    }

    // 8. Interrupts. DIO1 is wired on these boards but we poll instead: a
    //    CH341 interrupt endpoint tops out around 400 Hz, and the status
    //    register is one SPI read away.
    {
        constexpr std::uint16_t kDone = kIrqTxDone | kIrqTimeout;
        const std::uint8_t p[] = {
            0xFF, 0xFF, // report everything in the status register
            static_cast<std::uint8_t>(kDone >> 8), static_cast<std::uint8_t>(kDone & 0xFF),
            0x00, 0x00,
            0x00, 0x00,
        };
        if (!command(kCmdSetDioIrqParams, p, error)) return false;
        if (!clear_irq_status(error)) return false;
    }

    // 9. Key the transmitter. DIO2 raises the module's TX enable by itself;
    //    RXen must be low so the receive path stays out of the way.
    const double airtime = lora_airtime_seconds(cfg.params, payload.size());
    if (profile_.has_rxen && !bus_.write_pin(kSx126xPinRxen, false)) {
        error = "SPI bus: could not drive RXen low for transmit";
        return false;
    }
    {
        // Hardware timeout at 3x airtime, so a wedged PA de-keys itself even
        // if the host loop is starved.
        const auto ticks = static_cast<std::uint32_t>(
            std::min(3.0 * airtime / kTickSeconds, 16'777'215.0));
        const std::uint8_t p[] = {
            static_cast<std::uint8_t>((ticks >> 16) & 0xFF),
            static_cast<std::uint8_t>((ticks >> 8) & 0xFF),
            static_cast<std::uint8_t>(ticks & 0xFF),
        };
        if (!command(kCmdSetTx, p, error)) return false;
    }

    // 10. Wait for the burst. Poll rate scales with airtime so a 15 ms
    //     ShortTurbo frame is not waited on at the same granularity as a
    //     two-second LongSlow one.
    const auto poll = std::chrono::microseconds(
        std::max<long long>(1000, static_cast<long long>(airtime * 1e6 / 50.0)));
    const auto deadline = std::chrono::steady_clock::now() +
                          std::chrono::milliseconds(
                              static_cast<long long>(airtime * 1000.0 * 4.0) + 1000);

    bool sent = false;
    for (;;) {
        std::uint16_t irq = 0;
        if (!get_irq_status(irq, error)) break;
        if (irq & kIrqTxDone) { sent = true; break; }
        if (irq & kIrqTimeout) {
            error = "SX1262: transmit timed out in hardware";
            break;
        }
        if (std::chrono::steady_clock::now() >= deadline) {
            error = "SX1262: no TX_DONE after " +
                    std::to_string(static_cast<int>(airtime * 1000.0 * 4.0) + 1000) + " ms";
            break;
        }
        std::this_thread::sleep_for(poll);
    }

    // 11. Always de-key, even on the error paths above.
    std::string ignored;
    clear_irq_status(ignored);
    set_standby(ignored);
    if (profile_.has_rxen) bus_.write_pin(kSx126xPinRxen, false);

    if (sent && !check_device_errors(error)) return false;
    return sent;
}

bool Sx126xRadio::idle(std::string& error) {
    const bool ok = set_standby(error);
    if (profile_.has_rxen) bus_.write_pin(kSx126xPinRxen, false);
    return ok;
}

bool Sx126xRadio::enter_rx(const PacketRadioConfig& cfg, std::string& error) {
    // 255 rather than a real length: in explicit-header mode the received size
    // comes from the header, and this field is only the implicit-mode fixed
    // length. Passing the maximum keeps any legal frame acceptable.
    if (!configure_phy(cfg, 255, false, error)) return false;

    {
        constexpr std::uint16_t kWanted = kIrqRxDone | kIrqCrcErr | kIrqHeaderErr;
        const std::uint8_t p[] = {
            0xFF, 0xFF, // everything visible in the status register
            static_cast<std::uint8_t>(kWanted >> 8), static_cast<std::uint8_t>(kWanted & 0xFF),
            0x00, 0x00,
            0x00, 0x00,
        };
        if (!command(kCmdSetDioIrqParams, p, error)) return false;
    }
    if (!clear_irq_status(error)) return false;

    // RF switch the other way round from transmit: the receive path has to be
    // enabled, and DIO2 drops the transmit side by itself.
    if (profile_.has_rxen && !bus_.write_pin(kSx126xPinRxen, true)) {
        error = "SPI bus: could not drive RXen high for receive";
        return false;
    }

    const std::uint8_t p[] = {
        static_cast<std::uint8_t>((kRxContinuous >> 16) & 0xFF),
        static_cast<std::uint8_t>((kRxContinuous >> 8) & 0xFF),
        static_cast<std::uint8_t>(kRxContinuous & 0xFF),
    };
    return command(kCmdSetRx, p, error);
}

bool Sx126xRadio::poll_rx(ReceivedPacket& out, bool& got, std::string& error) {
    got = false;

    std::uint16_t irq = 0;
    if (!get_irq_status(irq, error)) return false;
    if (!(irq & (kIrqRxDone | kIrqCrcErr | kIrqHeaderErr))) return true; // nothing yet

    // A bad CRC or header means the radio already knows the frame is corrupt.
    // There is nothing to hand up — unlike the software demodulator, which can
    // still show the raw bytes — so drop it and keep receiving.
    if (irq & (kIrqCrcErr | kIrqHeaderErr)) {
        clear_irq_status(error);
        return true;
    }

    std::array<std::uint8_t, 2> status{};
    if (!command_read(kCmdGetRxBufferStatus, status, error)) return false;
    const std::uint8_t length = status[0];
    const std::uint8_t offset = status[1];

    if (length == 0) {
        clear_irq_status(error);
        return true;
    }

    out.payload.assign(length, 0);
    if (!read_buffer(offset, out.payload, error)) return false;

    // Real numbers from the radio, not estimates off an IQ stream: RSSI is in
    // half-dBm steps and SNR in quarter-dB steps, both negated per the
    // datasheet's packet-status encoding.
    std::array<std::uint8_t, 3> pkt{};
    if (!command_read(kCmdGetPacketStatus, pkt, error)) return false;
    out.rssi_dbm = -static_cast<float>(pkt[0]) / 2.0f;
    out.snr_db   = static_cast<float>(static_cast<std::int8_t>(pkt[1])) / 4.0f;

    if (!clear_irq_status(error)) return false;
    got = true;
    return true;
}

} // namespace mrf::hal
