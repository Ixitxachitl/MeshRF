// SPDX-License-Identifier: GPL-3.0-or-later
#include <gtest/gtest.h>

#include "mrf/dsp/Resampler.h"

#include <cmath>
#include <complex>
#include <numbers>
#include <vector>

using mrf::dsp::Resampler;
using cf = std::complex<float>;

namespace {

constexpr double kTwoPi = 2.0 * std::numbers::pi;

std::vector<cf> Tone(std::size_t n, double cycles_per_sample) {
    std::vector<cf> v(n);
    for (std::size_t i = 0; i < n; ++i) {
        const double a = kTwoPi * cycles_per_sample * static_cast<double>(i);
        v[i] = cf(static_cast<float>(std::cos(a)), static_cast<float>(std::sin(a)));
    }
    return v;
}

double MeanPower(std::span<const cf> v) {
    double sum = 0.0;
    for (const auto& s : v) sum += static_cast<double>(std::norm(s));
    return v.empty() ? 0.0 : sum / static_cast<double>(v.size());
}

std::vector<cf> All(Resampler& r, std::span<const cf> in, std::size_t chunk) {
    std::vector<cf> out;
    for (std::size_t i = 0; i < in.size(); i += chunk) {
        const auto take = std::min(chunk, in.size() - i);
        auto part = r.process(in.subspan(i, take));
        out.insert(out.end(), part.begin(), part.end());
    }
    return out;
}

} // namespace

TEST(Resampler, ReducesTheRateRatio) {
    Resampler r(10'000'000u, 1'000'000u);
    EXPECT_EQ(r.up_factor(), 1u);
    EXPECT_EQ(r.down_factor(), 10u);

    Resampler odd(2'400'000u, 1'000'000u);
    EXPECT_EQ(odd.up_factor(), 5u);
    EXPECT_EQ(odd.down_factor(), 12u);
}

TEST(Resampler, OutputCountFollowsTheRateRatio) {
    Resampler r(10'000'000u, 1'000'000u);
    auto in = Tone(100'000, 0.01);
    auto out = All(r, in, in.size());
    EXPECT_NEAR(static_cast<double>(out.size()), 10'000.0, 2.0);
}

/// <summary>
/// The stream does not know where the caller's blocks end. This is the whole
/// risk in how the filter keeps its history, so it is checked against block
/// sizes far smaller than the filter is long, and against sizes that share no
/// factor with the rate ratio.
/// </summary>
TEST(Resampler, SplittingTheInputChangesNothing) {
    auto in = Tone(60'000, 0.013);

    Resampler whole(10'000'000u, 1'000'000u);
    const auto reference = All(whole, in, in.size());

    for (std::size_t chunk : {std::size_t{1}, std::size_t{7}, std::size_t{999},
                              std::size_t{4096}, std::size_t{59'999}}) {
        Resampler split(10'000'000u, 1'000'000u);
        const auto got = All(split, in, chunk);
        ASSERT_EQ(got.size(), reference.size()) << "chunk " << chunk;
        for (std::size_t i = 0; i < got.size(); ++i) {
            ASSERT_FLOAT_EQ(got[i].real(), reference[i].real()) << "chunk " << chunk << " at " << i;
            ASSERT_FLOAT_EQ(got[i].imag(), reference[i].imag()) << "chunk " << chunk << " at " << i;
        }
    }
}

TEST(Resampler, KeepsAToneInsideTheNewBandAndDropsOneOutside) {
    auto passband = Tone(200'000, 0.02);   // 0.02 of 10 MHz = 200 kHz, inside 500 kHz
    auto stopband = Tone(200'000, 0.20);   // 2 MHz, far outside

    Resampler a(10'000'000u, 1'000'000u);
    Resampler b(10'000'000u, 1'000'000u);
    const auto kept = All(a, passband, 8192);
    const auto dropped = All(b, stopband, 8192);

    // Skip the filter's fill-up at the start of each stream.
    const std::size_t skip = 200;
    ASSERT_GT(kept.size(), skip);
    const double in_band = MeanPower(std::span(kept).subspan(skip));
    const double out_band = MeanPower(std::span(dropped).subspan(skip));

    EXPECT_NEAR(in_band, 1.0, 0.1);
    EXPECT_LT(out_band, in_band * 1e-4);
}

/// <summary>Transmit runs it the other way, from the modem rate up to the
/// device rate, so the interpolating path carries traffic too.</summary>
TEST(Resampler, InterpolatesAsWellAsDecimates) {
    Resampler up(1'000'000u, 4'000'000u);
    EXPECT_EQ(up.up_factor(), 4u);
    EXPECT_EQ(up.down_factor(), 1u);

    auto in = Tone(20'000, 0.05);
    const auto out = All(up, in, 1024);
    EXPECT_NEAR(static_cast<double>(out.size()), 80'000.0, 4.0);

    const std::size_t skip = 400;
    ASSERT_GT(out.size(), skip);
    EXPECT_NEAR(MeanPower(std::span(out).subspan(skip)), 1.0, 0.1);
}
