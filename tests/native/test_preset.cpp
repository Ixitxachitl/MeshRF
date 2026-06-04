// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/modem/Preset.h"

#include <gtest/gtest.h>

using mrf::modem::Preset;
using mrf::modem::params_for;

TEST(Preset, LongFastIsDefaultMeshtasticPreset) {
    const auto p = params_for(Preset::LongFast);
    EXPECT_EQ(p.spreading_factor, 11);
    EXPECT_EQ(p.bandwidth_hz, 250'000u);
    EXPECT_EQ(p.coding_rate, 5);
    EXPECT_EQ(p.sync_word, 0x2B);
    EXPECT_EQ(p.preamble_symbols, 16);
    EXPECT_TRUE(p.explicit_header);
    EXPECT_TRUE(p.crc_enabled);
}

TEST(Preset, ShortTurboIs500kHz) {
    const auto p = params_for(Preset::ShortTurbo);
    EXPECT_EQ(p.spreading_factor, 7);
    EXPECT_EQ(p.bandwidth_hz, 500'000u);
}

TEST(Preset, LongModerateEnablesLowDataRateOptimize) {
    const auto p = params_for(Preset::LongModerate);
    EXPECT_EQ(p.bandwidth_hz, 125'000u);
    EXPECT_EQ(p.coding_rate, 8);
    EXPECT_TRUE(p.low_data_rate_optimize);
}
