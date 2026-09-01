// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/c_api.h"
#include "mrf/Core.h"

#include <algorithm>
#include <cstring>
#include <string>
#include <string_view>
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

MRF_API int MRF_CALL mrf_core_start_rx_params(mrf_core_t* core,
                                              uint8_t sf,
                                              uint32_t bw_hz,
                                              uint8_t cr,
                                              uint64_t center_freq_hz) {
    if (!core) return -1;
    // sf/bw_hz cross the ABI from managed code as plain integers with no
    // prior validation; sf must stay in the modem's supported range (see
    // make_modem() in LoraModem.cpp) before it's used as a shift amount below
    // (1u << sf is undefined behavior for sf >= 32), and bw_hz must be
    // non-zero to avoid dividing by zero.
    if (sf < 7 || sf > 12 || bw_hz == 0) return -3;
    try {
        mrf::modem::LoraParams p{};
        p.spreading_factor = sf;
        p.bandwidth_hz     = bw_hz;
        p.coding_rate      = cr;
        // Auto-enable LDRO when the symbol time is >= 16 ms, matching the
        // firmware's modemPresetToParams / applyModemConfig behaviour.
        const double t_sym_ms = static_cast<double>(1u << sf) / (bw_hz / 1000.0);
        p.low_data_rate_optimize = (t_sym_ms >= 16.0);
        core->core.start_rx(p, center_freq_hz);
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

MRF_API int32_t MRF_CALL mrf_core_set_sx1262_board(mrf_core_t* core, int32_t board) {
    if (!core) return -2;
    if (board < 0 || board > mrf::hal::kSx126xBoardMax) return -1;
    core->core.set_sx1262_board(static_cast<mrf::hal::Sx126xBoard>(board));
    return 0;
}

MRF_API int32_t MRF_CALL mrf_core_get_sx1262_board(const mrf_core_t* core) {
    if (!core) return 0;
    return static_cast<int32_t>(core->core.sx1262_board());
}

MRF_API int32_t MRF_CALL mrf_core_set_sx1262_serial(mrf_core_t* core, const char* serial) {
    if (!core) return -2;
    core->core.set_sx1262_serial(serial ? std::string_view(serial) : std::string_view{});
    return 0;
}

MRF_API uint32_t MRF_CALL mrf_core_get_sx1262_serial(const mrf_core_t* core,
                                                     char* buf, uint32_t capacity) {
    if (!core || !buf || capacity == 0) return 0;
    const std::string s = core->core.sx1262_serial();
    const uint32_t n = static_cast<uint32_t>(
        std::min<std::size_t>(s.size(), capacity - 1));
    std::memcpy(buf, s.data(), n);
    buf[n] = '\0';
    return n;
}

MRF_API uint32_t MRF_CALL mrf_core_list_sx1262_serials(const mrf_core_t* core,
                                                       char* buf, uint32_t capacity) {
    if (!core || !buf || capacity == 0) return 0;
    // Newline-separated rather than an array of pointers: the list is a handful
    // of short strings and this keeps the ABI to one call with no ownership
    // question on either side.
    std::string joined;
    for (const auto& s : core->core.list_sx1262_serials()) {
        if (!joined.empty()) joined += '\n';
        joined += s;
    }
    const uint32_t n = static_cast<uint32_t>(
        std::min<std::size_t>(joined.size(), capacity - 1));
    std::memcpy(buf, joined.data(), n);
    buf[n] = '\0';
    return n;
}

MRF_API void MRF_CALL mrf_core_set_tx_power_dbm(mrf_core_t* core, int32_t dbm) {
    if (!core) return;
    // Clamped to the selected board's range inside Core; the int32 here is
    // only to keep the ABI free of signed-char marshalling questions.
    core->core.set_tx_power_dbm(static_cast<std::int8_t>(
        std::clamp(dbm, -128, 127)));
}

MRF_API int32_t MRF_CALL mrf_core_get_tx_power_dbm(const mrf_core_t* core) {
    if (!core) return 0;
    return core->core.tx_power_dbm();
}

MRF_API void MRF_CALL mrf_core_tx_power_range(const mrf_core_t* core,
                                              int32_t* min_dbm,
                                              int32_t* max_dbm) {
    if (!core) return;
    std::int8_t lo = 0, hi = 0;
    core->core.tx_power_range_dbm(lo, hi);
    if (min_dbm) *min_dbm = lo;
    if (max_dbm) *max_dbm = hi;
}

MRF_API void MRF_CALL mrf_core_set_tx_band_limits(mrf_core_t* core,
                                                  uint64_t min_hz,
                                                  uint64_t max_hz) {
    if (!core) return;
    core->core.set_tx_band_limits(min_hz, max_hz);
}

MRF_API void MRF_CALL mrf_core_get_tx_band_limits(const mrf_core_t* core,
                                                  uint64_t* min_hz,
                                                  uint64_t* max_hz) {
    if (!core) return;
    std::uint64_t lo = 0, hi = 0;
    core->core.tx_band_limits(lo, hi);
    if (min_hz) *min_hz = lo;
    if (max_hz) *max_hz = hi;
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

MRF_API uint64_t MRF_CALL mrf_core_spectrum_frame_count(const mrf_core_t* core) {
    if (!core) return 0u;
    return core->core.spectrum_frame_count();
}

MRF_API uint32_t MRF_CALL mrf_core_spectrum_history_frame_rate_hz(const mrf_core_t* core) {
    if (!core) return 0u;
    return core->core.spectrum_history_frame_rate_hz();
}

MRF_API uint32_t MRF_CALL mrf_core_pull_spectrum_frames(
    const mrf_core_t* core,
    uint64_t after_frame_idx,
    uint32_t max_count,
    float* out_frames,
    uint32_t out_frames_len) {
    if (!core || !out_frames || out_frames_len == 0u || max_count == 0u)
        return 0u;
    return core->core.pull_spectrum_frames(
        after_frame_idx, max_count, std::span<float>(out_frames, out_frames_len));
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

MRF_API int32_t MRF_CALL mrf_core_transmit_params(mrf_core_t* core,
                                                  uint8_t sf,
                                                  uint32_t bw_hz,
                                                  uint8_t cr,
                                                  uint64_t center_freq_hz,
                                                  const uint8_t* payload,
                                                  uint32_t payload_len,
                                                  uint8_t txvga_gain_db,
                                                  int32_t amp_enable) {
    if (!core || !payload || payload_len == 0) return 0;
    // ChirpChatTx (the modulator this ultimately reaches) only supports
    // SF 7..12; reject out of range here rather than letting a throw from
    // deep in the modem propagate, and to avoid `1u << sf` being UB below.
    if (sf < 7 || sf > 12 || bw_hz == 0) return 0;
    try {
        mrf::modem::LoraParams p{};
        p.spreading_factor = sf;
        p.bandwidth_hz     = bw_hz;
        p.coding_rate      = cr;
        const double t_sym_ms = static_cast<double>(1u << sf) / (bw_hz / 1000.0);
        p.low_data_rate_optimize = (t_sym_ms >= 16.0);
        const bool ok = core->core.transmit(
            p, center_freq_hz,
            std::span<const std::uint8_t>(payload, payload_len),
            txvga_gain_db, amp_enable != 0);
        return ok ? 1 : 0;
    } catch (...) {
        return 0;
    }
}

// Declares the wiring and power model of an SX1262 on the host's own SPI bus,
// for the Custom SPI board. Process-global rather than per-core, like the
// board profiles it feeds: it describes the machine, not a session.
//
// Every field is the operator's statement about their hardware. The power
// model especially: nothing on an SPI bus reports whether a front end is
// fitted, and assuming none would under-report the output of every board that
// has one.
MRF_API int32_t MRF_CALL mrf_set_custom_spi_board(
    const char* spidev, const char* gpiochip, int32_t speed_hz,
    int32_t cs, int32_t busy, int32_t reset, int32_t dio1, int32_t rxen,
    int32_t has_rxen, int32_t dio2_as_rf_switch, int32_t dio3_tcxo,
    int32_t tcxo_voltage, int32_t max_chip_dbm, int32_t pa_gain_db,
    int32_t min_out_dbm, int32_t max_out_dbm) {
    try {
        mrf::hal::Sx126xCustomSpiBoard b{};
        if (spidev && *spidev) b.pins.spidev = spidev;
        if (gpiochip && *gpiochip) b.pins.gpiochip = gpiochip;
        if (speed_hz > 0) b.pins.speed_hz = static_cast<std::uint32_t>(speed_hz);
        b.pins.cs    = cs;
        b.pins.busy  = busy;
        b.pins.reset = reset;
        b.pins.dio1  = dio1;
        b.pins.rxen  = rxen;

        b.has_rxen          = has_rxen != 0;
        b.dio2_as_rf_switch = dio2_as_rf_switch != 0;
        b.dio3_tcxo         = dio3_tcxo != 0;
        b.tcxo_voltage      = static_cast<std::uint8_t>(tcxo_voltage);
        b.max_chip_dbm      = static_cast<std::int8_t>(max_chip_dbm);
        b.pa_gain_db        = static_cast<std::int8_t>(pa_gain_db);
        b.min_out_dbm       = static_cast<std::int8_t>(min_out_dbm);
        b.max_out_dbm       = static_cast<std::int8_t>(max_out_dbm);

        mrf::hal::set_custom_spi_board(b);
        return 0;
    } catch (...) {
        return -1;
    }
}

// 8: SX1262 packet transmitter (board selection, dBm power control).
// 9: SX1262 receive path and serial-based stick selection.
// 10: SX1262 over the host's own SPI bus (uConsole AIO V2, Pi HATs), with a
//     declarable custom pin map and power model.
MRF_API uint32_t MRF_CALL mrf_abi_version(void) { return 10u; }

} // extern "C"
