// SPDX-License-Identifier: GPL-3.0-or-later
//
// Decimating FIR low-pass filter for complex baseband samples.
// Designs a Hamming-windowed sinc at construction; downsamples by an integer
// factor M = floor(input_rate / output_rate).

#pragma once

#include <complex>
#include <cstdint>
#include <span>
#include <vector>

namespace mrf::dsp {

class FirDecimator {
public:
    using sample_t = std::complex<float>;

    // Build a decimator. The actual output rate is input_rate_hz / decimation()
    // which may differ slightly from the requested output_rate_hz when
    // input is not an integer multiple of output.
    FirDecimator(std::uint32_t input_rate_hz, std::uint32_t output_rate_hz);

    // Build a decimator with explicit decimation factor.
    explicit FirDecimator(std::uint32_t decimation_factor, std::size_t num_taps = 0);

    [[nodiscard]] std::uint32_t decimation() const noexcept { return M_; }
    [[nodiscard]] std::size_t   num_taps() const noexcept { return taps_.size(); }

    // Filter and decimate `in`. Returned span is valid until the next call.
    std::span<const sample_t> process(std::span<const sample_t> in);

    // Read-only access to filter taps for testing / inspection.
    [[nodiscard]] std::span<const float> taps() const noexcept { return taps_; }

private:
    static std::vector<float> design_lowpass(std::size_t num_taps,
                                             float cutoff_normalized);

    std::uint32_t M_;
    std::vector<float> taps_;        // FIR coefficients (real, normalized)
    std::vector<sample_t> delay_;    // circular buffer of the last numTaps inputs
    std::size_t delay_pos_{0};       // index of the next slot to write
    std::uint32_t phase_{0};         // 0..M-1 counter; output when it wraps
    std::vector<sample_t> out_;      // scratch
};

} // namespace mrf::dsp
