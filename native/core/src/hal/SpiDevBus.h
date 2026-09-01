// SPDX-License-Identifier: GPL-3.0-or-later
//
// Linux spidev + GPIO transport for an SX126x wired to the host's own SPI
// controller. Implements Sx126xBus.
//
// This is how a radio attaches to a single-board computer rather than to a USB
// port: SPI through /dev/spidevB.D, and the four discrete lines (BUSY, DIO1,
// NRST, optionally RXEN) through the GPIO character device. It is the same
// wiring meshtasticd drives, so a board with a meshtasticd config already has
// its pin map written down — see kProfiles in Sx126x.cpp.
//
// GPIO goes through /dev/gpiochipN ioctls directly rather than libgpiod. The
// v2 line uAPI has been stable since Linux 5.10 and needs no library at all,
// where libgpiod would be this codebase's first hard native dependency and
// ships two mutually incompatible APIs (v1 and v2) across the distributions
// we would have to build on. The sysfs GPIO interface, the other option, has
// been deprecated since 4.8.
//
// Chip select works one of two ways, chosen per board:
//   * cs < 0 — the SPI controller drives its own hardware chip-select line,
//     which is the usual wiring (the radio sits on CE0). write_pin(CS) then
//     does nothing and each transfer carries its own CS pulse. Correct only
//     because every CS assertion in Sx126x.cpp brackets exactly one transfer;
//     see the invariant documented in Sx126xBus.h.
//   * cs >= 0 — the board wires CS to an ordinary GPIO instead (a second
//     radio on one bus, or a HAT that simply routed it elsewhere). spidev is
//     then opened with SPI_NO_CS and we drive the line ourselves.

#pragma once

#include "Sx126xBus.h"
#include "mrf/hal/PacketRadio.h"

#include <memory>
#include <string>

namespace mrf::hal {

// Open the radio's bus. Returns nullptr with `status` set when the spidev node
// or GPIO chip is missing, a line is already claimed by another process
// (meshtasticd holding the radio is the usual cause), or the caller lacks
// permission. Returns nullptr on every non-Linux platform.
std::unique_ptr<Sx126xBus> open_spidev(const Sx126xSpiPins& pins, std::string& status);

// True when this platform can talk spidev at all. Compile-time on non-Linux;
// on Linux it reports whether /dev/spidev* exists, since the SPI overlay being
// switched off in config.txt is the single most common reason a correctly
// wired board goes missing.
bool spidev_backend_available();

} // namespace mrf::hal
