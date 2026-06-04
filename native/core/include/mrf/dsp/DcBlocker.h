// SPDX-License-Identifier: GPL-3.0-or-later
//
// Single-pole IIR DC blocker for complex<float> streams.
//   y[n] = x[n] - x[n-1] + R * y[n-1]
// At R=0.995 the -3 dB corner is approximately (1-R)*Fs/(2*pi).

#pragma once

#include <complex>
#include <cstdint>
#include <span>

namespace mrf::dsp {

class DcBlocker {
public:
    using sample_t = std::complex<float>;

    explicit DcBlocker(float pole = 0.995f) noexcept : R_(pole) {}

    void reset() noexcept { prev_x_ = {}; prev_y_ = {}; }

    void process(std::span<sample_t> data) noexcept {
        for (auto& s : data) {
            const sample_t y = s - prev_x_ + R_ * prev_y_;
            prev_x_ = s;
            prev_y_ = y;
            s = y;
        }
    }

    // Estimated DC level (running tap of x[n-1]).
    [[nodiscard]] sample_t dc_estimate() const noexcept { return prev_x_; }

private:
    float R_;
    sample_t prev_x_{};
    sample_t prev_y_{};
};

} // namespace mrf::dsp
