// SPDX-License-Identifier: GPL-3.0-or-later
//
// Packet-level radio: a hardware LoRa modem rather than an SDR.
//
// The backends in RadioDevice.h are IQ-shaped — MeshRF modulates and
// demodulates LoRa in software and streams complex samples at the device rate.
// An SX1262 behind a CH341 USB-SPI bridge cannot be expressed that way: it
// takes and returns whole frames, doing preamble, sync, FEC and chirping
// itself. So it gets its own, much smaller interface rather than being bent
// into IRadioDevice.
//
// A single stick serves both directions, half-duplex, the way a real
// Meshtastic node does: receive continuously, break off to transmit, return to
// receive. That is the configuration for someone who owns one LoRa stick and
// no SDR — at the cost of the spectrum, waterfall, packet spectrogram and IQ
// capture, none of which a hardware modem can produce. Pairing an SDR receiver
// with this transmitter keeps all of that and is still the better setup when
// the hardware is available.

#pragma once

#include "mrf/hal/RadioDevice.h"
#include "mrf/modem/Preset.h"

#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace mrf::hal {

// Supported SX126x boards, across both transports. The two USB sticks
// enumerate as VID 0x1A86 / PID 0x5512 with an identical pin map and report no
// distinguishing product string, so they cannot be told apart at runtime.
// Values are part of the C ABI (mirrored by the managed Sx1262Board enum) —
// append only, never renumber.
enum class Sx126xBoard : int {
    MeshStick = 0, // Elecrow MeshStick: bare SX1262, 22 dBm at the antenna
    MeshToad  = 1, // NullHop/muzi MeshToad V3: SX1262 + E22P-915M30S PA, 30 dBm
    // No board chosen yet. The radio refuses to open in this state, so a user
    // who never touched the picker cannot transmit under a guessed power
    // model. Guessing is silently wrong in the dangerous direction — a
    // MeshToad driven as a MeshStick radiates ~8 dB more than the UI claims —
    // so the choice is made explicit instead.
    Unspecified = 2,
    // ClockworkPi uConsole with the HackerGadgets AIO V2 expansion: a bare
    // SX1262 on SPI1, no front end. Pin map from the board's own meshtasticd
    // config (spidev1.0, BUSY 24, NRST 25, DIO1 26, DIO2 as RF switch, DIO3
    // feeding the TCXO). Note the AIO V2 gates the radio behind GPIO 16 —
    // MeshRF will not find it until the board's own enable has been set.
    UConsoleAio = 3,
    // Any other SX126x on a host SPI bus: a Raspberry Pi HAT, a custom build.
    // Both the pin map and the power model come from the operator via
    // set_custom_spi_board(), because neither can be probed and a guessed PA
    // gain is wrong in the direction that over-radiates. See the HAT pin maps
    // in the README.
    CustomSpi = 4,
};

// Highest valid Sx126xBoard, for range-checking values arriving over the C
// ABI. Update when appending to the enum above.
inline constexpr int kSx126xBoardMax = static_cast<int>(Sx126xBoard::CustomSpi);

// Where a board's SX126x is wired, for the boards reached over the host's own
// SPI bus rather than a USB bridge. Line offsets are the numbers the GPIO chip
// uses, which on a Raspberry Pi are the BCM numbers meshtasticd configs quote.
struct Sx126xSpiPins {
    std::string   spidev{"spidev0.0"};   // under /dev
    std::string   gpiochip{"gpiochip0"}; // under /dev
    std::uint32_t speed_hz{2'000'000};   // SX126x tolerates 16 MHz; 2 is meshtasticd's
    int           cs{-1};                // -1 = the controller's own chip select
    int           busy{-1};
    int           reset{-1};
    int           dio1{-1};
    int           rxen{-1};              // -1 = board has no RXEN line

    // A pin map is usable once the three lines the driver cannot work without
    // are present. RXEN is genuinely optional (boards that let DIO2 run the RF
    // switch have none) and CS legitimately defaults to the controller's.
    bool complete() const { return busy >= 0 && reset >= 0 && dio1 >= 0; }
};

// The operator's description of a board MeshRF ships no preset for: where it
// is wired, and what its front end does to the chip's output. Defaults
// describe a bare SX1262 with nothing after it, which is the only power model
// that can be assumed without risk of under-reporting.
struct Sx126xCustomSpiBoard {
    Sx126xSpiPins pins;
    bool          has_rxen{false};
    bool          dio2_as_rf_switch{true};
    bool          dio3_tcxo{true};
    std::uint8_t  tcxo_voltage{0x02};
    std::int8_t   max_chip_dbm{22};
    std::int8_t   pa_gain_db{0};
    std::int8_t   min_out_dbm{-9};
    std::int8_t   max_out_dbm{22};
};

