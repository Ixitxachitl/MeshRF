// SPDX-License-Identifier: GPL-3.0-or-later
//
// Encode/decode Meshtastic MeshPacket protobuf payloads (the bytes that
// follow the 16-byte L1 header). Phase 4 placeholder.

#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace mrf::proto {

struct DecodedPayload {
    std::uint32_t portnum{};         // see meshtastic/protobufs portnums.proto
    std::vector<std::uint8_t> data;
    std::uint32_t request_id{};
    std::uint32_t reply_id{};
};

// Decode a (decrypted) Data submessage. Returns false on parse error.
bool decode_data_message(std::span<const std::uint8_t> in, DecodedPayload& out);

// Encode a Data submessage for transmission.
std::vector<std::uint8_t> encode_data_message(const DecodedPayload& in);

} // namespace mrf::proto
