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

TEST(Preset, LongSlowIsSF12With125kHzAndLdro) {
    const auto p = params_for(Preset::LongSlow);
    EXPECT_EQ(p.spreading_factor, 12);
    EXPECT_EQ(p.bandwidth_hz, 125'000u);
    EXPECT_EQ(p.coding_rate, 8);
    EXPECT_TRUE(p.low_data_rate_optimize);
}

TEST(Preset, LiteFastIsSF9With125kHz) {
    const auto p = params_for(Preset::LiteFast);
    EXPECT_EQ(p.spreading_factor, 9);
    EXPECT_EQ(p.bandwidth_hz, 125'000u);
    EXPECT_EQ(p.coding_rate, 5);
}

TEST(Preset, LiteSlowIsSF10With125kHz) {
    const auto p = params_for(Preset::LiteSlow);
    EXPECT_EQ(p.spreading_factor, 10);
    EXPECT_EQ(p.bandwidth_hz, 125'000u);
    EXPECT_EQ(p.coding_rate, 5);
}

TEST(Preset, NarrowFastIsSF7With62_5kHz) {
    const auto p = params_for(Preset::NarrowFast);
    EXPECT_EQ(p.spreading_factor, 7);
    EXPECT_EQ(p.bandwidth_hz, 62'500u);
    EXPECT_EQ(p.coding_rate, 6);
}

TEST(Preset, NarrowSlowIsSF8With62_5kHz) {
    const auto p = params_for(Preset::NarrowSlow);
    EXPECT_EQ(p.spreading_factor, 8);
    EXPECT_EQ(p.bandwidth_hz, 62'500u);
    EXPECT_EQ(p.coding_rate, 6);
}

TEST(Preset, TinyFastIsSF7With15_6kHz) {
    const auto p = params_for(Preset::TinyFast);
    EXPECT_EQ(p.spreading_factor, 7);
    EXPECT_EQ(p.bandwidth_hz, 15'600u);
    EXPECT_EQ(p.coding_rate, 5);
}

TEST(Preset, TinySlowIsSF8With15_6kHzAndLdro) {
    const auto p = params_for(Preset::TinySlow);
    EXPECT_EQ(p.spreading_factor, 8);
    EXPECT_EQ(p.bandwidth_hz, 15'600u);
    EXPECT_EQ(p.coding_rate, 6);
    EXPECT_TRUE(p.low_data_rate_optimize);
}

TEST(Preset, MediumTurboIsSF9With500kHz) {
    const auto p = params_for(Preset::MediumTurbo);
    EXPECT_EQ(p.spreading_factor, 9);
    EXPECT_EQ(p.bandwidth_hz, 500'000u);
    EXPECT_EQ(p.coding_rate, 5);
}
