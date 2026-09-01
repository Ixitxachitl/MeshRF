// SPDX-License-Identifier: GPL-3.0-or-later
//
// CH341A USB-to-SPI transport for an SX126x, as used by the MeshStick /
// MeshToad LoRa sticks. Implements Sx126xBus.
//
// Pin map (WCH CH341SetStream single-I/O SPI mode; confirmed against every
// lora-usb-*.yaml in meshtastic/firmware, which drive these same boards):
//
//   D0  CS0    chip select, active low
//   D1  CS1    free -> RXen (RF switch receive enable)
//   D2  CS2    free -> NRST (radio reset, active low)
//   D3  DCK    SPI clock          (owned by the CH341 SPI engine)
//   D4  DOUT2  free, input        -> BUSY
//   D5  DOUT   SPI MOSI           (owned by the CH341 SPI engine)
//   D6  DIN2   free, input only   -> DIO1 / IRQ
//   D7  DIN    SPI MISO           (owned by the CH341 SPI engine)
//
// D4 and D6 are only used by the CH341's dual-I/O SPI mode, which we do not
// select, so they are free as GPIO — which is exactly why the boards put BUSY
// and DIO1 there. The Sx126xBus pin constants are these same indices, so this
// transport addresses its pins without translating.
//
// Two backends implement it:
//   * Windows — WCH's CH341DLL.DLL from the CH341PAR package, loaded at
//     runtime (Ch341DynLoad). This is the driver already bound to the device
//     on a machine that has run the WCH installer, so it works without
//     re-binding anything with Zadig.
//   * Linux/macOS — libusb, speaking the CH341 vendor protocol directly, the
//     same way flashrom's ch341a_spi and pine64's libch341-spi-userspace do.

#pragma once

#include "Sx126xBus.h"

#include <cstddef>
#include <memory>
#include <string>
#include <vector>

namespace mrf::hal {

// Largest SPI chunk pushed in one call to the bridge. The CH341 moves SPI in
// 32-byte USB packets; staying under that keeps every chunk a single packet.
// Chunking is only safe because callers hold CS across the whole logical
// command — a bridge that dropped CS between packets is what broke long
// writes to the SX1262 for meshtasticd (meshtastic/firmware#3799).
inline constexpr std::size_t kCh341SpiChunk = 28;

// Open a CH341 bridge. `serial` selects a specific stick when several are
// attached; empty takes the first that answers. Returns nullptr when none can
// be claimed or the requested serial is absent; `status` is filled with a
// human-readable reason either way. Implemented once per platform
// (Ch341Windows.cpp / Ch341LibUsb.cpp).
std::unique_ptr<Sx126xBus> open_ch341(const std::string& serial, std::string& status);

// Serial numbers of every attached CH341, for a device picker. These come from
// the 24C02 EEPROM each stick carries — the only thing that distinguishes one
// from another, since they all share VID 0x1A86 / PID 0x5512 and report no
// product string. Claims and releases each device in turn, so do not call this
// while a radio is open.
std::vector<std::string> list_ch341_serials();

// True when this platform's CH341 backend can be loaded at all, without
// requiring hardware to be plugged in.
bool ch341_backend_available();

} // namespace mrf::hal
