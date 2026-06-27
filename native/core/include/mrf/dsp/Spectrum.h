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

    // Sets how many raw FFT frames are collapsed into one stored history frame
    // for pull_frames()/frame_count(). latest() still updates every FFT.
    void set_history_frame_stride(std::size_t stride);

    // Push samples. Computes a frame whenever `fft_size` new samples have
    // accumulated since the last frame.
    void push(std::span<const sample_t> samples);

    // Copy the max-held dBFS spectrum since the previous pull (length fft_size,
    // FFT-shifted so DC is at bin n/2). If no new frame arrived since the last
    // pull, copies the most recent frame. Returns true if a frame is available;
    // out must have capacity >= fft_size().
    bool latest(std::span<float> out_dbfs) const;

    // Extract up to count individual (non-max-held) frames from the rolling
    // history, starting after after_frame_index. Fills out_frames with
    // count*fft_size() floats (each fft_size() consecutive floats is one frame).
    // Returns the number of frames actually filled (0 to count). If out_frames
    // is too small or after_frame_index >= current frame count, returns 0.
    [[nodiscard]] std::uint32_t pull_frames(
        std::uint64_t after_frame_index,
        std::uint32_t max_count,
        std::span<float> out_frames) const;

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
    std::uint64_t latest_frames_{0};
    std::uint64_t history_frames_{0};
    std::vector<sample_t> scratch_;   // FFT working buffer
    std::size_t history_frame_stride_{1};
    std::size_t history_frame_accum_{0};

    // Ring buffer of individual (non-max-held) frames. Holds the last 256
    // frames. Each frame is n_ floats (FFT-shifted dBFS). Used by pull_frames().
    std::vector<std::vector<float>> frame_ring_; // length 256, each n_ floats
    std::size_t frame_ring_pos_{0};   // where the next frame will be written
    static constexpr std::size_t kFrameRingCapacity = 256;
};

} // namespace mrf::dsp
