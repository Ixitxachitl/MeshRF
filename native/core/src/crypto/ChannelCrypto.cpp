// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/crypto/ChannelCrypto.h"

#include <stdexcept>

// AES-CTR is built out of a raw AES-ECB single-block primitive (the standard
// construction: keystream_block = AES_ECB_Encrypt(key, counter_block)).
// libsodium (linked elsewhere in this target) does not expose a generic
// AES-128/256-CTR or ECB primitive, so each platform uses its native/system
// crypto library instead:
//   - Windows: CNG (bcrypt.lib)
//   - Everywhere else: mbedtls, which is also what the Meshtastic firmware
//     itself uses for CryptoEngine, keeping behavior consistent.
#if defined(_WIN32)
#  include <windows.h>
#  include <bcrypt.h>
#else
#  include <mbedtls/aes.h>
#  include <cstring>
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

namespace {

#if defined(_WIN32)

// RAII wrapper around a BCrypt AES-ECB key, used only to encrypt individual
// 16-byte counter blocks.
class AesEcbKey {
public:
    explicit AesEcbKey(std::span<const std::uint8_t> key) {
        NTSTATUS status = BCryptOpenAlgorithmProvider(&alg_, BCRYPT_AES_ALGORITHM, nullptr, 0);
        if (status < 0) throw std::runtime_error("aes_ctr_xcrypt: BCryptOpenAlgorithmProvider failed");

        status = BCryptSetProperty(alg_, BCRYPT_CHAINING_MODE,
                                    reinterpret_cast<PUCHAR>(const_cast<wchar_t*>(BCRYPT_CHAIN_MODE_ECB)),
                                    sizeof(BCRYPT_CHAIN_MODE_ECB), 0);
        if (status < 0) {
            BCryptCloseAlgorithmProvider(alg_, 0);
            throw std::runtime_error("aes_ctr_xcrypt: BCryptSetProperty(ECB) failed");
        }

        status = BCryptGenerateSymmetricKey(alg_, &hkey_, nullptr, 0,
                                             reinterpret_cast<PUCHAR>(const_cast<std::uint8_t*>(key.data())),
                                             static_cast<ULONG>(key.size()), 0);
        if (status < 0) {
            BCryptCloseAlgorithmProvider(alg_, 0);
            throw std::runtime_error("aes_ctr_xcrypt: BCryptGenerateSymmetricKey failed");
        }
    }

    ~AesEcbKey() {
        if (hkey_) BCryptDestroyKey(hkey_);
        if (alg_) BCryptCloseAlgorithmProvider(alg_, 0);
    }

    AesEcbKey(const AesEcbKey&) = delete;
    AesEcbKey& operator=(const AesEcbKey&) = delete;

    // Encrypts exactly one 16-byte block in place.
    void encrypt_block(std::array<std::uint8_t, 16>& block) const {
        ULONG out_len = 0;
        NTSTATUS status = BCryptEncrypt(hkey_, block.data(), static_cast<ULONG>(block.size()), nullptr,
                                         nullptr, 0, block.data(), static_cast<ULONG>(block.size()), &out_len, 0);
        if (status < 0 || out_len != block.size())
            throw std::runtime_error("aes_ctr_xcrypt: BCryptEncrypt failed");
    }

private:
    BCRYPT_ALG_HANDLE alg_ = nullptr;
    BCRYPT_KEY_HANDLE hkey_ = nullptr;
};

void secure_zero(std::array<std::uint8_t, 16>& block) {
    SecureZeroMemory(block.data(), block.size());
}

#else  // !_WIN32

// RAII wrapper around an mbedtls AES-ECB encryption key, used the same way
// as the BCrypt version above: encrypt individual 16-byte counter blocks.
class AesEcbKey {
public:
    explicit AesEcbKey(std::span<const std::uint8_t> key) {
        mbedtls_aes_init(&ctx_);
        int rc = mbedtls_aes_setkey_enc(&ctx_, key.data(), static_cast<unsigned>(key.size() * 8));
        if (rc != 0) {
            mbedtls_aes_free(&ctx_);
            throw std::runtime_error("aes_ctr_xcrypt: mbedtls_aes_setkey_enc failed");
        }
    }

    ~AesEcbKey() { mbedtls_aes_free(&ctx_); }

    AesEcbKey(const AesEcbKey&) = delete;
    AesEcbKey& operator=(const AesEcbKey&) = delete;

    // Encrypts exactly one 16-byte block in place.
    void encrypt_block(std::array<std::uint8_t, 16>& block) const {
        std::array<std::uint8_t, 16> out{};
        int rc = mbedtls_aes_crypt_ecb(const_cast<mbedtls_aes_context*>(&ctx_), MBEDTLS_AES_ENCRYPT,
                                        block.data(), out.data());
        if (rc != 0) throw std::runtime_error("aes_ctr_xcrypt: mbedtls_aes_crypt_ecb failed");
        block = out;
    }

private:
    mbedtls_aes_context ctx_{};
};

void secure_zero(std::array<std::uint8_t, 16>& block) {
    // Defeats dead-store elimination without relying on the non-portable
    // memset_s (not guaranteed available across libcs).
    volatile std::uint8_t* p = block.data();
    for (std::size_t i = 0; i < block.size(); ++i) p[i] = 0;
}

#endif // !_WIN32

// Increments a 16-byte big-endian counter (matches mbedtls_aes_crypt_ctr's
// default nonce_counter increment, used by firmware's CryptoEngine).
void increment_counter(std::array<std::uint8_t, 16>& ctr) noexcept {
    for (int i = 15; i >= 0; --i) {
        if (++ctr[i] != 0) break;
    }
}

} // namespace

void aes_ctr_xcrypt(std::span<const std::uint8_t> key,
                    std::uint64_t packet_id,
                    std::uint64_t sender_node_id,
                    std::span<std::uint8_t> data) {
    if (key.size() != 16 && key.size() != 32)
        throw std::invalid_argument("aes_ctr_xcrypt: key must be 16 or 32 bytes");

    // Nonce layout (16 bytes): packet_id (8 LE) || sender (8 LE). Matches
    // firmware CryptoEngine::initNonce.
    std::array<std::uint8_t, 16> counter{};
    for (int i = 0; i < 8; ++i) counter[i]     = static_cast<std::uint8_t>((packet_id >> (8 * i)) & 0xFF);
    for (int i = 0; i < 8; ++i) counter[8 + i] = static_cast<std::uint8_t>((sender_node_id >> (8 * i)) & 0xFF);

    AesEcbKey aes(key);
    std::size_t offset = 0;
    while (offset < data.size()) {
        std::array<std::uint8_t, 16> keystream = counter;
        aes.encrypt_block(keystream);

        const std::size_t block_len = std::min<std::size_t>(16, data.size() - offset);
        for (std::size_t i = 0; i < block_len; ++i)
            data[offset + i] ^= keystream[i];

        offset += block_len;
        increment_counter(counter);
    }

    secure_zero(counter);
}

} // namespace mrf::crypto
