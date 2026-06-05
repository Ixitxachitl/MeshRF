// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa / "ChirpChat" transmitter front-end — the modulation counterpart to
// ChirpChatRx / MeshtasticRx. Given the raw header+payload symbol bins from
// LoraEncoder::encode_frame_symbols, it synthesizes the full on-air IQ frame:
//
//   [preamble up-chirps] [2 sync-word up-chirps] [2.25 SFD down-chirps]
//   [8 header up-chirps]  [payload up-chirps]
//
// Output is continuous-phase complex<float> at chip_rate_hz * oversampling,
// i.e. the modem's working sample rate (Core re-samples it to the radio rate).
// The chirp shape matches the synthesis used by the RX synthesis tests, so an
// encoded frame round-trips through MeshtasticRx.

#pragma once

#include <complex>
#include <cstdint>
#include <span>
#include <vector>

namespace mrf::modem {

class ChirpChatTx {
public:
    // `chip_rate_hz` is the LoRa bandwidth. `oversampling` samples per chip
    // (>= 1) sets the output rate = chip_rate_hz * oversampling. `sync_word`
    // selects the two network-id chirps. `preamble_symbols` is the number of
    // leading base up-chirps (Meshtastic default 16).
    ChirpChatTx(std::uint8_t spreading_factor,
                std::uint32_t chip_rate_hz,
                int oversampling = 4,
                std::uint8_t sync_word = 0x2B,
                std::uint16_t preamble_symbols = 16);

    // Modulate the header+payload symbol bins (from encode_frame_symbols) into
    // a complete IQ frame, prepended with preamble + sync word + SFD.
    [[nodiscard]] std::vector<std::complex<float>> modulate(
        std::span<const std::uint16_t> symbols) const;

    [[nodiscard]] std::uint32_t output_rate_hz() const noexcept {
        return chip_rate_ * static_cast<std::uint32_t>(os_);
    }
    [[nodiscard]] int n() const noexcept { return n_; }

private:
    // Generate one symbol (N*os samples). `down` flips the chirp slope (used
    // for the SFD). `value` is the starting FFT bin (0..N-1). `phase` is the
    // running carrier phase carried across symbols so the whole frame is
    // phase-continuous (matches SDRangel's MeshtasticModSource, which never
    // resets the accumulating phasor between chirps); it is advanced in place.
    void append_symbol_(std::vector<std::complex<float>>& out,
                        double value, bool down, int sample_count,
                        double& phase) const;

    std::uint8_t  sf_;
    std::uint32_t chip_rate_;
    int           os_;
    std::uint8_t  sync_word_;
    std::uint16_t preamble_symbols_;
    int           n_; // 2^SF
};

} // namespace mrf::modem
