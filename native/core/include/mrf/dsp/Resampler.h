// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <complex>
#include <cstdint>
#include <span>
#include <vector>

namespace mrf::dsp {

// Polyphase rational resampler for complex baseband. Resamples by the exact
// ratio L/M = output_rate / input_rate (reduced by gcd), so non-integer ratios
// such as 2.4 MHz -> 1.0 MHz (5/12) are handled correctly. A single windowed-
// sinc prototype low-pass is polyphase-decomposed across the L interpolation
// phases; only the taps for the active phase are evaluated per output sample.
class Resampler {
public:
    Resampler(std::uint32_t input_rate_hz, std::uint32_t output_rate_hz);
    ~Resampler();

    Resampler(const Resampler&) = delete;
    Resampler& operator=(const Resampler&) = delete;

    [[nodiscard]] std::uint32_t input_rate_hz() const noexcept  { return in_rate_; }
    [[nodiscard]] std::uint32_t output_rate_hz() const noexcept { return out_rate_; }

    // Interpolation (L) / decimation (M) factors after gcd reduction.
    [[nodiscard]] std::uint32_t up_factor() const noexcept   { return L_; }
    [[nodiscard]] std::uint32_t down_factor() const noexcept { return M_; }

    std::span<const std::complex<float>> process(std::span<const std::complex<float>> in);

private:
    using cf = std::complex<float>;

    std::uint32_t in_rate_;
    std::uint32_t out_rate_;
    std::uint32_t L_{1};            // interpolation factor
    std::uint32_t M_{1};            // decimation factor
    std::size_t   taps_per_phase_{1};
    std::vector<float> proto_;      // prototype low-pass, length taps_per_phase_*L_

    std::vector<cf>    hist_;       // ring buffer of recent input samples
    std::size_t        hist_size_{1};
    std::size_t        wpos_{0};    // next write slot in hist_
    std::uint64_t      in_count_{0};// total input samples seen
    std::uint64_t      next_out_{0};// index of the next output sample

    std::vector<cf>    out_;        // scratch output for the current process()
};

} // namespace mrf::dsp
