// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include "mrf/mac/PacketHeader.h"

#include <cstdint>
#include <deque>
#include <unordered_set>

namespace mrf::router {

// Managed flooding router (Phase 5 placeholder).
//
// Tracks recently-seen (sender, packet_id) pairs and decides whether to
// rebroadcast an incoming packet.
class FloodingRouter {
public:
    explicit FloodingRouter(std::size_t dedup_capacity = 64);

    // Record a packet that was received from the air. Returns true if this
    // is the first time we've seen it (caller should consider rebroadcasting
    // if hop_limit > 0); false if it's a duplicate.
    bool observe(const mac::PacketHeader& hdr);

    // Number of distinct packets currently tracked for deduplication.
    std::size_t tracked_count() const noexcept { return ids_.size(); }

private:
    struct Key {
        std::uint32_t sender;
        std::uint32_t packet_id;
        bool operator==(const Key& o) const noexcept = default;
    };
    struct KeyHash {
        std::size_t operator()(const Key& k) const noexcept {
            return (static_cast<std::size_t>(k.sender) << 32) ^ k.packet_id;
        }
    };

    std::size_t capacity_;
    std::deque<Key> order_;
    std::unordered_set<Key, KeyHash> ids_;
};

} // namespace mrf::router
