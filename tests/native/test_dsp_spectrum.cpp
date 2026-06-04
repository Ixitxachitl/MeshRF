// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Spectrum.h"
#include "mrf/dsp/SignalStats.h"

#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <complex>
#include <numbers>
#include <vector>

using mrf::dsp::Spectrum;
using mrf::dsp::SignalStats;

TEST(Spectrum, NoFrameUntilFftSizeFilled) {
    Spectrum sp(256);
    EXPECT_EQ(sp.frame_count(), 0u);

    std::vector<std::complex<float>> half(128, {0.1f, 0.0f});
    sp.push(half);
    EXPECT_EQ(sp.frame_count(), 0u);

    sp.push(half);
    EXPECT_EQ(sp.frame_count(), 1u);
}

TEST(Spectrum, ToneShowsPeakAtExpectedBin) {
    constexpr std::size_t N = 1024;
    constexpr float kPi = std::numbers::pi_v<float>;
    Spectrum sp(N);

    // Tone at +0.10 of normalized frequency. After FFT-shift DC is at N/2;
    // the display axis is then inverted about DC (waterfall mirror fix), so a
    // positive tone lands at bin (N/2 - 0.10*N) = 410.
    const float freq = 0.10f;
    std::vector<std::complex<float>> x(N);
    for (std::size_t i = 0; i < N; ++i) {
        const float ph = 2.0f * kPi * freq * static_cast<float>(i);
        x[i] = {std::cos(ph), std::sin(ph)};
    }
    sp.push(x);
    ASSERT_EQ(sp.frame_count(), 1u);

    std::vector<float> out(N);
    ASSERT_TRUE(sp.latest(out));

    // Find peak bin and check it is close to the expected location.
    const auto it = std::max_element(out.begin(), out.end());
    const std::size_t peak_bin = static_cast<std::size_t>(it - out.begin());
    const std::size_t expected = N / 2 - static_cast<std::size_t>(freq * N);
    EXPECT_LE(std::abs(static_cast<long long>(peak_bin) -
                       static_cast<long long>(expected)), 2);
    // Peak should be well above floor.
    EXPECT_GT(*it, -10.0f);
}

TEST(SignalStats, RssiOfUnitTone) {
    SignalStats s;
    constexpr float kPi = std::numbers::pi_v<float>;
    const std::size_t N = 4096;
    std::vector<std::complex<float>> x(N);
    for (std::size_t i = 0; i < N; ++i) {
        const float ph = 2.0f * kPi * 0.05f * static_cast<float>(i);
        x[i] = {std::cos(ph), std::sin(ph)};
    }
    s.process(x);
    auto snap = s.snapshot();
    // Unit-amplitude complex tone has |s|^2 = 1, so RSSI = 0 dBFS.
    EXPECT_NEAR(snap.rssi_dbfs, 0.0f, 0.1f);
    EXPECT_EQ(snap.total_samples, static_cast<std::uint64_t>(N));
}

TEST(SignalStats, EmptyInputDoesNotAlterState) {
    SignalStats s;
    s.process({});
    auto snap = s.snapshot();
    EXPECT_EQ(snap.total_samples, 0u);
}
