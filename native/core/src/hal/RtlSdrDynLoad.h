// SPDX-License-Identifier: GPL-3.0-or-later
//
// Minimal runtime loader for librtlsdr (rtlsdr.dll). Mirrors HackRfDynLoad:
// we declare only the subset of the API we use, LoadLibrary the DLL, and
// GetProcAddress each entry point. This lets the app pick up an rtlsdr.dll
// shipped by PothosSDR / SDRangel / the zadig driver package without a
// build-time dependency on rtl-sdr.h or rtlsdr.lib.
#pragma once

#include <cstdint>

namespace mrf::hal::rtlsdr_dyn {

// Opaque device handle (matches the upstream typedef).
struct rtlsdr_dev;

// Subset of the return convention: 0 == success.
enum {
    RTLSDR_SUCCESS = 0,
};

// Async sample callback: interleaved unsigned 8-bit I/Q.
using rtlsdr_read_async_cb_t = void (*)(unsigned char* buf,
                                        std::uint32_t len,
                                        void* ctx);

// Function pointer table. Required entries are non-null when load() returns
// true; optional entries (agc/ppm/gains/name) may be null and callers must
// guard them.
struct Api {
    // Required.
    std::uint32_t (*rtlsdr_get_device_count)();
    int (*rtlsdr_open)(rtlsdr_dev**, std::uint32_t);
    int (*rtlsdr_close)(rtlsdr_dev*);
    int (*rtlsdr_set_center_freq)(rtlsdr_dev*, std::uint32_t);
    int (*rtlsdr_set_sample_rate)(rtlsdr_dev*, std::uint32_t);
    int (*rtlsdr_set_tuner_gain_mode)(rtlsdr_dev*, int);
    int (*rtlsdr_set_tuner_gain)(rtlsdr_dev*, int);
    int (*rtlsdr_reset_buffer)(rtlsdr_dev*);
    int (*rtlsdr_read_async)(rtlsdr_dev*, rtlsdr_read_async_cb_t, void*,
                             std::uint32_t, std::uint32_t);
    int (*rtlsdr_cancel_async)(rtlsdr_dev*);
    // Optional (guard for null before calling).
    int (*rtlsdr_set_agc_mode)(rtlsdr_dev*, int);
    int (*rtlsdr_set_freq_correction)(rtlsdr_dev*, int);
    int (*rtlsdr_get_tuner_gains)(rtlsdr_dev*, int*);
    const char* (*rtlsdr_get_device_name)(std::uint32_t);
};

// Tries to load rtlsdr.dll / librtlsdr.dll from the same locations as the
// HackRF loader (module dir, RTLSDR_DIR env, PothosSDR/SDRangel, PATH).
// Returns true and fills `out` when every required entry point resolves.
bool load(Api& out);

// Last human-readable status of load() on the calling thread.
const char* last_status();

} // namespace mrf::hal::rtlsdr_dyn
