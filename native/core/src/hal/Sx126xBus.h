// SPDX-License-Identifier: GPL-3.0-or-later
//
// The bus an SX126x hangs off: full-duplex SPI plus a handful of discrete
// lines. Everything the radio driver needs from the world below it.
//
// Two transports implement this:
//   * A CH341A USB-to-SPI bridge (Ch341Bus.h) — how the MeshStick and MeshToad
//     USB sticks attach a radio to a machine with no SPI bus of its own.
//   * Linux spidev + the GPIO character device (SpiDevBus.h) — how every
//     Raspberry Pi LoRa HAT and the uConsole AIO V2 attach one, wired straight
//     to the host's own SPI controller.
//
// The interface is deliberately narrow: assert/deassert CS explicitly and push
// bytes, rather than letting the transport manage CS per call. A single SX126x
// command (opcode + operands, or opcode + 255-byte buffer write) has to stay
// inside one CS assertion, and on the CH341 that means holding CS across
// several USB transfers.
//
// One invariant the transports rely on, and Sx126x.cpp maintains: every CS
// assertion brackets exactly one transfer() call. A transport whose CS is
// driven by the kernel rather than by us — spidev with hardware chip-select —
// is only correct because of that, since it can do nothing at write_pin(CS)
// time but let the next transfer carry its own CS pulse.

#pragma once

#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace mrf::hal {

// Logical pin identities, named for what a LoRa board wires them to. The
// values are the CH341's own D-pin indices, because that transport addresses
// its pins by number and there is no reason to make it translate; a transport
// with unrelated wiring (spidev + GPIO lines) maps these onto whatever it has.
enum : std::uint8_t {
    kSx126xPinCs    = 0,
    kSx126xPinRxen  = 1,
    kSx126xPinReset = 2,
    kSx126xPinBusy  = 4,
    kSx126xPinDio1  = 6,
};

class Sx126xBus {
public:
    virtual ~Sx126xBus() = default;

    // Human-readable identity, e.g. "CH341 #0 serial 00439056" or
    // "spidev1.0 + gpiochip0".
    virtual std::string describe() const = 0;

    // Stable identifier for this particular bus, surfaced as the device
    // serial. The CH341's EEPROM serial, which is the only thing telling two
    // otherwise identical sticks apart; the spidev path for a soldered-down
    // radio, which has no serial and cannot be confused with a second one.
    // Empty when the transport has nothing to offer.
    virtual std::string serial() const = 0;

    // Drive one of the output pins (CS, RXEN, RESET).
    virtual bool write_pin(std::uint8_t pin, bool high) = 0;

    // Sample one of the input pins (BUSY, DIO1).
    virtual bool read_pin(std::uint8_t pin, bool& high) = 0;

    // Full-duplex SPI. `tx` and `rx` must be the same length; `rx` may be
    // empty to discard the response. CS is *not* touched — bracket calls with
    // write_pin(kSx126xPinCs, ...). Transfers longer than the transport's
    // packet size are split internally, which is safe because SPI is
    // synchronous and the SX126x has no inter-byte timing requirement.
    virtual bool transfer(std::span<const std::uint8_t> tx,
                          std::span<std::uint8_t> rx) = 0;
};

} // namespace mrf::hal
