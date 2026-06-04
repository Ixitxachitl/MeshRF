// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/modem/LoraDecoder.h"

#include <algorithm>
#include <bit>
#include <cstring>
#include <stdexcept>

// Direct ports of myriadrf/LoRa-SDR's LoRaCodes.hpp (used verbatim by
// SDRangel's ChirpChat plugin), preserving bit/index conventions exactly.

namespace mrf::modem::lora {

std::uint16_t from_gray(std::uint16_t g) noexcept {
    g = static_cast<std::uint16_t>(g ^ (g >> 8));
    g = static_cast<std::uint16_t>(g ^ (g >> 4));
    g = static_cast<std::uint16_t>(g ^ (g >> 2));
    g = static_cast<std::uint16_t>(g ^ (g >> 1));
    return g;
}

std::uint16_t symbol_to_bits(std::uint16_t symbol_value,
                              std::uint8_t spreading_factor,
                              bool low_data_rate_optimize) noexcept {
    (void)low_data_rate_optimize;
    constexpr int spread = 4; // = 1 << deBits, deBits=2 (LoRa header / LDRO)
    const int nb_symbols_eff = 1 << (spreading_factor - 2);
    const std::uint16_t reduced = static_cast<std::uint16_t>(
        ((symbol_value + spread / 2 - 1) / spread) % nb_symbols_eff);
    return to_gray(reduced); // matches SDRangel `binaryToGray16`
}

std::uint16_t gray_demap(std::uint16_t symbol_value,
                          std::uint8_t spreading_factor,
                          std::uint8_t ppm) noexcept {
    const int diff = static_cast<int>(spreading_factor) - static_cast<int>(ppm);
    if (diff <= 0) {
        return to_gray(symbol_value);
    }
    const int offset = (1 << diff) / 2;
    const std::uint16_t reduced = static_cast<std::uint16_t>(
        (symbol_value + offset) >> diff);
    return to_gray(reduced);
}

std::vector<std::uint8_t> deinterleave(std::span<const std::uint16_t> symbols_bits,
                                        std::uint8_t sf_app,
                                        std::uint8_t cr_app) {
    if (cr_app < 4 || cr_app > 8)
        throw std::invalid_argument("deinterleave: cr_app out of range");
    if (symbols_bits.size() != cr_app)
        throw std::invalid_argument("deinterleave: symbols_bits size != cr_app");

    std::vector<std::uint8_t> codewords(sf_app, 0);
    // Per LoRa-SDR `diagonalDeterleaveSx`:
    //   codeword[(m + k) mod PPM] bit k = symbol[k] bit m
    for (std::uint8_t k = 0; k < cr_app; ++k) {
        for (std::uint8_t m = 0; m < sf_app; ++m) {
            const std::uint8_t i = static_cast<std::uint8_t>((m + k) % sf_app);
            const std::uint8_t bit = static_cast<std::uint8_t>(
                (symbols_bits[k] >> m) & 1u);
            codewords[i] = static_cast<std::uint8_t>(codewords[i] | (bit << k));
        }
    }
    return codewords;
}

std::uint8_t hamming_decode(std::uint8_t codeword, std::uint8_t cr, bool& corrected) noexcept {
    corrected = false;
    // -- CR=1 (4/5): single-parity, detection only --------------------
    if (cr == 1) {
        const std::uint8_t x0 = (codeword ^ (codeword >> 2)) & 0x01;
        const std::uint8_t x  = (x0 ^ (codeword >> 1) ^ (codeword >> 4)) & 0x01;
        if (x) corrected = true;
        return static_cast<std::uint8_t>(codeword & 0x0F);
    }
    // -- CR=2 (4/6): two-parity, detection only ------------------------
    if (cr == 2) {
        const std::uint8_t x = (codeword ^ (codeword >> 1) ^ (codeword >> 2) ^ (codeword >> 4)) & 0x01;
        const std::uint8_t y = (x ^ codeword ^ (codeword >> 3) ^ (codeword >> 5)) & 0x01;
        if (x | y) corrected = true;
        return static_cast<std::uint8_t>(codeword & 0x0F);
    }
    // -- CR=3 (4/7): Hamming 7/4, single-bit correction ----------------
    if (cr == 3) {
        const std::uint8_t b0 = (codeword >> 0) & 1u;
        const std::uint8_t b1 = (codeword >> 1) & 1u;
        const std::uint8_t b2 = (codeword >> 2) & 1u;
        const std::uint8_t b3 = (codeword >> 3) & 1u;
        const std::uint8_t b4 = (codeword >> 4) & 1u;
        const std::uint8_t b5 = (codeword >> 5) & 1u;
        const std::uint8_t b6 = (codeword >> 6) & 1u;

        const std::uint8_t p0 = b0 ^ b1 ^ b2 ^ b4;
        const std::uint8_t p1 = b1 ^ b2 ^ b3 ^ b5;
        const std::uint8_t p2 = b0 ^ b1 ^ b3 ^ b6;
        const std::uint8_t parity = static_cast<std::uint8_t>(
            (p0 << 0) | (p1 << 1) | (p2 << 2));
        if (parity != 0) corrected = true;
        switch (parity) {
            case 0x5: return static_cast<std::uint8_t>((codeword ^ 1) & 0xF);
            case 0x7: return static_cast<std::uint8_t>((codeword ^ 2) & 0xF);
            case 0x3: return static_cast<std::uint8_t>((codeword ^ 4) & 0xF);
            case 0x6: return static_cast<std::uint8_t>((codeword ^ 8) & 0xF);
            default:  return static_cast<std::uint8_t>(codeword & 0xF);
        }
    }
    const std::uint8_t b0 = (codeword >> 0) & 1u;
    const std::uint8_t b1 = (codeword >> 1) & 1u;
    const std::uint8_t b2 = (codeword >> 2) & 1u;
    const std::uint8_t b3 = (codeword >> 3) & 1u;
    const std::uint8_t b4 = (codeword >> 4) & 1u;
    const std::uint8_t b5 = (codeword >> 5) & 1u;
    const std::uint8_t b6 = (codeword >> 6) & 1u;
    const std::uint8_t b7 = (codeword >> 7) & 1u;

    const std::uint8_t p0 = b0 ^ b1 ^ b2 ^ b4;
    const std::uint8_t p1 = b1 ^ b2 ^ b3 ^ b5;
    const std::uint8_t p2 = b0 ^ b1 ^ b3 ^ b6;
    const std::uint8_t p3 = b0 ^ b2 ^ b3 ^ b7;
    const std::uint8_t parity = static_cast<std::uint8_t>(
        (p0 << 0) | (p1 << 1) | (p2 << 2) | (p3 << 3));

    if (parity != 0) corrected = true;
    switch (parity & 0xF) {
        case 0xD: return static_cast<std::uint8_t>((codeword ^ 1) & 0xF);
        case 0x7: return static_cast<std::uint8_t>((codeword ^ 2) & 0xF);
        case 0xB: return static_cast<std::uint8_t>((codeword ^ 4) & 0xF);
        case 0xE: return static_cast<std::uint8_t>((codeword ^ 8) & 0xF);
        default:
            return static_cast<std::uint8_t>(codeword & 0xF);
    }
}

std::uint8_t header_crc5(std::uint8_t length, std::uint8_t fec_info,
                          std::uint8_t /*unused*/) noexcept {
    const std::uint8_t a0 = (length >> 4) & 1u;
    const std::uint8_t a1 = (length >> 5) & 1u;
    const std::uint8_t a2 = (length >> 6) & 1u;
    const std::uint8_t a3 = (length >> 7) & 1u;

    const std::uint8_t b0 = (length >> 0) & 1u;
    const std::uint8_t b1 = (length >> 1) & 1u;
    const std::uint8_t b2 = (length >> 2) & 1u;
    const std::uint8_t b3 = (length >> 3) & 1u;

    const std::uint8_t c0 = (fec_info >> 0) & 1u;
    const std::uint8_t c1 = (fec_info >> 1) & 1u;
    const std::uint8_t c2 = (fec_info >> 2) & 1u;
    const std::uint8_t c3 = (fec_info >> 3) & 1u;

    return static_cast<std::uint8_t>(
          ((a0 ^ a1 ^ a2 ^ a3)             << 4)
        | ((a3 ^ b1 ^ b2 ^ b3 ^ c0)        << 3)
        | ((a2 ^ b0 ^ b3 ^ c1 ^ c3)        << 2)
        | ((a1 ^ b0 ^ b2 ^ c0 ^ c1 ^ c2)   << 1)
        | ((a0 ^ b1 ^ c0 ^ c1 ^ c2 ^ c3)   << 0));
}

void whiten(std::span<std::uint8_t> data) noexcept {
    std::uint16_t lfsr = 0x1FF;
    for (auto& b : data) {
        std::uint8_t out = 0;
        for (int i = 0; i < 8; ++i) {
            const std::uint8_t bit = static_cast<std::uint8_t>(lfsr & 1u);
            const std::uint16_t fb = static_cast<std::uint16_t>(((lfsr >> 0) ^ (lfsr >> 5)) & 1u);
            lfsr = static_cast<std::uint16_t>((lfsr >> 1) | (fb << 8));
            out = static_cast<std::uint8_t>(out | (bit << i));
        }
        b ^= out;
    }
}

std::uint16_t crc16(std::span<const std::uint8_t> data, std::uint16_t init) noexcept {
    std::uint16_t crc = init;
    for (auto b : data) {
        crc ^= static_cast<std::uint16_t>(b) << 8;
        for (int i = 0; i < 8; ++i) {
            const bool bit = (crc & 0x8000) != 0;
            crc = static_cast<std::uint16_t>(crc << 1);
            if (bit) crc ^= 0x1021;
        }
    }
    return crc;
}

// ===== LoRa-SDR / SDRangel Sx1272 codeword whitening + data CRC ==========
// Verbatim ports of `Sx1272ComputeWhiteningLfsr` and `sx1272DataChecksum`
// from myriadrf/LoRa-SDR `LoRaCodes.hpp`. These run BEFORE Hamming decode
// (the LFSR XORs the raw codewords, masked to 4+RDD bits), and the CRC is
// a non-standard CCITT variant masked by an 8-bit LFSR. Required to decode
// real Semtech-format LoRa frames.

void sx1272_whiten_codewords(std::span<std::uint8_t> codewords,
                              int bit_offset,
                              std::uint8_t rdd) noexcept {
    static constexpr std::uint64_t seed1[2] = {
        0x6572D100E85C2EFFULL, 0xE85C2EFFFFFFFFFFULL};
    static constexpr std::uint64_t seed2[2] = {
        0x05121100F8ECFEEFULL, 0xF8ECFEEFEFEFEFEFULL};

    const std::uint8_t mask = static_cast<std::uint8_t>(0xFF >> (4 - rdd));
    const bool single_parity = (rdd == 1);
    std::uint64_t r[2] = {
        single_parity ? seed2[0] : seed1[0],
        single_parity ? seed2[1] : seed1[1]};

    auto step = [](std::uint64_t v) -> std::uint64_t {
        return (v >> 8) | (((v >> 32) ^ (v >> 24) ^ (v >> 16) ^ v) << 56);
    };

    int i = 0;
    for (; i < bit_offset; ++i) {
        r[i & 1] = step(r[i & 1]);
    }
    for (std::size_t j = 0; j < codewords.size(); ++j, ++i) {
        codewords[j] = static_cast<std::uint8_t>(codewords[j] ^ (r[i & 1] & mask));
        r[i & 1] = step(r[i & 1]);
    }
}

std::uint16_t sx1272_data_checksum(std::span<const std::uint8_t> data) noexcept {
    auto xsum8 = [](std::uint8_t t) -> std::uint8_t {
        t = static_cast<std::uint8_t>(t ^ (t >> 4));
        t = static_cast<std::uint8_t>(t ^ (t >> 2));
        t = static_cast<std::uint8_t>(t ^ (t >> 1));
        return static_cast<std::uint8_t>(t & 1u);
    };
    auto crc16sx = [](std::uint16_t crc, std::uint16_t poly) -> std::uint16_t {
        for (int i = 0; i < 8; ++i) {
            if (crc & 0x8000)
                crc = static_cast<std::uint16_t>((crc << 1) ^ poly);
            else
                crc = static_cast<std::uint16_t>(crc << 1);
        }
        return crc;
    };
    std::uint16_t res = 0;
    std::uint8_t  v   = 0xFF;
    std::uint16_t crc = 0;
    for (auto b : data) {
        crc = crc16sx(res, 0x1021);
        v   = static_cast<std::uint8_t>(xsum8(v & 0xB8) | (v << 1));
        res = static_cast<std::uint16_t>(crc ^ b);
    }
    res = static_cast<std::uint16_t>(res ^ v);
    v   = static_cast<std::uint8_t>(xsum8(v & 0xB8) | (v << 1));
    res = static_cast<std::uint16_t>(res ^ (static_cast<std::uint16_t>(v) << 8));
    return res;
}

// ---- gr-lora_sdr / SDRangel Meshtastic data path ---------------------
//
// Verbatim port of `whitening_seq[]` from gr-lora_sdr `lib/tables.h` (also
// embedded as `s_whiteningSeq[]` in SDRangel's
// `meshtasticdemoddecoderlora.h`). 255 bytes; index modulo 255.
namespace {
constexpr std::uint8_t kWhiteningSeq[255] = {
    0xFF, 0xFE, 0xFC, 0xF8, 0xF0, 0xE1, 0xC2, 0x85, 0x0B, 0x17, 0x2F, 0x5E, 0xBC, 0x78, 0xF1, 0xE3,
    0xC6, 0x8D, 0x1A, 0x34, 0x68, 0xD0, 0xA0, 0x40, 0x80, 0x01, 0x02, 0x04, 0x08, 0x11, 0x23, 0x47,
    0x8E, 0x1C, 0x38, 0x71, 0xE2, 0xC4, 0x89, 0x12, 0x25, 0x4B, 0x97, 0x2E, 0x5C, 0xB8, 0x70, 0xE0,
    0xC0, 0x81, 0x03, 0x06, 0x0C, 0x19, 0x32, 0x64, 0xC9, 0x92, 0x24, 0x49, 0x93, 0x26, 0x4D, 0x9B,
    0x37, 0x6E, 0xDC, 0xB9, 0x72, 0xE4, 0xC8, 0x90, 0x20, 0x41, 0x82, 0x05, 0x0A, 0x15, 0x2B, 0x56,
    0xAD, 0x5B, 0xB6, 0x6D, 0xDA, 0xB5, 0x6B, 0xD6, 0xAC, 0x59, 0xB2, 0x65, 0xCB, 0x96, 0x2C, 0x58,
    0xB0, 0x61, 0xC3, 0x87, 0x0F, 0x1F, 0x3E, 0x7D, 0xFB, 0xF6, 0xED, 0xDB, 0xB7, 0x6F, 0xDE, 0xBD,
    0x7A, 0xF5, 0xEB, 0xD7, 0xAE, 0x5D, 0xBA, 0x74, 0xE8, 0xD1, 0xA2, 0x44, 0x88, 0x10, 0x21, 0x43,
    0x86, 0x0D, 0x1B, 0x36, 0x6C, 0xD8, 0xB1, 0x63, 0xC7, 0x8F, 0x1E, 0x3C, 0x79, 0xF3, 0xE7, 0xCE,
    0x9C, 0x39, 0x73, 0xE6, 0xCC, 0x98, 0x31, 0x62, 0xC5, 0x8B, 0x16, 0x2D, 0x5A, 0xB4, 0x69, 0xD2,
    0xA4, 0x48, 0x91, 0x22, 0x45, 0x8A, 0x14, 0x29, 0x52, 0xA5, 0x4A, 0x95, 0x2A, 0x54, 0xA9, 0x53,
    0xA7, 0x4E, 0x9D, 0x3B, 0x77, 0xEE, 0xDD, 0xBB, 0x76, 0xEC, 0xD9, 0xB3, 0x67, 0xCF, 0x9E, 0x3D,
    0x7B, 0xF7, 0xEF, 0xDF, 0xBF, 0x7E, 0xFD, 0xFA, 0xF4, 0xE9, 0xD3, 0xA6, 0x4C, 0x99, 0x33, 0x66,
    0xCD, 0x9A, 0x35, 0x6A, 0xD4, 0xA8, 0x51, 0xA3, 0x46, 0x8C, 0x18, 0x30, 0x60, 0xC1, 0x83, 0x07,
    0x0E, 0x1D, 0x3A, 0x75, 0xEA, 0xD5, 0xAA, 0x55, 0xAB, 0x57, 0xAF, 0x5F, 0xBE, 0x7C, 0xF9, 0xF2,
    0xE5, 0xCA, 0x94, 0x28, 0x50, 0xA1, 0x42, 0x84, 0x09, 0x13, 0x27, 0x4F, 0x9F, 0x3F, 0x7F};
} // namespace

void dewhiten_payload_bytes(std::span<std::uint8_t> data) noexcept {
    for (std::size_t i = 0; i < data.size(); ++i) {
        data[i] = static_cast<std::uint8_t>(data[i] ^ kWhiteningSeq[i % 255]);
    }
}

std::uint16_t crc16gr(std::span<const std::uint8_t> data) noexcept {
    std::uint16_t crc = 0x0000;
    for (auto byte : data) {
        std::uint8_t b = byte;
        for (int j = 0; j < 8; ++j) {
            const std::uint16_t top_bit_xor =
                static_cast<std::uint16_t>(((crc & 0x8000) >> 8) ^ (b & 0x80));
            if (top_bit_xor != 0)
                crc = static_cast<std::uint16_t>((crc << 1) ^ 0x1021);
            else
                crc = static_cast<std::uint16_t>(crc << 1);
            b = static_cast<std::uint8_t>(b << 1);
        }
    }
    return crc;
}

} // namespace mrf::modem::lora
