// SPDX-License-Identifier: GPL-3.0-or-later
//
// Runtime loader for WCH's CH341DLL, shipped in the CH341PAR driver package.
// Same approach as HackRfDynLoad: we don't need CH341DLL.H or CH341DLL.LIB at
// build time, so declare the handful of entry points we use, LoadLibrary the
// DLL and GetProcAddress each one.
//
// Why the vendor DLL rather than libusb here: on Windows the CH341PAR
// installer binds its own WDM driver (service CH341_A64) to the stick, and
// libusb can only claim devices bound to WinUSB/libusbK/libusb0. Anyone who
// has set one of these sticks up for meshtasticd already has CH341PAR
// installed, so going through the DLL works against that existing binding
// instead of requiring a Zadig re-bind that would break their other tools.
#pragma once

#if defined(_WIN32)

#include <cstdint>

#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace mrf::hal::ch341_dyn {

// CH341SetStream mode bits.
//   bit 1-0  I2C clock (unused for SPI, but the field still has to be sane)
//   bit 2    0 = single I/O SPI: DCK=D3, DOUT=D5, DIN=D7 (leaves D4/D6 free,
//            which is exactly where these boards put BUSY and DIO1)
//   bit 7    1 = most-significant bit first, as the SX126x expects
enum : std::uint32_t {
    kStreamModeSpiMsbFirst = 0x82u,
};

// CH341StreamSPI4 chip-select field: bit 7 enables CS handling, bits 1-0 pick
// D0/D1/D2. We drive CS by hand (see Ch341Transport.h) so a multi-transfer
// SX126x command stays inside one assertion, and pass 0 to leave it alone.
enum : std::uint32_t {
    kChipSelectIgnore = 0x00u,
};

struct Api {
    HANDLE (WINAPI* CH341OpenDevice)(ULONG index);
    void   (WINAPI* CH341CloseDevice)(ULONG index);
    BOOL   (WINAPI* CH341SetStream)(ULONG index, ULONG mode);
    BOOL   (WINAPI* CH341StreamSPI4)(ULONG index, ULONG chip_select, ULONG length, void* io_buffer);
    BOOL   (WINAPI* CH341Set_D5_D0)(ULONG index, ULONG set_dir_out, ULONG set_data_out);
    BOOL   (WINAPI* CH341GetStatus)(ULONG index, ULONG* status);
    BOOL   (WINAPI* CH341SetTimeout)(ULONG index, ULONG write_timeout_ms, ULONG read_timeout_ms);
    BOOL   (WINAPI* CH341SetExclusive)(ULONG index, ULONG exclusive);
    void*  (WINAPI* CH341GetDeviceName)(ULONG index);
    ULONG  (WINAPI* CH341GetVerIC)(ULONG index);
};

// Loads CH341DLLA64.DLL (x64) or CH341DLL.DLL (x86) from the bridge DLL's own
// directory, then the default search path (System32, where the CH341PAR
// installer puts it). Returns true and fills `out` when every entry point
// resolves. The module is loaded once and cached.
bool load(Api& out);

// Human-readable result of the last load() on this thread.
const char* last_status();

} // namespace mrf::hal::ch341_dyn

#endif // _WIN32
