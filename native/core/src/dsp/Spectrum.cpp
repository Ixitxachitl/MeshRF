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
    scratch_.assign(n_, sample_t{0.0f, 0.0f});

    // Initialize the frame ring with kFrameRingCapacity frames.
    frame_ring_.resize(kFrameRingCapacity);
    for (auto& frame : frame_ring_)
        frame.assign(n_, -200.0f);
}

void Spectrum::set_history_frame_stride(std::size_t stride) {
    std::lock_guard<std::mutex> lk(mu_);
    history_frame_stride_ = std::max<std::size_t>(1u, stride);
    history_frame_accum_ = 0;
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
    const bool store_history = (++history_frame_accum_ >= history_frame_stride_);
    if (store_history)
        history_frame_accum_ = 0;

    // Copy ring into scratch in chronological order, then window.
    for (std::size_t i = 0; i < n_; ++i) {
        const std::size_t src = (ring_pos_ + i) % n_;
        scratch_[i] = ring_[src] * window_[i];
    }
    fft_.forward(std::span<sample_t>(scratch_.data(), n_));

    // Magnitude in dBFS, FFT-shifted (DC at index n/2). Standard layout:
    // index 0 = most negative frequency, index n/2 = DC (tuned LO), index
    // n-1 = most positive frequency. No additional mirror is applied so the
    // display centre always corresponds to the tuned frequency.
    const float norm = 1.0f / static_cast<float>(n_);
    const std::size_t half = n_ / 2;
    for (std::size_t k = 0; k < n_; ++k) {
        const std::size_t shifted = (k + half) % n_;
        const auto v = scratch_[k] * norm;
        const float power = v.real() * v.real() + v.imag() * v.imag();
        const float db = (power > 1e-20f)
            ? 10.0f * std::log10(power)
            : -200.0f;
        latest_db_[shifted] = db;

        // Store only decimated history frames for the waterfall pull path.
        if (store_history)
            frame_ring_[frame_ring_pos_][shifted] = db;
    }
    ++latest_frames_;

    if (store_history) {
        ++history_frames_;
        // Advance frame ring position for the next stored history frame.
        frame_ring_pos_ = (frame_ring_pos_ + 1) % kFrameRingCapacity;
    }
}

bool Spectrum::latest(std::span<float> out_dbfs) const {
    if (out_dbfs.size() < n_) return false;
    std::lock_guard<std::mutex> lk(mu_);
    if (latest_frames_ == 0) return false;
    // Always return the latest instantaneous frame. The UI-side EMA smoothing
    // provides visual stability; max-hold here caused bouncing when UI frame
    // rate didn't align with native FFT frame rate.
    std::copy_n(latest_db_.begin(), n_, out_dbfs.begin());
    return true;
}

std::uint32_t Spectrum::pull_frames(
    std::uint64_t after_frame_index,
    std::uint32_t max_count,
    std::span<float> out_frames) const {
    std::lock_guard<std::mutex> lk(mu_);

    if (after_frame_index >= history_frames_ || max_count == 0)
        return 0;

    // Requested frames are those with index > after_frame_index, up to max_count.
    std::uint64_t first_idx = after_frame_index + 1;
    std::uint32_t avail = static_cast<std::uint32_t>(
        std::min(static_cast<std::uint64_t>(max_count),
                 history_frames_ - first_idx));

    // Check if output buffer is large enough.
    if (out_frames.size() < avail * n_)
        return 0;

    // Extract frames from the ring. The ring holds the most recent
    // kFrameRingCapacity frames. Frame index F is stored at position
    // (F - (frames_ - kFrameRingCapacity)) % kFrameRingCapacity, provided
    // F >= frames_ - kFrameRingCapacity.
    for (std::uint32_t i = 0; i < avail; ++i) {
        std::uint64_t frame_idx = first_idx + i;
        // Compute position in the frame ring.
        std::uint64_t age = history_frames_ - frame_idx;  // how old is this frame?
        if (age >= kFrameRingCapacity) {
            // Frame has cycled out of the ring; fill with silence.
            std::fill_n(out_frames.begin() + i * n_, n_, -200.0f);
        } else {
            // Position of this frame in frame_ring.
            std::size_t pos = (frame_ring_pos_ + kFrameRingCapacity - age) %
                              kFrameRingCapacity;
            std::copy_n(frame_ring_[pos].begin(), n_,
                        out_frames.begin() + i * n_);
        }
    }
    return avail;
}

std::uint64_t Spectrum::frame_count() const noexcept {
    std::lock_guard<std::mutex> lk(mu_);
    return history_frames_;
}

} // namespace mrf::dsp
