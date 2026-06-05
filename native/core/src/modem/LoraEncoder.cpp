// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/modem/LoraEncoder.h"
#include "mrf/modem/LoraDecoder.h"

#include <algorithm>
#include <stdexcept>

namespace mrf::modem::lora {

std::uint8_t hamming_encode(std::uint8_t nibble, std::uint8_t cr) noexcept {
    // Bit-exact match to SDRangel's modmeshtastic encoder (the proven-good TX
    // that OpenWebRX+ decodes). The data bits occupy the low nibble; the parity
    // bits are computed differently for the single-parity CR=4/5 case than for
    // the Hamming(8,4) family used by CR 4/6..4/8 (and the explicit header).
    const std::uint8_t d0 = nibble & 1u;
    const std::uint8_t d1 = (nibble >> 1) & 1u;
    const std::uint8_t d2 = (nibble >> 2) & 1u;
    const std::uint8_t d3 = (nibble >> 3) & 1u;
    if (cr == 1) {
        // CR 4/5: SDRangel `encodeParity54` -> single even-parity bit at b4.
        const std::uint8_t p = d0 ^ d1 ^ d2 ^ d3;
        return static_cast<std::uint8_t>((nibble & 0x0F) | (p << 4));
    }
    // CR 4/6..4/8: SDRangel `encodeHamming84sx` parity layout. NOTE this is the
    // exact inverse of LoraDecoder::hamming_decode's syndrome (b4=d0^d1^d2,
    // b5=d1^d2^d3, b6=d0^d1^d3, b7=d0^d2^d3); our previous encoder permuted
    // bits 5..7, corrupting the header on strict receivers.
    const std::uint8_t b4 = d0 ^ d1 ^ d2;
    const std::uint8_t b5 = d1 ^ d2 ^ d3;
    const std::uint8_t b6 = d0 ^ d1 ^ d3;
    const std::uint8_t b7 = d0 ^ d2 ^ d3;
    std::uint8_t cw = static_cast<std::uint8_t>(
        (b7 << 7) | (b6 << 6) | (b5 << 5) | (b4 << 4) |
        (d3 << 3) | (d2 << 2) | (d1 << 1) | d0);
    switch (cr) {
        case 2: cw &= 0x3F; break; // 4/6
        case 3: cw &= 0x7F; break; // 4/7
        default: break;            // 4/8 keeps all 8 bits
    }
    return cw;
}

std::vector<std::uint16_t> interleave(std::span<const std::uint8_t> codewords,
                                      std::uint8_t sf_app,
                                      std::uint8_t cr_app) {
    if (cr_app < 4 || cr_app > 8)
        throw std::invalid_argument("interleave: cr_app out of range");
    if (codewords.size() != sf_app)
        throw std::invalid_argument("interleave: codewords size != sf_app");

    std::vector<std::uint16_t> symbols(cr_app, 0);
    // Inverse of `deinterleave` (LoRa-SDR diagonalInterleaveSx):
    //   symbols[k] bit m = codewords[(m + k) mod sf_app] bit k
    for (std::uint8_t k = 0; k < cr_app; ++k) {
        for (std::uint8_t m = 0; m < sf_app; ++m) {
            const std::uint8_t i = static_cast<std::uint8_t>((m + k) % sf_app);
            const std::uint8_t bit = static_cast<std::uint8_t>((codewords[i] >> k) & 1u);
            symbols[k] = static_cast<std::uint16_t>(symbols[k] | (bit << m));
        }
    }
    return symbols;
}

std::uint16_t header_bits_to_symbol(std::uint16_t bits,
                                    std::uint8_t spreading_factor) noexcept {
    const int n = 1 << spreading_factor;
    const std::uint16_t reduced = from_gray(bits);
    // The header is always sent at reduced rate (DE=2, deWidth=4) and, like the
    // payload, carries the Semtech +1 symbol shift. SDRangel's Meshtastic
    // modulator (`MeshtasticModSource::encodeSymbol`) emits
    //   rawSymbol = (deWidth * baseSymbol + 1) % N
    // for BOTH header and payload, and its demod undoes it with `raw_bin - 1`.
    // A strict receiver (OpenWebRX+, RadioLib) therefore expects `4*g + 1`;
    // emitting `4*g` (no +1) decodes to g-1 and fails the header CRC. Our own
    // RX tolerates either because it floor-rounds `(bin+1)/4` and sweeps a bin
    // offset, which is why the round-trip test never caught this.
    return static_cast<std::uint16_t>(((static_cast<int>(reduced) << 2) + 1) % n);
}

std::uint16_t payload_bits_to_symbol(std::uint16_t bits,
                                     std::uint8_t spreading_factor,
                                     std::uint8_t ppm) noexcept {
    const int n = 1 << spreading_factor;
    const std::uint16_t reduced = from_gray(bits);
    const int diff = static_cast<int>(spreading_factor) - static_cast<int>(ppm);
    if (diff <= 0) {
        // ppm == sf: raw = (from_gray(bits) + 1) mod N
        return static_cast<std::uint16_t>((static_cast<int>(reduced) + 1) % n);
    }
    // ppm == sf-diff (LDRO): raw = (from_gray(bits) << diff) + 1, mod N
    const int corr = (static_cast<int>(reduced) << diff);
    return static_cast<std::uint16_t>((corr + 1) % n);
}

std::vector<std::uint16_t> encode_frame_symbols(std::span<const std::uint8_t> data,
                                                std::uint8_t spreading_factor,
                                                std::uint8_t cr,
                                                bool has_crc,
                                                bool low_data_rate_optimize) {
    const int sf = static_cast<int>(spreading_factor);
    if (sf < 7 || sf > 12)
        throw std::invalid_argument("encode_frame_symbols: SF must be 7..12");
    if (cr < 1 || cr > 4)
        throw std::invalid_argument("encode_frame_symbols: cr must be 1..4");
    if (data.empty() || data.size() > 255)
        throw std::invalid_argument("encode_frame_symbols: data length 1..255");

    const std::uint8_t sf_app = static_cast<std::uint8_t>(sf - 2);
    const std::uint8_t pl = static_cast<std::uint8_t>(data.size());

    // --- Block layout (gr-lora_sdr formula) --------------------------------
    // Determine how many payload symbols/nibbles we emit so we know how many
    // bytes of the (whitened) stream are actually consumed, including the
    // trailing CRC and any zero padding that fills the final interleaver block.
    const std::uint8_t ppm =
        static_cast<std::uint8_t>(sf - (low_data_rate_optimize ? 2 : 0));
    const std::uint8_t cw_len = static_cast<std::uint8_t>(cr + 4);
    const int crc_flag = has_crc ? 1 : 0;
    const int num = 8 * static_cast<int>(pl) - 4 * sf + 28 + 16 * crc_flag;
    const int den = 4 * (sf - (low_data_rate_optimize ? 2 : 0));
    int blocks = (num + den - 1) / den;
    if (blocks < 0) blocks = 0;
    const std::size_t leak_count = static_cast<std::size_t>(sf_app) - 5u; // sf-7
    const std::size_t total_nibbles =
        leak_count + static_cast<std::size_t>(blocks) * ppm;
    const std::size_t total_bytes = (total_nibbles + 1u) / 2u;

    // --- 1. Build the whitened byte stream: data + CRC + padding -----------
    // SDRangel's Meshtastic TX appends the 2-byte data checksum to the data,
    // FEC-encodes the whole thing, then whitens every codeword. The 255-byte
    // gr-lora_sdr table whitening applied to the *bytes* (data + CRC + the zero
    // padding that fills the last interleaver block) is bit-for-bit equivalent
    // to that codeword whitening, so we whiten the full byte stream here.
    std::uint16_t crc = 0;
    if (has_crc) {
        crc = sx1272_data_checksum(data);
    }
    std::vector<std::uint8_t> stream(data.begin(), data.end());
    if (has_crc) {
        stream.push_back(static_cast<std::uint8_t>(crc & 0xFF));
        stream.push_back(static_cast<std::uint8_t>((crc >> 8) & 0xFF));
    }
    while (stream.size() < total_bytes) stream.push_back(0u); // zero padding
    dewhiten_payload_bytes(std::span<std::uint8_t>(stream.data(), stream.size()));

    // --- 2. Whitened stream -> nibble sequence (low nibble first) ----------
    std::vector<std::uint8_t> stream_nibbles;
    stream_nibbles.reserve(stream.size() * 2);
    for (std::uint8_t b : stream) {
        stream_nibbles.push_back(static_cast<std::uint8_t>(b & 0x0F));
        stream_nibbles.push_back(static_cast<std::uint8_t>((b >> 4) & 0x0F));
    }

    // --- 3. Header block: 5 header nibbles + (sf_app-5) "leak" payload ------
    const std::uint8_t fec_info = static_cast<std::uint8_t>((cr << 1) | (has_crc ? 1 : 0));
    const std::uint8_t chk = header_crc5(pl, fec_info);

    std::vector<std::uint8_t> header_nibbles(sf_app, 0);
    header_nibbles[0] = static_cast<std::uint8_t>((pl >> 4) & 0x0F);
    header_nibbles[1] = static_cast<std::uint8_t>(pl & 0x0F);
    header_nibbles[2] = static_cast<std::uint8_t>(fec_info & 0x0F);
    header_nibbles[3] = static_cast<std::uint8_t>((chk >> 4) & 0x0F);
    header_nibbles[4] = static_cast<std::uint8_t>(chk & 0x0F);
    std::size_t consumed = 0;
    for (std::size_t i = 0; i < leak_count; ++i) {
        const std::uint8_t nib = (consumed < stream_nibbles.size())
                                     ? stream_nibbles[consumed] : 0u;
        header_nibbles[5 + i] = nib;
        ++consumed;
    }

    std::vector<std::uint16_t> symbols;
    {
        std::vector<std::uint8_t> codewords(sf_app);
        for (std::size_t i = 0; i < sf_app; ++i)
            codewords[i] = hamming_encode(header_nibbles[i], 4); // header is CR=4/8
        auto sym_bits = interleave(
            std::span<const std::uint8_t>(codewords.data(), codewords.size()),
            sf_app, /*cr_app*/ 8);
        for (std::uint16_t b : sym_bits)
            symbols.push_back(header_bits_to_symbol(b, spreading_factor));
    }

    // --- 4. Payload blocks --------------------------------------------------
    for (int b = 0; b < blocks; ++b) {
        std::vector<std::uint8_t> codewords(ppm);
        for (std::uint8_t i = 0; i < ppm; ++i) {
            const std::uint8_t nib = (consumed < stream_nibbles.size())
                                         ? stream_nibbles[consumed] : 0u;
            codewords[i] = hamming_encode(nib, cr);
            ++consumed;
        }
        auto sym_bits = interleave(
            std::span<const std::uint8_t>(codewords.data(), codewords.size()),
            ppm, cw_len);
        for (std::uint16_t bits : sym_bits)
            symbols.push_back(payload_bits_to_symbol(bits, spreading_factor, ppm));
    }

    return symbols;
}

} // namespace mrf::modem::lora
