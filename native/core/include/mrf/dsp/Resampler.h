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

    // The prototype low-pass, decomposed once into its L phases and stored a
    // phase at a time, each phase's taps in the order the samples come. An
    // output is then one walk along two neighbouring runs of memory rather
    // than a stride of L through the prototype.
    std::vector<float> branches_;   // L_ phases of taps_per_phase_ taps

    // The tail of the input, kept so an output landing at the start of a block
    // can still reach back into the one before it. Linear rather than a ring:
    // the inner loop is the whole cost of this class, and a ring puts an index
    // wrap in the middle of it.
    std::vector<cf>    hist_;
    std::size_t        hist_len_{1};

    std::vector<cf>    edge_;       // hist_ and the head of the block, joined
    std::uint64_t      in_count_{0};// total input samples seen

    // Where the next output lands, carried forward rather than divided out of
    // an output counter: base_ is the newest input sample it taps and branch_
    // the phase it taps with.
    std::uint64_t      base_{0};
    std::uint32_t      branch_{0};
    std::uint32_t      step_base_{1}; // M_ / L_
    std::uint32_t      step_branch_{0}; // M_ % L_

    std::vector<cf>    out_;        // scratch output for the current process()
};

} // namespace mrf::dsp
