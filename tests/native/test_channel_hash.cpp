// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/crypto/ChannelCrypto.h"

#include <gtest/gtest.h>

#include <array>

using mrf::crypto::channel_hash;

TEST(ChannelHash, EmptyInputs) {
    std::array<std::uint8_t, 0> empty{};
    EXPECT_EQ(channel_hash("", empty), 0);
}

TEST(ChannelHash, NameOnly) {
    std::array<std::uint8_t, 0> empty{};
    // 'A' = 0x41, 'B' = 0x42 -> XOR = 0x03
    EXPECT_EQ(channel_hash("AB", empty), 0x03);
}

TEST(ChannelHash, PskOnly) {
    std::array<std::uint8_t, 3> psk{0x10, 0x20, 0x30};
    EXPECT_EQ(channel_hash("", psk), 0x10 ^ 0x20 ^ 0x30);
}

TEST(ChannelHash, NameAndPsk) {
    std::array<std::uint8_t, 2> psk{0xFF, 0x0F};
    // 'L'=0x4C, 'F'=0x46 -> 0x0A; XOR with psk: 0x0A ^ 0xFF ^ 0x0F = 0xFA
    EXPECT_EQ(channel_hash("LF", psk), 0xFA);
}
