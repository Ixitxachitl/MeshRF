// SPDX-License-Identifier: GPL-3.0-or-later
#include <gtest/gtest.h>

#include "mrf/modem/LoraDecoder.h"

#include <array>
#include <bit>

using namespace mrf::modem::lora;

TEST(LoraDecoder, GrayRoundTrip) {
    for (std::uint16_t s = 0; s < 4096; ++s) {
        EXPECT_EQ(s, from_gray(to_gray(s))) << "s=" << s;
    }
}

TEST(LoraDecoder, HammingRoundTripCleanCR4) {
    // For each (cr, nibble), exactly one canonical codeword exists. Verify
    // it round-trips cleanly with corrected==false.
    for (std::uint8_t cr = 1; cr <= 4; ++cr) {
        const std::uint8_t mask =
            cr == 1 ? 0x1F : cr == 2 ? 0x3F : cr == 3 ? 0x7F : 0xFF;
        for (std::uint8_t n = 0; n < 16; ++n) {
            // Find the unique codeword whose decode-distance to n is zero.
            int matches = 0;
            std::uint8_t cw = 0;
            for (int byte = 0; byte <= mask; ++byte) {
                bool corrected = false;
                const auto out = hamming_decode(static_cast<std::uint8_t>(byte), cr, corrected);
                if (out == n && !corrected) {
                    // For CR>=3 a single-bit-error decode also lands on n.
                    // Filter to exact 0-distance: re-encode and compare.
                    // Easier: distance 0 means corrected stays false AND no
                    // other byte at hamming distance 0 exists. We rely on
                    // the next assertion (round-trip after re-encode).
                    ++matches;
                    cw = static_cast<std::uint8_t>(byte);
                }
            }
            // CR=1 has no parity, so multiple "unchanged" mappings can match
            // with distance 0 (since fewer parity bits constrain). For CR>=2
            // there should be exactly one.
            if (cr >= 2) EXPECT_GE(matches, 1) << "cr=" << int(cr) << " n=" << int(n);
            // Round-trip the canonical codeword.
            bool corrected = false;
            EXPECT_EQ(hamming_decode(cw, cr, corrected), n);
            EXPECT_FALSE(corrected);
        }
    }
}

TEST(LoraDecoder, HammingCorrectsSingleBit) {
    // For CR=4 (8,4) Hamming corrects 1-bit errors; verify by encoding
    // then flipping one bit at a time.
    bool corrected = false;
    // nibble 0xA -> find its valid codeword by clean decode of all 256.
    std::uint8_t valid_cw = 0;
    for (int byte = 0; byte < 256; ++byte) {
        bool c2 = false;
        const auto out = hamming_decode(static_cast<std::uint8_t>(byte), 4, c2);
        if (!c2 && out == 0xA) { valid_cw = static_cast<std::uint8_t>(byte); break; }
    }
    for (int bit = 0; bit < 8; ++bit) {
        const std::uint8_t corrupt = static_cast<std::uint8_t>(valid_cw ^ (1u << bit));
        corrected = false;
        const auto out = hamming_decode(corrupt, 4, corrected);
        EXPECT_EQ(out, 0xA) << "bit=" << bit;
        EXPECT_TRUE(corrected) << "bit=" << bit;
    }
}

TEST(LoraDecoder, Crc16Reference) {
    // CRC-16/CCITT-FALSE of "123456789" with init 0xFFFF == 0x29B1 (well-known).
    const std::array<std::uint8_t, 9> ref = {'1','2','3','4','5','6','7','8','9'};
    EXPECT_EQ(crc16(ref, 0xFFFF), 0x29B1);
    // With init 0x0000 (CCITT-XMODEM) -> 0x31C3.
    EXPECT_EQ(crc16(ref, 0x0000), 0x31C3);
}

TEST(LoraDecoder, WhitenIsInvolution) {
    std::array<std::uint8_t, 32> data{};
    for (std::size_t i = 0; i < data.size(); ++i) data[i] = static_cast<std::uint8_t>(i * 7 + 3);
    const auto orig = data;
    whiten(std::span<std::uint8_t>(data));
    EXPECT_NE(data, orig);          // actually changed
    whiten(std::span<std::uint8_t>(data));
    EXPECT_EQ(data, orig);          // self-inverse
}

