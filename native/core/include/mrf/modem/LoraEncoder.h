// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa physical-layer ENCODING primitives — the exact inverse of the decode
// chain in LoraDecoder.h / MeshtasticRx.cpp. These pure functions take a
// PHY payload (the bytes carried on air: the 16-byte L1 header followed by the
// encrypted Meshtastic payload) and produce the sequence of raw LoRa symbol
// bins (0..N-1, N=2^SF) for the explicit header + payload, matching what
// `MeshtasticRx` demodulates. The chirp modulator (ChirpChatTx) turns those
// symbols into IQ.
//
// Conventions mirror gr-lora_sdr / SDRangel and the round-trip is verified by
// tests/native/test_lora_tx.cpp.

#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace mrf::modem::lora {

// ---- Hamming (8,4)/(7,4)/(6,4)/(5,4) encoder --------------------------
// Inverse of `hamming_decode`. `cr` is the LoRa coding-rate index 1..4 mapped
// to codeword sizes 5..8. Low nibble of `nibble` carries the 4 data bits.
[[nodiscard]] std::uint8_t hamming_encode(std::uint8_t nibble,
                                          std::uint8_t cr) noexcept;

// ---- Diagonal interleaver ---------------------------------------------
// Inverse of `deinterleave`. Input is `sf_app` codewords each `cr_app` bits
// wide; output is `cr_app` symbol-bit words each `sf_app` bits wide.
//   symbols[k] bit m = codewords[(m + k) mod sf_app] bit k
[[nodiscard]] std::vector<std::uint16_t> interleave(
    std::span<const std::uint8_t> codewords,
    std::uint8_t sf_app,
    std::uint8_t cr_app);

// ---- Symbol-bits -> raw FFT bin ---------------------------------------
// Inverse of the header demap (`symbol_to_bits(raw, sf, ldro=true)`):
//   raw = (from_gray(bits) * 4) mod N
[[nodiscard]] std::uint16_t header_bits_to_symbol(std::uint16_t bits,
                                                  std::uint8_t spreading_factor) noexcept;

// Inverse of the payload demap (`gray_demap` after the -1 bin shift), where
// ppm = sf (no LDRO) or sf-2 (LDRO):
//   non-LDRO: raw = (from_gray(bits) + 1) mod N
//   LDRO:     raw = (from_gray(bits) * 4 + 1) mod N
[[nodiscard]] std::uint16_t payload_bits_to_symbol(std::uint16_t bits,
                                                   std::uint8_t spreading_factor,
                                                   std::uint8_t ppm) noexcept;

// ---- Full PHY-frame symbol synthesis ----------------------------------
// Build the raw symbol bins for an explicit-header LoRa frame carrying `data`
// (the on-air bytes: L1 header + encrypted payload; CRC is appended here).
//   - `cr` is the coding-rate index 1..4 (4/5..4/8).
//   - `has_crc` appends a 2-byte CRC (gr-lora_sdr scheme) after whitening.
//   - `low_data_rate_optimize` selects ppm = sf-2 for the payload.
// The returned vector is [8 header symbols][payload symbols], exactly what
// MeshtasticRx collects after the SFD. Throws std::invalid_argument on bad
// parameters.
[[nodiscard]] std::vector<std::uint16_t> encode_frame_symbols(
    std::span<const std::uint8_t> data,
    std::uint8_t spreading_factor,
    std::uint8_t cr,
    bool has_crc,
    bool low_data_rate_optimize);

} // namespace mrf::modem::lora
