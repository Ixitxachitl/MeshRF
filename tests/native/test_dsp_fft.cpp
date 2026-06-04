// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Fft.h"

#include <gtest/gtest.h>

#include <cmath>
#include <complex>
#include <numbers>
#include <stdexcept>
#include <vector>

using mrf::dsp::Fft;

TEST(Fft, RejectsNonPowerOfTwo) {
    EXPECT_THROW((Fft{3}), std::invalid_argument);
    EXPECT_THROW((Fft{0}), std::invalid_argument);
    EXPECT_NO_THROW((Fft{2}));
    EXPECT_NO_THROW((Fft{1024}));
}

TEST(Fft, DcImpulse) {
    Fft fft(8);
    std::vector<std::complex<float>> x(8, {1.0f, 0.0f});
    fft.forward(x);
    // DC bin should equal sum (8), all other bins 0.
    EXPECT_NEAR(x[0].real(), 8.0f, 1e-4f);
    EXPECT_NEAR(x[0].imag(), 0.0f, 1e-4f);
    for (std::size_t k = 1; k < x.size(); ++k) {
        EXPECT_LT(std::abs(x[k]), 1e-4f);
    }
}

TEST(Fft, ToneLandsOnExpectedBin) {
    constexpr std::size_t N = 1024;
    constexpr float kPi = std::numbers::pi_v<float>;
    Fft fft(N);
    std::vector<std::complex<float>> x(N);
    const std::size_t bin = 37;
    for (std::size_t i = 0; i < N; ++i) {
        const float ph = 2.0f * kPi * static_cast<float>(bin) *
                         static_cast<float>(i) / static_cast<float>(N);
        x[i] = {std::cos(ph), std::sin(ph)};
    }
    fft.forward(x);

    // Energy should be concentrated in bin `bin`.
    auto mag2 = [](std::complex<float> v) { return v.real() * v.real() + v.imag() * v.imag(); };
    const float peak = mag2(x[bin]);
    float total = 0.0f;
    for (auto v : x) total += mag2(v);
    EXPECT_GT(peak / total, 0.99f);
}
