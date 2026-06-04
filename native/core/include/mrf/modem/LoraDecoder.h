// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa physical-layer decoding primitives, ported from SDRangel's
// `ChirpChatDemodDecoder` and the public LoRa references (Robyns 2018,
// Tapparel 2020). These are pure functions used both by `ChirpChatRx`
// and by unit tests.

#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace mrf::modem::lora {

// ---- Gray --------------------------------------------------------------

// Standard binary -> Gray and back. LoRa uses Gray *de*-mapping after the
// FFT peak: gray(s) = s ^ (s >> 1).
[[nodiscard]] inline std::uint16_t to_gray(std::uint16_t s) noexcept {
    return static_cast<std::uint16_t>(s ^ (s >> 1));
}
[[nodiscard]] std::uint16_t from_gray(std::uint16_t g) noexcept;

// ---- Symbol -> nibble bits --------------------------------------------

// Convert a raw FFT-peak symbol value (0..N-1, N=2^SF) to the SF-2 (or SF-2
// when LDRO is on, SF when off — Meshtastic uses SF-2 universally for the
// header) "useful" bits used by the deinterleaver. Implements the firmware
// rule: shift right by 2, then Gray-decode.
[[nodiscard]] std::uint16_t symbol_to_bits(std::uint16_t symbol_value,
                                            std::uint8_t spreading_factor,
                                            bool low_data_rate_optimize) noexcept;

// LoRa-SDR's exact formula:
//   sym += (1 << (sf - ppm)) / 2;
//   sym >>= (sf - ppm);
//   return binaryToGray16(sym);
// PPM == SF:    no shift, no offset      (payload, LDRO off)
// PPM == SF-2:  add 2, shift right 2     (header, or payload with LDRO on)
[[nodiscard]] std::uint16_t gray_demap(std::uint16_t symbol_value,
                                       std::uint8_t spreading_factor,
                                       std::uint8_t ppm) noexcept;

// ---- Diagonal de-interleaver ------------------------------------------

// LoRa interleaves blocks of (sf_app x cr_app) bits diagonally. Input is a
// vector of `cr_app` symbol-bit-words, each `sf_app` bits wide. Output is a
// vector of `sf_app` codewords, each `cr_app` bits wide. (cr_app = 4..8;
// sf_app = sf - 2 normally, sf for header where LDRO is forced.)
[[nodiscard]] std::vector<std::uint8_t> deinterleave(
    std::span<const std::uint16_t> symbols_bits,
    std::uint8_t sf_app,
    std::uint8_t cr_app);

// ---- Hamming (8,4) / (7,4) / (6,4) / (5,4) decoder --------------------

// Decode one codeword. `cr` is the LoRa coding-rate index 1..4 mapped to
// codeword sizes 5..8. Returns the 4-bit nibble in the low nibble of the
// returned byte. `corrected` (out) is set to true if the decoder applied
// single-bit error correction (CR=3,4 only).
[[nodiscard]] std::uint8_t hamming_decode(std::uint8_t codeword,
                                          std::uint8_t cr,
                                          bool& corrected) noexcept;

// ---- PN9 whitening ----------------------------------------------------

// XOR `data` with the LoRa whitening sequence (Semtech PN9, polynomial
// x^9 + x^5 + 1, seed 0x1FF, output little-endian byte at a time).
void whiten(std::span<std::uint8_t> data) noexcept;

// ---- CRC-16/CCITT-FALSE ----------------------------------------------

// Standard LoRa data CRC: poly 0x1021, init 0x0000, no reflect, no xorout
// (firmware option). Some Meshtastic builds use 0xFFFF init; we expose
// both via the `init` parameter.
[[nodiscard]] std::uint16_t crc16(std::span<const std::uint8_t> data,
                                  std::uint16_t init = 0x0000) noexcept;

// ---- LoRa header checksum (5-bit) -------------------------------------

// Computes the 5-bit checksum over the 2-byte header prefix
// {length, fec_info}, exactly per LoRa-SDR `headerChecksum`. The third
// argument is unused (kept for backward source compatibility) and may be
// removed in a follow-up cleanup.
[[nodiscard]] std::uint8_t header_crc5(std::uint8_t length,
                                       std::uint8_t fec_info,
                                       std::uint8_t /*unused*/ = 0) noexcept;

// ---- Sx1272-format codeword whitening + data CRC ----------------------

// Verbatim ports of LoRa-SDR `Sx1272ComputeWhiteningLfsr` and
// `sx1272DataChecksum`. Used by the payload decode pipeline. The
// whitening operates on raw codewords (4+RDD bits each) BEFORE Hamming
// decode, masked by `0xff >> (4 - RDD)`.
void sx1272_whiten_codewords(std::span<std::uint8_t> codewords,
                              int bit_offset,
                              std::uint8_t rdd) noexcept;

[[nodiscard]] std::uint16_t sx1272_data_checksum(
    std::span<const std::uint8_t> data) noexcept;

// ---- gr-lora_sdr / SDRangel Meshtastic data path ----------------------
//
// Modern Meshtastic frames don't use Sx1272 codeword whitening or the
// Sx1272 data checksum. Instead, after Hamming-decoding all codewords
// and packing them into bytes, the data portion (excluding any trailing
// CRC) is XORed against a fixed 255-byte PN9 sequence (dewhitening).
// The CRC is plain CCITT-16 (poly 0x1021, init 0x0000) over the first
// `length-2` payload bytes, then XORed with the last two payload bytes:
//   crc = crc16gr(bytes[0..length-2)) ^ bytes[length-1] ^ (bytes[length-2] << 8)
// and compared to the trailing two CRC bytes treated as little-endian.

void dewhiten_payload_bytes(std::span<std::uint8_t> data) noexcept;

[[nodiscard]] std::uint16_t crc16gr(
    std::span<const std::uint8_t> data) noexcept;

} // namespace mrf::modem::lora