// Declare the Custom SPI board's wiring and power model. Takes effect on the
// next open; a radio already running keeps the model it opened under.
void set_custom_spi_board(const Sx126xCustomSpiBoard& board);
Sx126xCustomSpiBoard custom_spi_board();

// PHY settings for a burst or a receive session.
struct PacketRadioConfig {
    std::uint64_t     center_freq_hz{915'000'000};
    modem::LoraParams params{modem::params_for(modem::Preset::LongFast)};
    // Requested power at the antenna port, in dBm. The backend subtracts any
    // external PA gain and clamps to the board's range. Ignored on receive.
    std::int8_t       power_dbm{22};
    // Band the operator declared by choosing a region, in Hz. Transmit is
    // refused outside it: a stick's front end — matching network, filter, PA —
    // is built for one band and cannot be read back over SPI, so the region is
    // the only statement of it available, and driving a band-limited PA far
    // off-band is the one mistake here that damages hardware rather than
    // merely wasting power.
    //
    // Receive ignores both: listening off-band keys no PA, and being able to
    // do it is how you find where a stick's filtering actually rolls off.
    //
    // Zero means no band was declared, leaving only the chip's own range to
    // enforce. Callers that know the region should always set these.
    std::uint64_t     tx_band_min_hz{0};
    std::uint64_t     tx_band_max_hz{0};
};

// One frame off the air. The radio has already checked the CRC and stripped
// the PHY layer, so `payload` is the Meshtastic on-air byte stream — the same
// thing the software demodulator hands up.
struct ReceivedPacket {
    std::vector<std::uint8_t> payload;
    float rssi_dbm{};
    float snr_db{};
};

using PacketRxCallback = std::function<void(const ReceivedPacket&)>;

// A radio that speaks framed bytes rather than IQ.
class IPacketRadio {
public:
    virtual ~IPacketRadio() = default;

    virtual DeviceInfo info() const = 0;
    virtual DeviceKind kind() const = 0;

    // Begin receiving. Delivers packets on a private thread until stop_rx().
    // Returns false with `error` set if the radio could not be configured.
    virtual bool start_rx(const PacketRadioConfig& cfg, PacketRxCallback cb,
                          std::string& error) = 0;
    virtual void stop_rx() = 0;
    [[nodiscard]] virtual bool is_rx_running() const = 0;

    // Send one Meshtastic frame (16-byte L1 header + encrypted payload, exactly
    // what Core::transmit() hands the software modulator). Blocks until the
    // radio reports TX_DONE. If a receive session is running it is suspended
    // for the burst and resumed afterwards — the radio is half-duplex.
    virtual bool transmit(const PacketRadioConfig& cfg,
                          std::span<const std::uint8_t> payload,
                          std::string& error) = 0;

    // Selectable antenna-port power range in dBm, for the UI to bound its
    // control. Differs per board because of the external PA.
    virtual std::int8_t min_power_dbm() const = 0;
    virtual std::int8_t max_power_dbm() const = 0;
};

// Open the SX1262 stick for `board`. `serial` picks a specific stick when
// several are attached; empty takes the first that answers. Returns nullptr
// when no CH341 can be claimed, the requested serial is absent, the radio does
// not answer, or `board` is Unspecified; the reason is in packet_radio_status().
std::unique_ptr<IPacketRadio> open_packet_radio(Sx126xBoard board,
                                                const std::string& serial);

// True when the CH341 transport is usable on this machine at all (the WCH
// CH341DLL loads on Windows, or libusb is present elsewhere). Does not require
// hardware, so the UI can offer the device before it is plugged in.
bool packet_radio_available();

// Serial numbers of every attached CH341 stick, for the device picker. Empty
// when none are present or the backend cannot load. Cheap enough to poll when
// a dropdown opens; it claims and releases each device in turn, so do not call
// it while a radio is open.
std::vector<std::string> list_packet_radio_serials();

// Diagnostic from the most recent open_packet_radio() call.
const char* packet_radio_status();

// Antenna-port power range for a board, available without opening the device.
void packet_radio_power_range(Sx126xBoard board, std::int8_t& min_dbm, std::int8_t& max_dbm);

} // namespace mrf::hal
