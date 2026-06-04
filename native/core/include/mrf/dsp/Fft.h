// SPDX-License-Identifier: GPL-3.0-or-later
//
// Tiny in-place radix-2 FFT for power-of-two sizes. Float complex.

#pragma once

#include <complex>
#include <cstddef>
#include <span>

namespace mrf::dsp {

class Fft {
public:
    using sample_t = std::complex<float>;

    // size must be a power of two and >= 2.
    explicit Fft(std::size_t size);

    [[nodiscard]] std::size_t size() const noexcept { return n_; }

    // Forward DFT, in place. data.size() must equal size().
    void forward(std::span<sample_t> data) const;

    [[nodiscard]] static bool is_power_of_two(std::size_t v) noexcept {
        return v >= 2 && (v & (v - 1)) == 0;
    }

private:
    std::size_t n_;
    std::size_t log2n_;
};

} // namespace mrf::dsp
