// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Fir.h"

#include <cmath>
#include <numbers>
#include <stdexcept>

namespace mrf::dsp {

namespace {
constexpr float kPi = std::numbers::pi_v<float>;
}

std::vector<float> FirDecimator::design_lowpass(std::size_t num_taps,
                                                float cutoff_normalized) {
    // Windowed-sinc with a Hamming window. cutoff_normalized is in cycles/sample
    // (0..0.5). Linear-phase, length=num_taps (odd preferred for symmetric).
    if (num_taps < 3) num_taps = 3;
    std::vector<float> h(num_taps);
    const float fc = cutoff_normalized;
    const float center = (num_taps - 1) * 0.5f;

    float sum = 0.0f;
    for (std::size_t i = 0; i < num_taps; ++i) {
        const float n = static_cast<float>(i) - center;
        // Sinc (0/0 -> 2*fc at the center)
        float s;
        if (std::abs(n) < 1e-6f) {
            s = 2.0f * fc;
        } else {
            s = std::sin(2.0f * kPi * fc * n) / (kPi * n);
        }
        // Hamming window
        const float w = 0.54f - 0.46f * std::cos(2.0f * kPi *
                                                 static_cast<float>(i) /
                                                 static_cast<float>(num_taps - 1));
        h[i] = s * w;
        sum += h[i];
    }
    // Normalize unity DC gain so |H(e^{j0})| == 1.
    if (sum != 0.0f) {
        for (auto& c : h) c /= sum;
    }
    return h;
}

FirDecimator::FirDecimator(std::uint32_t input_rate_hz, std::uint32_t output_rate_hz)
    : FirDecimator(
          (input_rate_hz == 0 || output_rate_hz == 0)
              ? 1u
              : std::max<std::uint32_t>(1u, input_rate_hz / output_rate_hz),
          std::size_t{0}) {
    if (input_rate_hz == 0 || output_rate_hz == 0)
        throw std::invalid_argument("FirDecimator: zero sample rate");
}

FirDecimator::FirDecimator(std::uint32_t decimation_factor, std::size_t num_taps)
    : M_(decimation_factor == 0 ? 1u : decimation_factor) {
    // Tap count: 8*M + 1 odd taps gives a reasonable transition. Cap at 257
    // so very large M doesn't blow up.
    if (num_taps == 0) {
        num_taps = static_cast<std::size_t>(8u * M_ + 1u);
        if (num_taps > 257) num_taps = 257;
    }
    if ((num_taps & 1u) == 0) num_taps += 1; // prefer odd
    // Cutoff: 0.45/M (slight margin below new Nyquist).
    const float cutoff = 0.45f / static_cast<float>(M_);
    taps_ = design_lowpass(num_taps, cutoff);
    delay_.assign(taps_.size(), sample_t{0.0f, 0.0f});
}

std::span<const FirDecimator::sample_t> FirDecimator::process(std::span<const sample_t> in) {
    out_.clear();
    out_.reserve(in.size() / M_ + 1);

    const std::size_t N = taps_.size();
    for (auto x : in) {
        // Write input into circular delay line.
        delay_[delay_pos_] = x;
        delay_pos_ = (delay_pos_ + 1u) % N;

        if (++phase_ >= M_) {
            phase_ = 0;
            // Compute one output sample: sum(taps[k] * delay[delay_pos - 1 - k])
            sample_t acc{0.0f, 0.0f};
            // delay_pos_ now points one past the most-recent sample.
            std::size_t idx = (delay_pos_ + N - 1u) % N;
            for (std::size_t k = 0; k < N; ++k) {
                acc += taps_[k] * delay_[idx];
                idx = (idx == 0) ? (N - 1) : (idx - 1);
            }
            out_.push_back(acc);
        }
    }
    return std::span<const sample_t>(out_.data(), out_.size());
}

} // namespace mrf::dsp
