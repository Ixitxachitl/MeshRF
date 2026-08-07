// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/crypto/ChannelCrypto.h"

#include <gtest/gtest.h>

#include <array>
#include <vector>

using mrf::crypto::aes_ctr_xcrypt;

namespace {

std::vector<std::uint8_t> make_plaintext(std::size_t n) {
    std::vector<std::uint8_t> v(n);
    for (std::size_t i = 0; i < n; ++i) v[i] = static_cast<std::uint8_t>(i * 7 + 1);
    return v;
}

} // namespace

TEST(ChannelCrypto, ActuallyChangesData) {
    // Regression guard: aes_ctr_xcrypt used to be an unwired no-op stub that
    // left `data` untouched. This must never regress silently.
    std::array<std::uint8_t, 16> key{};
    for (std::size_t i = 0; i < key.size(); ++i) key[i] = static_cast<std::uint8_t>(i);

    auto plaintext = make_plaintext(40);
    auto ciphertext = plaintext;
    aes_ctr_xcrypt(key, /*packet_id=*/0x1122334455667788ULL, /*sender_node_id=*/0xAABBCCDDULL, ciphertext);

    EXPECT_NE(plaintext, ciphertext);
}

TEST(ChannelCrypto, RoundTripsForAes128AndAes256) {
    for (std::size_t key_len : {16u, 32u}) {
        std::vector<std::uint8_t> key(key_len);
        for (std::size_t i = 0; i < key_len; ++i) key[i] = static_cast<std::uint8_t>(i * 3 + 1);

        auto plaintext = make_plaintext(77); // spans multiple 16-byte blocks, non-multiple length
        auto buf = plaintext;

        aes_ctr_xcrypt(key, 42, 99, buf);
        EXPECT_NE(buf, plaintext) << "key_len=" << key_len;

        aes_ctr_xcrypt(key, 42, 99, buf); // CTR is its own inverse with the same nonce
        EXPECT_EQ(buf, plaintext) << "key_len=" << key_len;
    }
}

TEST(ChannelCrypto, DifferentNoncesProduceDifferentKeystreams) {
    std::array<std::uint8_t, 16> key{};
    for (std::size_t i = 0; i < key.size(); ++i) key[i] = static_cast<std::uint8_t>(i);

    auto a = make_plaintext(32);
    auto b = make_plaintext(32);

    aes_ctr_xcrypt(key, /*packet_id=*/1, /*sender_node_id=*/1, a);
    aes_ctr_xcrypt(key, /*packet_id=*/2, /*sender_node_id=*/1, b);

    EXPECT_NE(a, b);
}

TEST(ChannelCrypto, RejectsInvalidKeyLength) {
    std::array<std::uint8_t, 15> bad_key{};
    std::vector<std::uint8_t> data(16, 0);
    EXPECT_THROW(aes_ctr_xcrypt(bad_key, 0, 0, data), std::invalid_argument);
}
