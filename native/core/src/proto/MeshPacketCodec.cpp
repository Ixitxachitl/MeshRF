// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/proto/MeshPacketCodec.h"

namespace mrf::proto {

bool decode_data_message(std::span<const std::uint8_t>, DecodedPayload&) {
    // TODO(phase-4): pull in nanopb or protobuf-c generated code from
    // meshtastic/protobufs (mesh.proto Data message) and parse here.
    return false;
}

std::vector<std::uint8_t> encode_data_message(const DecodedPayload&) {
    // TODO(phase-4): see decode_data_message.
    return {};
}

} // namespace mrf::proto
