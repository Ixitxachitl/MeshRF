// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <atomic>
#include <complex>
#include <cstdint>
#include <span>

namespace mrf::dsp {

// Lightweight statistics block: tracks running mean(|s|^2) for RSSI in dBFS,
// peak magnitude, and average DC offset. Designed to be called from the RX
// thread; readers see a relaxed-atomic snapshot.
class SignalStats {
public:
    using sample_t = std::complex<float>;

    void process(std::span<const sample_t> samples) noexcept;

    struct Snapshot {
        float rssi_dbfs;   // 10*log10(mean |s|^2), -inf saturated to -120
        float peak_dbfs;   // 20*log10(max |s|)
        float dc_re;
        float dc_im;
        std::uint64_t total_samples;
    };

    [[nodiscard]] Snapshot snapshot() const noexcept;

    void reset() noexcept;

private:
    // Updated on the RX thread, read with relaxed memory order from any thread.
    std::atomic<float> last_rssi_{-120.0f};
    std::atomic<float> last_peak_{-120.0f};
    std::atomic<float> last_dc_re_{0.0f};
    std::atomic<float> last_dc_im_{0.0f};
    std::atomic<std::uint64_t> total_{0};
};

} // namespace mrf::dsp
