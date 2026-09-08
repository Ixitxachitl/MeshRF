// SPDX-License-Identifier: GPL-3.0-or-later
//
// Overdrive detection. A front end driven past what the converter can
// represent does not produce a loud reading, it produces a meaningless one:
// samples stop following the wave, every level pins against the rail, and
// LoRa header FEC starts failing on frames whose chirps still look clean.

#include "mrf/dsp/SignalStats.h"

#include <gtest/gtest.h>

#include <complex>
#include <span>
#include <vector>

using namespace mrf::dsp;

namespace {
using cf = std::complex<float>;

SignalStats::Snapshot statistics_of(const std::vector<cf>& block) {
    SignalStats stats;
    stats.process(std::span<const cf>(block.data(), block.size()));
    return stats.snapshot();
}
} // namespace

TEST(SignalStatsClipping, AComfortableSignalIsNotClipping) {
    std::vector<cf> block(1024, cf{0.3f, -0.2f});
    const auto s = statistics_of(block);

    EXPECT_FLOAT_EQ(s.clipped, 0.0f);
    EXPECT_LT(s.rssi_dbfs, 0.0f);
}

// Either component railing distorts the sample, so both are counted.
TEST(SignalStatsClipping, EitherComponentAtTheRailCounts) {
    std::vector<cf> block(1000, cf{0.1f, 0.1f});
    block[0] = cf{0.999f, 0.0f};   // I railed
    block[1] = cf{0.0f, -0.999f};  // Q railed

    EXPECT_NEAR(statistics_of(block).clipped, 2.0 / 1000.0, 1e-6);
}

// A fully saturated block is what an overdriven receiver actually delivers,
// and it is where the level stops telling anything apart -- pinned at the
// +3.01 dBFS ceiling that both components at once produces.
TEST(SignalStatsClipping, ASaturatedBlockPinsAgainstTheCeiling) {
    std::vector<cf> block(1024, cf{1.0f, 1.0f});
    const auto s = statistics_of(block);

    EXPECT_FLOAT_EQ(s.clipped, 1.0f);
    EXPECT_NEAR(s.rssi_dbfs, 3.01f, 0.05f);
}

// The threshold is about damage, not about a stray sample on a peak.
TEST(SignalStatsClipping, AStraySampleIsNotAnOverdrive) {
    std::vector<cf> block(100'000, cf{0.2f, 0.2f});
    block[0] = cf{1.0f, 1.0f};

    // One in a hundred thousand is far below the tenth of a percent the app
    // treats as overdriven.
    EXPECT_LT(statistics_of(block).clipped, 0.001f);
}
