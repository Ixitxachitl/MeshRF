// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/crypto/ChannelCrypto.h"

#include <stdexcept>

#if defined(MRF_HAVE_SODIUM)
#  include <sodium.h>
#endif

namespace mrf::crypto {

std::uint8_t channel_hash(std::string_view name, std::span<const std::uint8_t> psk) noexcept {
    // Reproduces firmware Channels::generateHash:
    //   h = 0; for c in name: h ^= c; for b in psk: h ^= b;
    std::uint8_t h = 0;
    for (char c : name) h ^= static_cast<std::uint8_t>(c);
    for (auto b : psk) h ^= b;
    return h;
}

void aes_ctr_xcrypt(std::span<const std::uint8_t> key,
                    std::uint64_t packet_id,
                    std::uint64_t sender_node_id,
                    std::span<std::uint8_t> data) {
    if (key.size() != 16 && key.size() != 32)
        throw std::invalid_argument("aes_ctr_xcrypt: key must be 16 or 32 bytes");

    // Nonce layout (16 bytes): packet_id (8 LE) || sender (8 LE). Matches
    // firmware CryptoEngine::initNonce.
    [[maybe_unused]] std::array<std::uint8_t, 16> nonce{};
    for (int i = 0; i < 8; ++i) nonce[i]     = static_cast<std::uint8_t>((packet_id >> (8 * i)) & 0xFF);
    for (int i = 0; i < 8; ++i) nonce[8 + i] = static_cast<std::uint8_t>((sender_node_id >> (8 * i)) & 0xFF);

#if defined(MRF_HAVE_SODIUM)
    // TODO(phase-4): libsodium does not expose raw AES-CTR; we'll use either
    // its AES256-GCM primitive in CTR-only mode (advanced API) or link
    // BoringSSL/OpenSSL for AES-CTR. Placeholder: leave data unmodified.
    (void)key; (void)data;
#else
    (void)key; (void)data;
#endif
}

} // namespace mrf::crypto
