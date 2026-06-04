// SPDX-License-Identifier: GPL-3.0-or-later
//
// Channel-level AES256-CTR encryption (Meshtastic primary/secondary channels).
// PKC for direct messages lives in a separate translation unit (Phase 4).

#pragma once

#include <array>
#include <cstdint>
#include <span>
#include <string_view>
#include <vector>

namespace mrf::crypto {

using ChannelKey = std::vector<std::uint8_t>; // 16 (AES128) or 32 (AES256) bytes

// Compute the 1-byte channel hash hint stored in the packet header.
// Mirrors firmware Channels::generateHash: XOR-fold of name bytes XOR each
// byte of the PSK.
[[nodiscard]] std::uint8_t channel_hash(std::string_view name, std::span<const std::uint8_t> psk) noexcept;

// AES-CTR encrypt/decrypt the payload in place. Nonce = packet_id (LE u64) ||
// sender (LE u64). Same primitive used for both directions (CTR).
void aes_ctr_xcrypt(std::span<const std::uint8_t> key,
                    std::uint64_t packet_id,
                    std::uint64_t sender_node_id,
                    std::span<std::uint8_t> data);

} // namespace mrf::crypto
