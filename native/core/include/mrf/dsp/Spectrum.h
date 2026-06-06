// SPDX-License-Identifier: GPL-3.0-or-later
//
// Streaming spectrum analyzer. Accepts samples in arbitrary chunk sizes,
// accumulates them in a ring of length fft_size, and on each fill computes a
// Hann-windowed magnitude spectrum (dBFS). Frames are max-held between UI
// pulls so short bursts are still visible when the UI polls slowly.

#pragma once

#include "mrf/dsp/Fft.h"

#include <complex>
#include <cstddef>
#include <mutex>
#include <span>
#include <vector>

namespace mrf::dsp {

class Spectrum {
public:
    using sample_t = std::complex<float>;

    explicit Spectrum(std::size_t fft_size);

    [[nodiscard]] std::size_t fft_size() const noexcept { return n_; }

    // Push samples. Computes a frame whenever `fft_size` new samples have
    // accumulated since the last frame.
    void push(std::span<const sample_t> samples);

    // Copy the max-held dBFS spectrum since the previous pull (length fft_size,
    // FFT-shifted so DC is at bin n/2). If no new frame arrived since the last
    // pull, copies the most recent frame. Returns true if a frame is available;
    // out must have capacity >= fft_size().
    bool latest(std::span<float> out_dbfs) const;

    [[nodiscard]] std::uint64_t frame_count() const noexcept;

private:
    void compute_frame_locked();

    std::size_t n_;
    Fft fft_;
    std::vector<float> window_;       // Hann
    std::vector<sample_t> ring_;      // length n_
    std::size_t ring_pos_{0};
    std::size_t since_frame_{0};      // input samples accumulated since last FFT

    mutable std::mutex mu_;
    std::vector<float> latest_db_;    // length n_, FFT-shifted
    mutable std::vector<float> held_db_; // max-held rows since previous pull
    mutable bool held_valid_{false};
    std::uint64_t frames_{0};
    std::vector<sample_t> scratch_;   // FFT working buffer
};

} // namespace mrf::dsp