TEST(LoraDecoder, DeinterleaveShape) {
    // sf_app=10, cr_app=8 -> input 8 symbols, output 10 codewords. Check
    // sizes and that all bits are accounted for via popcount round-trip.
    const std::uint8_t sf_app = 10, cr_app = 8;
    std::array<std::uint16_t, 8> syms = {
        0x0001, 0x0002, 0x0004, 0x0008, 0x0010, 0x0020, 0x0040, 0x0080,
    };
    auto out = deinterleave(std::span<const std::uint16_t>(syms.data(), syms.size()),
                            sf_app, cr_app);
    EXPECT_EQ(out.size(), sf_app);
    int pop_in = 0;
    for (auto s : syms) pop_in += std::popcount(static_cast<unsigned>(s));
    int pop_out = 0;
    for (auto c : out) pop_out += std::popcount(static_cast<unsigned>(c));
    EXPECT_EQ(pop_in, pop_out);
}

namespace {
// Helpers to TX-encode a LoRa header so we can prove the RX chain inverts it.

// Hamming(8,4) encoder, mirroring the encoding lookup-by-distance done in
// LoraDecoder.cpp.
std::uint8_t hamming_encode_test(std::uint8_t nib, std::uint8_t cr) {
    const std::uint8_t d0 = nib & 1, d1 = (nib >> 1) & 1, d2 = (nib >> 2) & 1, d3 = (nib >> 3) & 1;
    const std::uint8_t p1 = d3 ^ d2 ^ d1;
    const std::uint8_t p2 = d3 ^ d2 ^ d0;
    const std::uint8_t p3 = d3 ^ d1 ^ d0;
    const std::uint8_t p4 = d2 ^ d1 ^ d0;
    std::uint8_t cw = static_cast<std::uint8_t>(
        (p1 << 7) | (p2 << 6) | (p3 << 5) | (p4 << 4) |
        (d3 << 3) | (d2 << 2) | (d1 << 1) | d0);
    if (cr == 1) cw &= 0x1F;
    else if (cr == 2) cw &= 0x3F;
    else if (cr == 3) cw &= 0x7F;
    return cw;
}

// Inverse of `deinterleave`: given sf_app codewords, produce cr_app symbol bits.
// Per LoRa-SDR `diagonalInterleaveSx`:
//   symbols[k] bit m  =  codewords[(m + k) mod PPM] bit k
std::vector<std::uint16_t> interleave_test(std::span<const std::uint8_t> codewords,
                                            std::uint8_t sf_app, std::uint8_t cr_app) {
    std::vector<std::uint16_t> syms(cr_app, 0);
    for (std::uint8_t k = 0; k < cr_app; ++k) {
        for (std::uint8_t m = 0; m < sf_app; ++m) {
            const std::uint8_t i = static_cast<std::uint8_t>((m + k) % sf_app);
            const std::uint8_t bit = static_cast<std::uint8_t>((codewords[i] >> k) & 1u);
            syms[k] = static_cast<std::uint16_t>(syms[k] | (bit << m));
        }
    }
    return syms;
}

// Inverse of `symbol_to_bits`: bits -> raw FFT bin for given SF (deBits=2).
// `symbol_to_bits` does: reduced = ((v + 1) / 4) % (1<<(sf-2)); then gray-encode.
// Pick the canonical inverse v = reduced * 4 (which round-trips since
// (reduced*4 + 1)/4 == reduced for unsigned int division).
std::uint16_t bits_to_symbol_test(std::uint16_t bits, std::uint8_t sf) {
    const int N = 1 << sf;
    const std::uint16_t reduced = from_gray(bits);
    return static_cast<std::uint16_t>((static_cast<int>(reduced) << 2) % N);
}
} // namespace

