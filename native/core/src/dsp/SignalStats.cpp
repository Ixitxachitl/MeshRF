// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/SignalStats.h"

#include <cmath>

namespace mrf::dsp {

void SignalStats::process(std::span<const sample_t> samples) noexcept {
    if (samples.empty()) return;

    double sum_pwr = 0.0;
    double sum_re  = 0.0;
    double sum_im  = 0.0;
    float peak_mag = 0.0f;

    for (auto s : samples) {
        const float re = s.real();
        const float im = s.imag();
        const float p  = re * re + im * im;
        sum_pwr += p;
        sum_re  += re;
        sum_im  += im;
        if (p > peak_mag) peak_mag = p;
    }

    const std::size_t n = samples.size();
    const float mean_pwr = static_cast<float>(sum_pwr / static_cast<double>(n));
    const float dc_re    = static_cast<float>(sum_re / static_cast<double>(n));
    const float dc_im    = static_cast<float>(sum_im / static_cast<double>(n));

    auto safe_db10 = [](float v) {
        return (v > 1e-12f) ? 10.0f * std::log10(v) : -120.0f;
    };

    last_rssi_.store(safe_db10(mean_pwr), std::memory_order_relaxed);
    last_peak_.store(safe_db10(peak_mag), std::memory_order_relaxed);
    last_dc_re_.store(dc_re, std::memory_order_relaxed);
    last_dc_im_.store(dc_im, std::memory_order_relaxed);
    total_.fetch_add(n, std::memory_order_relaxed);
}

SignalStats::Snapshot SignalStats::snapshot() const noexcept {
    return Snapshot{
        last_rssi_.load(std::memory_order_relaxed),
        last_peak_.load(std::memory_order_relaxed),
        last_dc_re_.load(std::memory_order_relaxed),
        last_dc_im_.load(std::memory_order_relaxed),
        total_.load(std::memory_order_relaxed),
    };
}

void SignalStats::reset() noexcept {
    last_rssi_.store(-120.0f, std::memory_order_relaxed);
    last_peak_.store(-120.0f, std::memory_order_relaxed);
    last_dc_re_.store(0.0f, std::memory_order_relaxed);
    last_dc_im_.store(0.0f, std::memory_order_relaxed);
    total_.store(0, std::memory_order_relaxed);
}

} // namespace mrf::dsp
