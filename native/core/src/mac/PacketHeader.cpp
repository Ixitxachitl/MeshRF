// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/mac/PacketHeader.h"

namespace mrf::mac {

namespace {

constexpr void store_u32_le(std::uint8_t* p, std::uint32_t v) noexcept {
    p[0] = static_cast<std::uint8_t>(v & 0xFFu);
    p[1] = static_cast<std::uint8_t>((v >> 8) & 0xFFu);
    p[2] = static_cast<std::uint8_t>((v >> 16) & 0xFFu);
    p[3] = static_cast<std::uint8_t>((v >> 24) & 0xFFu);
}

constexpr std::uint32_t load_u32_le(const std::uint8_t* p) noexcept {
    return static_cast<std::uint32_t>(p[0]) |
           (static_cast<std::uint32_t>(p[1]) << 8) |
           (static_cast<std::uint32_t>(p[2]) << 16) |
           (static_cast<std::uint32_t>(p[3]) << 24);
}

} // namespace

std::array<std::uint8_t, PacketHeader::kSize> PacketHeader::to_bytes() const noexcept {
    std::array<std::uint8_t, kSize> out{};
    store_u32_le(out.data() + 0, dest);
    store_u32_le(out.data() + 4, sender);
    store_u32_le(out.data() + 8, packet_id);
    out[12] = flags;
    out[13] = channel_hash;
    out[14] = next_hop;
    out[15] = relay_node;
    return out;
}

std::optional<PacketHeader> PacketHeader::from_bytes(std::span<const std::uint8_t> bytes) noexcept {
    if (bytes.size() < kSize) return std::nullopt;
    PacketHeader h{};
    h.dest         = load_u32_le(bytes.data() + 0);
    h.sender       = load_u32_le(bytes.data() + 4);
    h.packet_id    = load_u32_le(bytes.data() + 8);
    h.flags        = bytes[12];
    h.channel_hash = bytes[13];
    h.next_hop     = bytes[14];
    h.relay_node   = bytes[15];
    return h;
}

} // namespace mrf::mac