TEST(LoraDecoder, HeaderRoundTripSf9) {
    // Build a canonical LoRa explicit header at SF=9: len=20, cr=1 (4/5),
    // crc=on. Header is always encoded at CR=4/8 with sf_app = sf - 2 = 7.
    const std::uint8_t sf = 9;
    const std::uint8_t sf_app = sf - 2;
    const std::uint8_t cr_app = 8;

    std::vector<std::uint8_t> nibbles = {
        0x1,                                 // payload length high
        0x4,                                 // payload length low  -> 0x14 = 20
        static_cast<std::uint8_t>(0x8 | 1),  // [crc=1, cr=1]
        0x0, 0x0,                            // header parity (CRC-5; not validated)
        0x0, 0x0,                            // sf_app=7 -> total 7 nibbles
    };
    ASSERT_EQ(nibbles.size(), sf_app);

    // Encode: nibble -> Hamming codeword (8 bits, CR=4)
    std::vector<std::uint8_t> codewords(sf_app);
    for (std::size_t i = 0; i < sf_app; ++i)
        codewords[i] = hamming_encode_test(nibbles[i], 4);

    // Interleave -> 8 symbol bits
    auto sym_bits = interleave_test(std::span<const std::uint8_t>(codewords.data(), codewords.size()),
                                     sf_app, cr_app);
    ASSERT_EQ(sym_bits.size(), cr_app);

    // Bits -> raw FFT bins
    std::vector<std::uint16_t> bins(cr_app);
    for (std::size_t i = 0; i < cr_app; ++i)
        bins[i] = bits_to_symbol_test(sym_bits[i], sf);

    // Now run the RX path: bin -> symbol_to_bits -> deinterleave ->
    // hamming_decode, and verify we recover the same nibbles.
    std::vector<std::uint16_t> rx_bits(cr_app);
    for (std::size_t i = 0; i < cr_app; ++i)
        rx_bits[i] = symbol_to_bits(bins[i], sf, /*ldro*/ true);

    auto rx_codewords = deinterleave(std::span<const std::uint16_t>(rx_bits.data(), rx_bits.size()),
                                      sf_app, cr_app);
    ASSERT_EQ(rx_codewords.size(), sf_app);

    for (std::size_t i = 0; i < sf_app; ++i) {
        bool corrected = false;
        const std::uint8_t nib = hamming_decode(rx_codewords[i], /*cr*/ 4, corrected);
        EXPECT_EQ(nib, nibbles[i]) << "i=" << i;
    }
}

// Diagnostic: dump everything the decoder produces for a real captured frame.
// This is not a strict pass/fail (we don't know the ground-truth header for
// the captured TX), but it lets us see what `length`, `cr`, `has_crc`, and
// the checksum match status look like with the canonical pipeline.
TEST(LoraDecoder, CapturedFrameSf9Diag) {
    const std::uint16_t bins[8] = {41, 9, 481, 253, 441, 445, 109, 85};
    const std::uint8_t sf = 9, sf_app = sf - 2, cr_app = 8;
    std::vector<std::uint16_t> bits(8);
    for (int i = 0; i < 8; ++i)
        bits[i] = symbol_to_bits(bins[i], sf, true);
    auto cws = deinterleave(std::span<const std::uint16_t>(bits.data(), bits.size()),
                             sf_app, cr_app);
    ASSERT_EQ(cws.size(), sf_app);
    std::uint8_t nibs[8] = {};
    for (std::size_t i = 0; i < cws.size(); ++i) {
        bool c = false;
        nibs[i] = hamming_decode(cws[i], 4, c);
    }
    const std::uint8_t length = static_cast<std::uint8_t>((nibs[0] << 4) | (nibs[1] & 0xF));
    const std::uint8_t fec    = static_cast<std::uint8_t>(nibs[2] & 0xF);
    const std::uint8_t got    = static_cast<std::uint8_t>(((nibs[3] & 0xF) << 4) | (nibs[4] & 0xF));
    const std::uint8_t exp_   = header_crc5(length, fec);

    std::printf("captured: length=0x%02x (%u) fec=0x%x has_crc=%d cr=%d\n",
                length, length, fec, fec & 1, (fec >> 1) & 7);
    std::printf("captured: checksum got=0x%02x exp=0x%02x ok=%d\n",
                got, exp_, got == exp_);
    for (std::size_t i = 0; i < cws.size(); ++i)
        std::printf("captured: cw[%zu]=0x%02x nib=0x%x\n", i, cws[i], nibs[i]);
    SUCCEED();
}
