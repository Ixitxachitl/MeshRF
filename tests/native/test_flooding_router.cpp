// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/router/FloodingRouter.h"

#include <gtest/gtest.h>

using mrf::mac::PacketHeader;
using mrf::router::FloodingRouter;

namespace {
PacketHeader make(std::uint32_t sender, std::uint32_t pid) {
    PacketHeader h{};
    h.sender = sender;
    h.packet_id = pid;
    return h;
}
} // namespace

TEST(FloodingRouter, FirstSeenIsTrueDuplicateIsFalse) {
    FloodingRouter r;
    EXPECT_TRUE(r.observe(make(1, 100)));
    EXPECT_FALSE(r.observe(make(1, 100)));
    EXPECT_TRUE(r.observe(make(1, 101)));
    EXPECT_TRUE(r.observe(make(2, 100)));
}

TEST(FloodingRouter, EvictsOldestWhenAtCapacity) {
    FloodingRouter r{3};
    r.observe(make(1, 1));
    r.observe(make(1, 2));
    r.observe(make(1, 3));
    EXPECT_EQ(r.tracked_count(), 3u);

    // Inserting a 4th evicts (1,1)
    r.observe(make(1, 4));
    EXPECT_EQ(r.tracked_count(), 3u);

    // (1,1) should now be considered new again
    EXPECT_TRUE(r.observe(make(1, 1)));
    // Inserting (1,1) just evicted (1,2); it is also new again now.
    EXPECT_TRUE(r.observe(make(1, 2)));
    // But (1,4) is still tracked.
    EXPECT_FALSE(r.observe(make(1, 4)));
}
