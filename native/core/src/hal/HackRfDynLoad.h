// SPDX-License-Identifier: GPL-3.0-or-later
//
// Minimal runtime loader for libhackrf. We don't need hackrf.h or hackrf.lib
// at compile time — we declare the few types/functions we actually use here,
// LoadLibrary the DLL, and GetProcAddress each function. This lets us ship a
// vendored hackrf.dll without depending on having the SDK installed.
#pragma once

#include <cstdint>

#if defined(_WIN32)
// Guarded because the MSVC build already defines this on the command line
// (see the root CMakeLists.txt); kept so the header still stands on its own
// for a build that doesn't.
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
#endif

namespace mrf::hal::hackrf_dyn {

// Opaque device handle (matches the upstream typedef).
struct hackrf_device;

// Subset of hackrf_error we care about.
enum {
    HACKRF_SUCCESS = 0,
};

// Mirrors libhackrf's hackrf_transfer struct layout. ABI-stable since the
// project's 2014 release.
struct hackrf_transfer {
    hackrf_device* device;
    std::uint8_t*  buffer;
    int            buffer_length;
    int            valid_length;
    void*          rx_ctx;
    void*          tx_ctx;
};

using hackrf_sample_block_cb_fn = int (*)(hackrf_transfer*);

// Function pointer table. All non-null when load() returns true.
struct Api {
    int (*hackrf_init)();
    int (*hackrf_exit)();
    int (*hackrf_open)(hackrf_device** device);
    int (*hackrf_close)(hackrf_device* device);
    int (*hackrf_set_freq)(hackrf_device*, std::uint64_t);
    int (*hackrf_set_sample_rate)(hackrf_device*, double);
    int (*hackrf_set_baseband_filter_bandwidth)(hackrf_device*, std::uint32_t);
    std::uint32_t (*hackrf_compute_baseband_filter_bw_round_down_lt)(std::uint32_t);
    int (*hackrf_set_lna_gain)(hackrf_device*, std::uint32_t);
    int (*hackrf_set_vga_gain)(hackrf_device*, std::uint32_t);
    int (*hackrf_set_txvga_gain)(hackrf_device*, std::uint32_t);
    int (*hackrf_set_amp_enable)(hackrf_device*, std::uint8_t);
    int (*hackrf_start_rx)(hackrf_device*, hackrf_sample_block_cb_fn, void*);
    int (*hackrf_stop_rx)(hackrf_device*);
    int (*hackrf_start_tx)(hackrf_device*, hackrf_sample_block_cb_fn, void*);
    int (*hackrf_stop_tx)(hackrf_device*);
};

// Tries to load hackrf.dll from (in order):
//   1. The directory of the calling module (next to MeshRF.Native.dll)
//   2. %HACKRF_DIR% / %HACKRF_ROOT%
//   3. C:\Program Files\SDRangel
//   4. C:\Program Files\PothosSDR\bin
//   5. The default Windows DLL search order (PATH, etc.)
//
// Returns true and fills `out` when every required entry point is resolved.
bool load(Api& out);

// Last human-readable status of load() on the calling thread, e.g.
// "loaded from C:/.../hackrf.dll" or "hackrf.dll not found".
const char* last_status();

} // namespace mrf::hal::hackrf_dyn
