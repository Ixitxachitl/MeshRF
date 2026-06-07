// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/c_api.h"
#include "mrf/Core.h"

#include <new>
#include <span>

extern "C" {

struct mrf_core_t {
    mrf::Core core;
};

MRF_API mrf_core_t* MRF_CALL mrf_core_create(void) {
    return new (std::nothrow) mrf_core_t{};
}

MRF_API void MRF_CALL mrf_core_destroy(mrf_core_t* core) {
    delete core;
}

MRF_API int MRF_CALL mrf_core_start_rx(mrf_core_t* core,
                                       int32_t preset,
                                       uint64_t center_freq_hz) {
    if (!core) return -1;
    try {
        core->core.start_rx(static_cast<mrf::modem::Preset>(preset), center_freq_hz);
        return 0;
    } catch (...) {
        return -2;
    }
}

MRF_API void MRF_CALL mrf_core_stop(mrf_core_t* core) {
    if (core) core->core.stop();
}

MRF_API int32_t MRF_CALL mrf_core_set_device(mrf_core_t* core, int32_t kind) {
    return mrf_core_set_rx_device(core, kind);
}

MRF_API int32_t MRF_CALL mrf_core_set_rx_device(mrf_core_t* core, int32_t kind) {
    if (!core) return -2;
    return core->core.set_rx_device(static_cast<mrf::hal::DeviceKind>(kind)) ? 0 : -1;
}

MRF_API int32_t MRF_CALL mrf_core_set_tx_device(mrf_core_t* core, int32_t kind) {
    if (!core) return -2;
    return core->core.set_tx_device(static_cast<mrf::hal::DeviceKind>(kind)) ? 0 : -1;
}

MRF_API int32_t MRF_CALL mrf_core_get_device_kind(const mrf_core_t* core) {
    return mrf_core_get_rx_device_kind(core);
}

MRF_API int32_t MRF_CALL mrf_core_get_rx_device_kind(const mrf_core_t* core) {
    if (!core) return static_cast<int32_t>(mrf::hal::DeviceKind::Null);
    return static_cast<int32_t>(core->core.rx_device_kind());
}

MRF_API int32_t MRF_CALL mrf_core_get_tx_device_kind(const mrf_core_t* core) {
    if (!core) return static_cast<int32_t>(mrf::hal::DeviceKind::Null);
    return static_cast<int32_t>(core->core.tx_device_kind());
}

MRF_API int32_t MRF_CALL mrf_core_device_available(const mrf_core_t* core,
                                                   int32_t kind) {
    if (!core) return 0;
    return core->core.is_device_available(
               static_cast<mrf::hal::DeviceKind>(kind)) ? 1 : 0;
}

MRF_API void MRF_CALL mrf_core_set_gains(mrf_core_t* core,
                                         uint8_t lna_db,
                                         uint8_t vga_db,
                                         int32_t amp_enable) {
    if (!core) return;
    core->core.set_gains(lna_db, vga_db, amp_enable != 0);
}

MRF_API void MRF_CALL mrf_core_set_device_option(mrf_core_t* core,
                                                 const char* key,
                                                 int32_t value) {
    if (!core || !key) return;
    core->core.set_device_option(key, value);
}

MRF_API int MRF_CALL mrf_core_is_running(const mrf_core_t* core) {
    return (core && core->core.is_running()) ? 1 : 0;
}

MRF_API int MRF_CALL mrf_core_start_capture(mrf_core_t* core, const char* path) {
    return (core && core->core.start_capture(path)) ? 1 : 0;
}

MRF_API void MRF_CALL mrf_core_stop_capture(mrf_core_t* core) {
    if (core) core->core.stop_capture();
}

MRF_API int MRF_CALL mrf_core_is_capturing(const mrf_core_t* core) {
    return (core && core->core.is_capturing()) ? 1 : 0;
}

MRF_API void MRF_CALL mrf_core_get_signal_stats(const mrf_core_t* core,
                                                mrf_signal_stats_t* out) {
    if (!core || !out) return;
    const auto s = core->core.signal_stats();
    out->rssi_dbfs     = s.rssi_dbfs;
    out->peak_dbfs     = s.peak_dbfs;
    out->dc_re         = s.dc_re;
    out->dc_im         = s.dc_im;
    out->total_samples = s.total_samples;
}

MRF_API uint32_t MRF_CALL mrf_core_spectrum_size(const mrf_core_t* core) {
    if (!core) return 0u;
    return static_cast<uint32_t>(core->core.spectrum_size());
}

MRF_API uint32_t MRF_CALL mrf_core_sample_rate_hz(const mrf_core_t* core) {
    if (!core) return 0u;
    return core->core.sample_rate_hz();
}

MRF_API uint64_t MRF_CALL mrf_core_spectrum_center_hz(const mrf_core_t* core) {
    if (!core) return 0u;
    return core->core.spectrum_center_hz();
}

MRF_API uint32_t MRF_CALL mrf_core_pull_spectrum(const mrf_core_t* core,
                                                 float* out_dbfs,
                                                 uint32_t capacity) {
    if (!core || !out_dbfs) return 0u;
    const std::size_t n = core->core.spectrum_size();
    if (n == 0 || capacity < n) return 0u;
    const bool ok = core->core.latest_spectrum(std::span<float>(out_dbfs, n));
    return ok ? static_cast<uint32_t>(n) : 0u;
}

MRF_API uint32_t MRF_CALL mrf_core_pull_packet_spectrogram(const mrf_core_t* core,
                                                           float* out_dbfs,
                                                           uint32_t n_time,
                                                           uint32_t n_freq) {
    if (!core || !out_dbfs) return 0u;
    return core->core.pull_packet_spectrogram(
        std::span<float>(out_dbfs,
                         static_cast<std::size_t>(n_time) * n_freq),
        n_time, n_freq);
}

MRF_API uint32_t MRF_CALL mrf_core_get_device_name(const mrf_core_t* core,
                                                   char* buf,
                                                   uint32_t capacity) {
    const char* name = (core && core->core.device_name())
                           ? core->core.device_name() : "(none)";
    uint32_t len = 0;
    while (name[len] != '\0') ++len;
    if (buf && capacity > 0) {
        uint32_t copy = len < (capacity - 1) ? len : (capacity - 1);
        for (uint32_t i = 0; i < copy; ++i) buf[i] = name[i];
        buf[copy] = '\0';
    }
    return len;
}

MRF_API uint32_t MRF_CALL mrf_core_get_tx_device_name(const mrf_core_t* core,
                                                      char* buf,
                                                      uint32_t capacity) {
    const char* name = (core && core->core.tx_device_name())
                           ? core->core.tx_device_name() : "(none)";
    uint32_t len = 0;
    while (name[len] != '\0') ++len;
    if (buf && capacity > 0) {
        uint32_t copy = len < (capacity - 1) ? len : (capacity - 1);
        for (uint32_t i = 0; i < copy; ++i) buf[i] = name[i];
        buf[copy] = '\0';
    }
    return len;
}

MRF_API uint32_t MRF_CALL mrf_core_get_device_status(const mrf_core_t* core,
                                                     char* buf,
                                                     uint32_t capacity) {
    const char* s = (core && core->core.device_status())
                        ? core->core.device_status() : "";
    uint32_t len = 0;
    while (s[len] != '\0') ++len;
    if (buf && capacity > 0) {
        uint32_t copy = len < (capacity - 1) ? len : (capacity - 1);
        for (uint32_t i = 0; i < copy; ++i) buf[i] = s[i];
        buf[copy] = '\0';
    }
    return len;
}

MRF_API uint32_t MRF_CALL mrf_core_pull_event(mrf_core_t* core,
                                              char* buf,
                                              uint32_t capacity) {
    if (!core || !buf || capacity == 0) return 0u;
    return static_cast<uint32_t>(
        core->core.pull_event(std::span<char>(buf, capacity)));
}

MRF_API int32_t MRF_CALL mrf_core_can_transmit(const mrf_core_t* core) {
    return (core && core->core.can_transmit()) ? 1 : 0;
}

MRF_API int32_t MRF_CALL mrf_core_transmit(mrf_core_t* core,
                                           int32_t preset,
                                           uint64_t center_freq_hz,
                                           const uint8_t* payload,
                                           uint32_t payload_len,
                                           uint8_t txvga_gain_db,
                                           int32_t amp_enable) {
    if (!core || !payload || payload_len == 0) return 0;
    try {
        const bool ok = core->core.transmit(
            static_cast<mrf::modem::Preset>(preset), center_freq_hz,
            std::span<const std::uint8_t>(payload, payload_len),
            txvga_gain_db, amp_enable != 0);
        return ok ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

MRF_API uint32_t MRF_CALL mrf_abi_version(void) { return 7u; }

} // extern "C"
