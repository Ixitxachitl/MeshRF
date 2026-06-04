// SPDX-License-Identifier: GPL-3.0-or-later
//
// C ABI consumed by the managed P/Invoke layer in app/MeshtasticRF.Core.
// Keep this header valid C — no C++ types — so other languages can also bind.

#pragma once

#include "mrf/Export.h"

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mrf_core_t mrf_core_t;

// Lifecycle ---------------------------------------------------------------
MRF_API mrf_core_t* MRF_CALL mrf_core_create(void);
MRF_API void        MRF_CALL mrf_core_destroy(mrf_core_t* core);

// Control -----------------------------------------------------------------
// preset corresponds to mrf::modem::Preset (enum order in Preset.h).
MRF_API int  MRF_CALL mrf_core_start_rx(mrf_core_t* core,
                                        int32_t preset,
                                        uint64_t center_freq_hz);
MRF_API void MRF_CALL mrf_core_stop(mrf_core_t* core);
MRF_API int  MRF_CALL mrf_core_is_running(const mrf_core_t* core);

// Radio backend selection. `kind` mirrors mrf::hal::DeviceKind:
//   0 = Auto, 1 = HackRF, 2 = RTL-SDR, 3 = Synthetic/Null.
// Reopens the device immediately when RX is stopped so the device name/status
// reflect the choice. Returns 0 on success, -1 if RX is running, -2 on null.
MRF_API int32_t MRF_CALL mrf_core_set_device(mrf_core_t* core, int32_t kind);

// The backend that actually opened (may differ from the requested kind).
MRF_API int32_t MRF_CALL mrf_core_get_device_kind(const mrf_core_t* core);

// 1 if the given backend's runtime library can be loaded (selectable), else 0.
MRF_API int32_t MRF_CALL mrf_core_device_available(const mrf_core_t* core,
                                                   int32_t kind);

// IQ capture: dump the decimated modem-input stream (interleaved float32
// I/Q, ".cf32") to `path`. Safe to toggle while RX runs. Capped to ~60 s.
MRF_API int  MRF_CALL mrf_core_start_capture(mrf_core_t* core, const char* path);
MRF_API void MRF_CALL mrf_core_stop_capture(mrf_core_t* core);
MRF_API int  MRF_CALL mrf_core_is_capturing(const mrf_core_t* core);

// Live gain control. Clamped to HackRF ranges (lna 0..40, vga 0..62).
MRF_API void MRF_CALL mrf_core_set_gains(mrf_core_t* core,
                                         uint8_t lna_db,
                                         uint8_t vga_db,
                                         int32_t amp_enable);

// Diagnostics ------------------------------------------------------------
typedef struct mrf_signal_stats_t {
    float rssi_dbfs;
    float peak_dbfs;
    float dc_re;
    float dc_im;
    uint64_t total_samples;
} mrf_signal_stats_t;

MRF_API void     MRF_CALL mrf_core_get_signal_stats(const mrf_core_t* core,
                                                    mrf_signal_stats_t* out);

// Returns the spectrum size in bins (0 if RX is not running). The caller
// should pre-allocate a float[spectrum_size] buffer.
MRF_API uint32_t MRF_CALL mrf_core_spectrum_size(const mrf_core_t* core);

// Returns the device sample rate in Hz (0 if RX is not running). This is the
// full span of the spectrum/waterfall; DC (bin n/2) maps to the tuned center
// frequency.
MRF_API uint32_t MRF_CALL mrf_core_sample_rate_hz(const mrf_core_t* core);

// Copies the latest dBFS spectrum frame into out. Returns the number of bins
// copied (0 if no frame is available or capacity is insufficient). Bins are
// FFT-shifted: index 0 is the most negative frequency, index n/2 is DC.
MRF_API uint32_t MRF_CALL mrf_core_pull_spectrum(const mrf_core_t* core,
                                                 float* out_dbfs,
                                                 uint32_t capacity);

// Computes a high-time-resolution spectrogram of the most recent ~150 ms of
// modem-rate IQ, cropped to the LoRa channel. Fills out_dbfs row-major as
// n_time rows of n_freq dBFS values (low->high freq left->right). out_dbfs
// must hold at least n_time*n_freq floats. Returns the number of rows written
// (n_time) or 0 if not enough IQ history is available.
MRF_API uint32_t MRF_CALL mrf_core_pull_packet_spectrogram(const mrf_core_t* core,
                                                           float* out_dbfs,
                                                           uint32_t n_time,
                                                           uint32_t n_freq);

// Copies a NUL-terminated UTF-8 device name into `buf` (up to `capacity`
// bytes including the NUL). Returns the number of bytes that would be needed
// (excluding NUL). If buf is null or capacity is 0, no copy is made.
MRF_API uint32_t MRF_CALL mrf_core_get_device_name(const mrf_core_t* core,
                                                   char* buf,
                                                   uint32_t capacity);

// Diagnostic string from the most recent device-open attempt.
MRF_API uint32_t MRF_CALL mrf_core_get_device_status(const mrf_core_t* core,
                                                     char* buf,
                                                     uint32_t capacity);

// Pop the next pending demodulator event into `buf` (UTF-8, NUL-terminated).
// Returns the number of bytes written (excluding NUL), or 0 if no event is
// queued or the buffer is too small.
MRF_API uint32_t MRF_CALL mrf_core_pull_event(mrf_core_t* core,
                                              char* buf,
                                              uint32_t capacity);

// Returns the bridge ABI version. Bumped on breaking change.
MRF_API uint32_t MRF_CALL mrf_abi_version(void);

#ifdef __cplusplus
} // extern "C"
#endif
