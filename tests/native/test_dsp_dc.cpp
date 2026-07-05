// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/DcBlocker.h"

#include <gtest/gtest.h>

#include <cmath>
#include <numbers>
#include <vector>

using mrf::dsp::DcBlocker;

TEST(DcBlocker, RemovesConstantOffset) {
    DcBlocker blk;
    // With R=0.9995 the output after k samples of constant input decays as
    // R^k * c.  At k=20000: 0.9995^19999 * 0.5 ≈ 2.3e-5 — well within 1e-3.
    // 10000 samples only gives ~0.0034, which is above the old tolerance.
    std::vector<std::complex<float>> data(20000, std::complex<float>{0.5f, -0.3f});
    blk.process(data);

    // After enough samples, output should converge to ~0.
    auto last = data.back();
    EXPECT_NEAR(last.real(), 0.0f, 1e-3f);
    EXPECT_NEAR(last.imag(), 0.0f, 1e-3f);
}

TEST(DcBlocker, PassesAcSignalNearUnity) {
    DcBlocker blk;
    constexpr float kPi = std::numbers::pi_v<float>;
    const std::size_t N = 8192;
    const float freq = 0.05f; // cycles/sample, well above the ~0.00008*Fs corner
    std::vector<std::complex<float>> data(N);
    for (std::size_t i = 0; i < N; ++i) {
        const float ph = 2.0f * kPi * freq * static_cast<float>(i);
        data[i] = {std::cos(ph), std::sin(ph)};
    }

    auto in_copy = data;
    blk.process(data);

    // Mean power ratio should be close to 1 in steady state.
    auto mean_pwr = [](const std::vector<std::complex<float>>& x, std::size_t skip) {
        double s = 0.0;
        for (std::size_t i = skip; i < x.size(); ++i) {
            s += static_cast<double>(x[i].real() * x[i].real() + x[i].imag() * x[i].imag());
        }
        return s / static_cast<double>(x.size() - skip);
    };
    const double pi = mean_pwr(in_copy, 1024);
    const double po = mean_pwr(data, 1024);
    EXPECT_NEAR(po / pi, 1.0, 0.05);
}
