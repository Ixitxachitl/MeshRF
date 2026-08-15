// SPDX-License-Identifier: GPL-3.0-or-later
//
// Packet-level transmit device.
//
// The SDR backends in RadioDevice.h are IQ-shaped: MeshRF modulates a LoRa
// frame in software and streams complex samples at the device rate. A hardware
// LoRa modem (SX1262 behind a CH341 USB-SPI bridge) cannot be expressed that
// way — it takes the on-air byte stream and does preamble/sync/FEC/chirping
// itself. So it gets its own, much smaller interface rather than being bent
// into IRadioDevice.
//
// This is deliberately TX-only. MeshRF's identity is software demodulation
// with a live spectrum, and an SX1262 receiver would darken the waterfall,
// the packet spectrogram and the IQ capture. Pairing an SDR receiver with a
// hardware transmitter keeps all of that and fixes the weak leg: HackRF TX is
// ~10 dBm of unfiltered wideband output, where these sticks put out 22 dBm
// (MeshStick) or 30 dBm (MeshToad) through a matched front end.

#pragma once

#include "mrf/hal/RadioDevice.h"
#include "mrf/modem/Preset.h"

#include <cstdint>
#include <memory>
#include <span>
#include <string>

namespace mrf::hal {

// Supported CH341+SX126x USB sticks. Both enumerate as VID 0x1A86 / PID 0x5512
// with an identical pin map, so they cannot be told apart over USB alone; the
// user selects which one is plugged in. Values are part of the C ABI (mirrored
// by the managed Sx1262Board enum) — append only, never renumber.
enum class Sx126xBoard : int {
    MeshStick = 0, // Elecrow MeshStick: bare SX1262, 22 dBm at the antenna
    MeshToad  = 1, // NullHop/muzi MeshToad V3: SX1262 + E22P-915M30S PA, 30 dBm
    // No board chosen yet. The transmitter refuses to open in this state, so a
    // user who never touched the picker cannot transmit under a guessed power
    // model. This exists because the two boards cannot be told apart at
    // runtime: they share USB IDs, and the sticks report no product string to
    // key off (verified against the CH341 driver's device properties). Guessing
    // is silently wrong in the dangerous direction — a MeshToad driven as a
    // MeshStick radiates ~8 dB more than the UI claims — so the choice is made
    // explicit instead.
    Unspecified = 2,
};

// Everything the radio needs for one burst. `params` is MeshRF's canonical PHY
// description, translated to SX126x register values by the backend.
struct PacketTxConfig {
    std::uint64_t     center_freq_hz{915'000'000};
    modem::LoraParams params{modem::params_for(modem::Preset::LongFast)};
    // Requested power at the antenna port, in dBm. The backend maps this onto
    // the chip's SetTxParams value, subtracting any external PA gain, and
    // clamps to the board's range.
    std::int8_t       power_dbm{22};
};

// A radio that accepts framed bytes rather than IQ.
class IPacketTxDevice {
public:
    virtual ~IPacketTxDevice() = default;

    virtual DeviceInfo info() const = 0;
    virtual DeviceKind kind() const = 0;

    // Send one Meshtastic frame (16-byte L1 header + encrypted payload, exactly
    // what Core::transmit() hands the software modulator). Blocks until the
    // radio reports TX_DONE or the burst times out. Returns false and fills
    // `error` with a human-readable reason on failure.
    virtual bool transmit(const PacketTxConfig& cfg,
                          std::span<const std::uint8_t> payload,
                          std::string& error) = 0;

    // Selectable antenna-port power range, in dBm, for the UI to bound its
    // control. Differs per board because of the external PA.
    virtual std::int8_t min_power_dbm() const = 0;
    virtual std::int8_t max_power_dbm() const = 0;
};

// Open the SX1262 stick for `board`. Returns nullptr when no CH341 device can
// be claimed or the radio does not answer; the reason is recorded in
// packet_tx_status().
std::unique_ptr<IPacketTxDevice> open_packet_tx_device(Sx126xBoard board);

// True when the CH341 transport is usable on this machine at all (the WCH
// CH341DLL loads on Windows, or libusb is present elsewhere). Does not require
// hardware to be connected, so the UI can offer the device before it is
// plugged in.
bool packet_tx_available();

// Diagnostic from the most recent open_packet_tx_device() call, e.g.
// "MeshStick on CH341 #0" or "no CH341 device found (index 0..7)".
const char* packet_tx_status();

// Antenna-port power range for a board, available without opening the device
// so the UI can bound its control before anything is connected.
void packet_tx_power_range(Sx126xBoard board, std::int8_t& min_dbm, std::int8_t& max_dbm);

} // namespace mrf::hal
