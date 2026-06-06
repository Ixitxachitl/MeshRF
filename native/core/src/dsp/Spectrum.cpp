// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Spectrum.h"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace mrf::dsp {

Spectrum::Spectrum(std::size_t fft_size)
    : n_(fft_size), fft_(fft_size) {
    constexpr float kPi = std::numbers::pi_v<float>;
    window_.resize(n_);
    // Hann window
    for (std::size_t i = 0; i < n_; ++i) {
        window_[i] = 0.5f * (1.0f - std::cos(2.0f * kPi *
                                             static_cast<float>(i) /
                                             static_cast<float>(n_ - 1)));
    }
    ring_.assign(n_, sample_t{0.0f, 0.0f});
    latest_db_.assign(n_, -120.0f);
    held_db_.assign(n_, -200.0f);
    scratch_.assign(n_, sample_t{0.0f, 0.0f});
}

void Spectrum::push(std::span<const sample_t> samples) {
    for (auto s : samples) {
        ring_[ring_pos_] = s;
        ring_pos_ = (ring_pos_ + 1u) % n_;
        if (++since_frame_ >= n_) {
            std::lock_guard<std::mutex> lk(mu_);
            compute_frame_locked();
            since_frame_ = 0;
        }
    }
}

void Spectrum::compute_frame_locked() {
    // Copy ring into scratch in chronological order, then window.
    for (std::size_t i = 0; i < n_; ++i) {
        const std::size_t src = (ring_pos_ + i) % n_;
        scratch_[i] = ring_[src] * window_[i];
    }
    fft_.forward(std::span<sample_t>(scratch_.data(), n_));

    // Magnitude in dBFS, FFT-shifted (DC at index n/2). The display frequency
    // axis is then inverted about DC (mirror = (n - shifted) % n) so the
    // waterfall reads low->high frequency left->right to match the spectrum
    // the decoder sees. This is a display-only reflection; the modem path
    // consumes the unflipped IQ separately, so decoding is unaffected.
    const float norm = 1.0f / static_cast<float>(n_);
    const std::size_t half = n_ / 2;
    for (std::size_t k = 0; k < n_; ++k) {
        const std::size_t shifted = (k + half) % n_;
        const std::size_t mirror  = (n_ - shifted) % n_;
        const auto v = scratch_[k] * norm;
        const float power = v.real() * v.real() + v.imag() * v.imag();
        const float db = (power > 1e-20f)
            ? 10.0f * std::log10(power)
            : -200.0f;
        latest_db_[mirror] = db;
        held_db_[mirror] = held_valid_ ? std::max(held_db_[mirror], db) : db;
    }
    held_valid_ = true;
    ++frames_;
}

bool Spectrum::latest(std::span<float> out_dbfs) const {
    if (out_dbfs.size() < n_) return false;
    std::lock_guard<std::mutex> lk(mu_);
    if (frames_ == 0) return false;
    if (held_valid_) {
        std::copy_n(held_db_.begin(), n_, out_dbfs.begin());
        std::fill(held_db_.begin(), held_db_.end(), -200.0f);
        held_valid_ = false;
    } else {
        std::copy_n(latest_db_.begin(), n_, out_dbfs.begin());
    }
    return true;
}

std::uint64_t Spectrum::frame_count() const noexcept {
    std::lock_guard<std::mutex> lk(mu_);
    return frames_;
}

} // namespace mrf::dsp
