// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/modem/ChirpChatTx.h"

#include <cmath>
#include <numbers>
#include <stdexcept>

namespace mrf::modem {

namespace {
constexpr double kPi = std::numbers::pi;
} // namespace

ChirpChatTx::ChirpChatTx(std::uint8_t spreading_factor,
                         std::uint32_t chip_rate_hz,
                         int oversampling,
                         std::uint8_t sync_word,
                         std::uint16_t preamble_symbols)
    : sf_(spreading_factor),
      chip_rate_(chip_rate_hz),
      os_(oversampling),
      sync_word_(sync_word),
      preamble_symbols_(preamble_symbols),
      n_(1 << spreading_factor) {
    if (sf_ < 7 || sf_ > 12)
        throw std::invalid_argument("ChirpChatTx: SF must be 7..12");
    if (os_ < 1)
        throw std::invalid_argument("ChirpChatTx: oversampling must be >= 1");
}

void ChirpChatTx::append_symbol_(std::vector<std::complex<float>>& out,
                                 double value, bool down,
                                 int sample_count, double& phase) const {
    // Continuous-phase single-fold LoRa chirp. The instantaneous frequency
    // ramps linearly from the starting bin, folding modulo N. `phase` is the
    // running carrier phase, carried in from the previous symbol so the whole
    // frame is phase-continuous: SDRangel's MeshtasticModSource accumulates a
    // single phasor across preamble/sync/SFD/payload and only wraps it to
    // ]-pi,pi], never resetting it per chirp. A per-symbol reset (the old
    // behaviour) injects a phase step at every symbol boundary, which splatters
    // energy outside the channel and can stop a strict receiver from locking
    // even though our own dechirp-FFT RX (which is phase-insensitive) decodes
    // it fine.
    const double slope = down ? -1.0 : 1.0;
    for (int m = 0; m < sample_count; ++m) {
        out.emplace_back(static_cast<float>(std::cos(phase)),
                         static_cast<float>(std::sin(phase)));
        const double t = static_cast<double>(m) / os_; // chip time
        double ph = std::fmod(value + slope * t, static_cast<double>(n_));
        if (ph < 0) ph += n_;
        const double f = (ph - n_ / 2.0) / n_; // cycles per chip
        phase += 2.0 * kPi * f / os_;
        // Keep the accumulator bounded (matches SDRangel's ]-pi,pi] wrap) so
        // the double doesn't lose precision over a long frame.
        if (phase > kPi) phase -= 2.0 * kPi;
        else if (phase < -kPi) phase += 2.0 * kPi;
    }
}

std::vector<std::complex<float>> ChirpChatTx::modulate(
    std::span<const std::uint16_t> symbols) const {
    const int sym_samples = n_ * os_;

    std::vector<std::complex<float>> out;
    // preamble + 2 sync + 2.25 SFD + payload symbols, plus small guard.
    out.reserve(static_cast<std::size_t>(sym_samples) *
                (preamble_symbols_ + 5 + symbols.size()));

    // Single carrier phase accumulated across the entire frame for phase
    // continuity at every symbol boundary (see append_symbol_).
    double phase = 0.0;

    // Preamble: base up-chirps at bin 0.
    for (std::uint16_t i = 0; i < preamble_symbols_; ++i)
        append_symbol_(out, 0.0, /*down*/ false, sym_samples, phase);

    // Sync word: two up-chirps at bins (nibble << 3).
    const double net0 = static_cast<double>((sync_word_ >> 4) & 0x0F) * 8.0;
    const double net1 = static_cast<double>(sync_word_ & 0x0F) * 8.0;
    append_symbol_(out, net0, false, sym_samples, phase);
    append_symbol_(out, net1, false, sym_samples, phase);

    // SFD: 2.25 down-chirps at bin 0 (two full + a quarter symbol).
    append_symbol_(out, 0.0, /*down*/ true, sym_samples, phase);
    append_symbol_(out, 0.0, /*down*/ true, sym_samples, phase);
    append_symbol_(out, 0.0, /*down*/ true, sym_samples / 4, phase);

    // Header + payload data symbols.
    for (std::uint16_t v : symbols)
        append_symbol_(out, static_cast<double>(v), false, sym_samples, phase);

    return out;
}

} // namespace mrf::modem
