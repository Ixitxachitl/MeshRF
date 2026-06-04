// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Fft.h"

#include <bit>
#include <cmath>
#include <numbers>
#include <stdexcept>

namespace mrf::dsp {

Fft::Fft(std::size_t size) : n_(size), log2n_(0) {
    if (!is_power_of_two(size))
        throw std::invalid_argument("Fft: size must be a power of two >= 2");
    log2n_ = static_cast<std::size_t>(std::countr_zero(size));
}

void Fft::forward(std::span<sample_t> data) const {
    if (data.size() != n_)
        throw std::invalid_argument("Fft::forward: span size mismatch");

    // Bit-reversal permutation.
    const std::size_t n = n_;
    for (std::size_t i = 1, j = 0; i < n; ++i) {
        std::size_t bit = n >> 1;
        for (; j & bit; bit >>= 1) j ^= bit;
        j ^= bit;
        if (i < j) std::swap(data[i], data[j]);
    }

    // Cooley-Tukey: stages of size 2, 4, 8, ..., n.
    constexpr float kPi = std::numbers::pi_v<float>;
    for (std::size_t len = 2; len <= n; len <<= 1) {
        const float angle = -2.0f * kPi / static_cast<float>(len);
        const sample_t wlen{std::cos(angle), std::sin(angle)};
        for (std::size_t i = 0; i < n; i += len) {
            sample_t w{1.0f, 0.0f};
            const std::size_t half = len >> 1;
            for (std::size_t k = 0; k < half; ++k) {
                const sample_t u = data[i + k];
                const sample_t t = w * data[i + k + half];
                data[i + k]        = u + t;
                data[i + k + half] = u - t;
                w *= wlen;
            }
        }
    }
}

} // namespace mrf::dsp
