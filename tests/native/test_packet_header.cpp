// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/mac/PacketHeader.h"

#include <gtest/gtest.h>

using mrf::mac::PacketHeader;

TEST(PacketHeader, RoundTripBytes) {
    PacketHeader h{};
    h.dest = 0xDEADBEEFu;
    h.sender = 0x12345678u;
    h.packet_id = 0xCAFEF00Du;
    h.set_hop_limit(5);
    h.set_want_ack(true);
    h.set_via_mqtt(false);
    h.set_hop_start(7);
    h.channel_hash = 0x2B;
    h.next_hop = 0xAB;
    h.relay_node = 0xCD;

    auto bytes = h.to_bytes();
    ASSERT_EQ(bytes.size(), PacketHeader::kSize);

    auto round = PacketHeader::from_bytes(bytes);
    ASSERT_TRUE(round.has_value());
    EXPECT_EQ(*round, h);
}

TEST(PacketHeader, LittleEndianLayout) {
    PacketHeader h{};
    h.dest = 0x04030201u;
    h.sender = 0x08070605u;
    h.packet_id = 0x0C0B0A09u;
    h.flags = 0xAA;
    h.channel_hash = 0xBB;
    h.next_hop = 0xCC;
    h.relay_node = 0xDD;

    const auto b = h.to_bytes();
    // Bytes 0..3 = dest LE
    EXPECT_EQ(b[0], 0x01); EXPECT_EQ(b[1], 0x02);
    EXPECT_EQ(b[2], 0x03); EXPECT_EQ(b[3], 0x04);
    // Bytes 4..7 = sender LE
    EXPECT_EQ(b[4], 0x05); EXPECT_EQ(b[7], 0x08);
    // Bytes 8..11 = packet_id LE
    EXPECT_EQ(b[8], 0x09); EXPECT_EQ(b[11], 0x0C);
    // Trailing bytes
    EXPECT_EQ(b[12], 0xAA);
    EXPECT_EQ(b[13], 0xBB);
    EXPECT_EQ(b[14], 0xCC);
    EXPECT_EQ(b[15], 0xDD);
}

TEST(PacketHeader, FlagBitfields) {
    PacketHeader h{};

    h.set_hop_limit(7);
    h.set_want_ack(true);
    h.set_via_mqtt(true);
    h.set_hop_start(3);

    EXPECT_EQ(h.hop_limit(), 7);
    EXPECT_TRUE(h.want_ack());
    EXPECT_TRUE(h.via_mqtt());
    EXPECT_EQ(h.hop_start(), 3);

    h.set_want_ack(false);
    EXPECT_FALSE(h.want_ack());
    EXPECT_TRUE(h.via_mqtt()); // unaffected
    EXPECT_EQ(h.hop_limit(), 7);
    EXPECT_EQ(h.hop_start(), 3);

    // Setting hop_start should not corrupt other fields.
    h.set_hop_start(0);
    EXPECT_EQ(h.hop_start(), 0);
    EXPECT_EQ(h.hop_limit(), 7);
    EXPECT_TRUE(h.via_mqtt());
}

TEST(PacketHeader, BroadcastDetection) {
    PacketHeader h{};
    h.dest = PacketHeader::kBroadcast;
    EXPECT_TRUE(h.is_broadcast());
    h.dest = 0x12345678;
    EXPECT_FALSE(h.is_broadcast());
}

TEST(PacketHeader, RejectShortBuffer) {
    std::array<std::uint8_t, 8> too_short{};
    EXPECT_FALSE(PacketHeader::from_bytes(too_short).has_value());
}
