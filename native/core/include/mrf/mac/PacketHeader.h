// SPDX-License-Identifier: GPL-3.0-or-later
//
// Meshtastic L1 packet header (16 bytes, little-endian).
// Spec: https://meshtastic.org/docs/overview/mesh-algo/#layer-1-unreliable-zero-hop-messaging
// Cross-reference: meshtastic/firmware src/mesh/MeshTypes.h, Router.cpp.

#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <span>

namespace mrf::mac {

// On-air packet header. NodeIDs are little-endian uint32. The flags byte
// packs hop_limit (3), want_ack (1), via_mqtt (1), hop_start (3) — see below.
struct PacketHeader {
    static constexpr std::size_t kSize = 16;
    static constexpr std::uint32_t kBroadcast = 0xFFFFFFFFu;

    std::uint32_t dest{};        // 0x00 dest NodeID (0xFFFFFFFF = broadcast)
    std::uint32_t sender{};      // 0x04 sender NodeID
    std::uint32_t packet_id{};   // 0x08 sender's packet ID
    std::uint8_t  flags{};       // 0x0C bit-packed; use accessors below
    std::uint8_t  channel_hash{};// 0x0D decryption hint (XOR-fold of name+PSK)
    std::uint8_t  next_hop{};    // 0x0E next-hop relay (low byte of NodeID)
    std::uint8_t  relay_node{};  // 0x0F current relay's NodeID low byte

    // ---- flag bit accessors (mirror MeshPacket protobuf semantics) -------
    // Bits:  [0..2]=hop_limit  [3]=want_ack  [4]=via_mqtt  [5..7]=hop_start
    [[nodiscard]] std::uint8_t hop_limit() const noexcept { return flags & 0x07u; }
    void set_hop_limit(std::uint8_t v) noexcept {
        flags = static_cast<std::uint8_t>((flags & ~0x07u) | (v & 0x07u));
    }

    [[nodiscard]] bool want_ack() const noexcept { return (flags & 0x08u) != 0; }
    void set_want_ack(bool v) noexcept {
        flags = static_cast<std::uint8_t>(v ? (flags | 0x08u) : (flags & ~0x08u));
    }

    [[nodiscard]] bool via_mqtt() const noexcept { return (flags & 0x10u) != 0; }
    void set_via_mqtt(bool v) noexcept {
        flags = static_cast<std::uint8_t>(v ? (flags | 0x10u) : (flags & ~0x10u));
    }

    [[nodiscard]] std::uint8_t hop_start() const noexcept {
        return static_cast<std::uint8_t>((flags >> 5) & 0x07u);
    }
    void set_hop_start(std::uint8_t v) noexcept {
        flags = static_cast<std::uint8_t>((flags & ~0xE0u) | ((v & 0x07u) << 5));
    }

    // ---- (de)serialisation ----------------------------------------------
    [[nodiscard]] std::array<std::uint8_t, kSize> to_bytes() const noexcept;
    static std::optional<PacketHeader> from_bytes(std::span<const std::uint8_t> bytes) noexcept;

    [[nodiscard]] bool is_broadcast() const noexcept { return dest == kBroadcast; }

    friend bool operator==(const PacketHeader&, const PacketHeader&) = default;
};

} // namespace mrf::mac
