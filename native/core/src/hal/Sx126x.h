// SPDX-License-Identifier: GPL-3.0-or-later
//
// Semtech SX126x command-level driver, transmit path only.
//
// Written directly against the datasheet rather than vendoring RadioLib: we
// need one direction of one modem, and RadioLib would drag in an Arduino-style
// HAL shim for millis()/pinMode()/SPI just to reach the same dozen opcodes.
//
// Reference: SX1261/2 datasheet rev 2.1, chapters 11 (commands), 13 (LoRa
// parameters) and 15 (known-limitation workarounds). The two errata in
// chapter 15 both matter here — 15.1 affects every 500 kHz preset
// (ShortTurbo/LongTurbo/MediumTurbo) and 15.2 protects the PA against antenna
// mismatch, which is a real risk on a USB stick whose antenna the user screws
// on themselves.
#pragma once

#include "Ch341Transport.h"
#include "mrf/hal/PacketRadio.h"

#include <cstdint>
#include <span>
#include <string>

namespace mrf::hal {

// Per-board wiring and power model. Both supported sticks share a pin map —
// verified against lora-usb-meshstick-1262.yaml and lora-usb-meshtoad-e22.yaml
// in meshtastic/firmware, which are byte-for-byte identical apart from
// comments — so only the power model actually differs.
struct Sx126xBoardProfile {
    Sx126xBoard  board;
    const char*  name;
    bool         has_rxen;           // RF-switch receive enable on D1
    bool         dio2_as_rf_switch;  // DIO2 drives the transmit side directly
    bool         dio3_tcxo;          // DIO3 supplies the TCXO
    std::uint8_t tcxo_voltage;       // SetDIO3AsTCXOCtrl code (0x02 = 1.8 V)
    std::int8_t  max_chip_dbm;       // ceiling written to SetTxParams
    // Antenna power minus chip power. Zero on a bare SX1262; the MeshToad's
    // E22P-915M30S front end adds roughly this much, which is why the number
    // shown in the UI is not the number programmed into the radio.
    std::int8_t  pa_gain_db;
    std::int8_t  min_out_dbm;
    std::int8_t  max_out_dbm;
};

const Sx126xBoardProfile& sx126x_profile(Sx126xBoard board);

// Time-on-air in seconds for a LoRa frame, per the SX1276/SX126x formula.
// Exposed for tests and used to bound how long a burst is waited on.
double lora_airtime_seconds(const modem::LoraParams& params, std::size_t payload_len);

// Maps a bandwidth in Hz onto the SX126x LoRa bandwidth code, choosing the
// nearest supported step (MeshRF's TinyFast/TinySlow presets say 15600 Hz
// where the chip's step is 15630).
std::uint8_t sx126x_bandwidth_code(std::uint32_t bandwidth_hz);

// Encodes an 8-bit LoRa sync word into the two register bytes at 0x0740.
// Meshtastic's 0x2B becomes 0x24 0xB4, the same way the well-known public
// LoRaWAN 0x34 becomes 0x34 0x44.
void sx126x_sync_word_bytes(std::uint8_t sync_word, std::uint8_t& msb, std::uint8_t& lsb);

// Converts a requested antenna-port power into the value to program into
// SetTxParams: subtract the module's PA gain, then clamp to what the chip can
// be asked for. This is where a MeshToad's 30 dBm becomes the chip's 22.
std::int8_t sx126x_chip_power_dbm(const Sx126xBoardProfile& profile, std::int8_t requested_dbm);

// Drives one SX126x over a CH341 bridge. Not thread-safe; Core serializes
// transmits behind its own lock.
class Sx126xRadio {
public:
    Sx126xRadio(Ch341Transport& bus, const Sx126xBoardProfile& profile)
        : bus_(bus), profile_(profile) {}

    // Reset, verify the radio answers, and apply the board-level settings
    // (TCXO, RF switch, packet type) that do not change between bursts.
    bool begin(std::string& error);

    // Configure the PHY for this burst, load the payload and key the
    // transmitter, blocking until TX_DONE. Returns false with `error` set on
    // any SPI failure, device error, or timeout.
    bool transmit(const PacketRadioConfig& cfg,
                  std::span<const std::uint8_t> payload,
                  std::string& error);

    // Put the radio into continuous receive on `cfg`. Call poll_rx() to drain
    // it. Separate from poll_rx() so the caller owns the thread and can drop
    // out of receive to transmit.
    bool enter_rx(const PacketRadioConfig& cfg, std::string& error);

    // Check for a completed frame. Returns true and fills `out` when one
    // arrived, false when nothing is ready (in which case `error` is empty) or
    // on failure (`error` set). Frames failing the hardware CRC are dropped
    // and reported as "nothing ready" — the radio has already told us they are
    // corrupt, and there is nothing useful to hand up.
    bool poll_rx(ReceivedPacket& out, bool& got, std::string& error);

    // Drop out of receive or transmit into standby and park the RF switch.
    // Safe to call when the radio is already idle.
    bool idle(std::string& error);

private:
    // --- transport helpers ---------------------------------------------
    bool wait_busy(int timeout_ms, std::string& error);
    bool command(std::uint8_t opcode, std::span<const std::uint8_t> params,
                 std::string& error);
    bool command_read(std::uint8_t opcode, std::span<std::uint8_t> out,
                      std::string& error);
    bool write_register(std::uint16_t addr, std::span<const std::uint8_t> data,
                        std::string& error);
    bool read_register(std::uint16_t addr, std::span<std::uint8_t> out,
                       std::string& error);
    bool write_buffer(std::uint8_t offset, std::span<const std::uint8_t> data,
                      std::string& error);
    bool read_buffer(std::uint8_t offset, std::span<std::uint8_t> out,
                     std::string& error);
    bool modify_register(std::uint16_t addr, std::uint8_t clear_mask,
                         std::uint8_t set_mask, std::string& error);

    // Applies frequency, PA, modulation, packet layout and sync word. Shared
    // by transmit() and enter_rx() so the two directions can never drift apart
    // on the PHY — a receiver configured even slightly differently from the
    // transmitter simply hears nothing, with no error to explain it.
    bool configure_phy(const PacketRadioConfig& cfg, std::uint8_t payload_len,
                       bool for_transmit, std::string& error);

    // --- radio helpers -------------------------------------------------
    bool reset(std::string& error);
    bool set_standby(std::string& error);
    bool get_irq_status(std::uint16_t& irq, std::string& error);
    bool clear_irq_status(std::string& error);
    bool check_device_errors(std::string& error);

    Ch341Transport&           bus_;
    const Sx126xBoardProfile& profile_;
};

} // namespace mrf::hal
