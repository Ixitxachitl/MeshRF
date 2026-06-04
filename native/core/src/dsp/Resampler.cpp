// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Resampler.h"

#include <cmath>
#include <numbers>
#include <numeric>
#include <stdexcept>

namespace mrf::dsp {

namespace {
constexpr float kPi = std::numbers::pi_v<float>;

// Windowed-sinc low-pass, Hamming window, unity-DC-sum normalized.
std::vector<float> design_lowpass(std::size_t num_taps, float cutoff_normalized) {
    if (num_taps < 3) num_taps = 3;
    std::vector<float> h(num_taps);
    const float fc = cutoff_normalized;
    const float center = (num_taps - 1) * 0.5f;
    float sum = 0.0f;
    for (std::size_t i = 0; i < num_taps; ++i) {
        const float n = static_cast<float>(i) - center;
        float s;
        if (std::abs(n) < 1e-6f) {
            s = 2.0f * fc;
        } else {
            s = std::sin(2.0f * kPi * fc * n) / (kPi * n);
        }
        const float w = 0.54f - 0.46f * std::cos(2.0f * kPi *
                                                 static_cast<float>(i) /
                                                 static_cast<float>(num_taps - 1));
        h[i] = s * w;
        sum += h[i];
    }
    if (sum != 0.0f) {
        for (auto& c : h) c /= sum; // unity DC gain
    }
    return h;
}
} // namespace

Resampler::Resampler(std::uint32_t input_rate_hz, std::uint32_t output_rate_hz)
    : in_rate_(input_rate_hz), out_rate_(output_rate_hz) {
    if (in_rate_ == 0 || out_rate_ == 0)
        throw std::invalid_argument("Resampler: zero sample rate");

    // Reduce the rate ratio to L/M.
    const std::uint32_t g = std::gcd(in_rate_, out_rate_);
    L_ = out_rate_ / g; // interpolation
    M_ = in_rate_ / g;  // decimation

    // Prototype low-pass at the upsampled (L*input) rate. The combined
    // anti-imaging / anti-aliasing cutoff is the lower of the input and output
    // Nyquist edges: f = 0.5 / max(L, M) cycles/sample of the upsampled stream.
    const std::uint32_t big = std::max(L_, M_);
    const float cutoff = 0.5f / static_cast<float>(big);

    // ~12 taps per upsampled-Nyquist period gives a clean transition; round the
    // prototype length up to a whole number of polyphase branches (multiple L).
    std::size_t nh = static_cast<std::size_t>(12u * big) + 1u;
    nh = ((nh + L_ - 1) / L_) * L_;          // multiple of L_
    if (nh < L_) nh = L_;
    taps_per_phase_ = nh / L_;

    proto_ = design_lowpass(nh, cutoff);
    // Scale so each polyphase branch has ~unity DC gain (interpolation by L
    // spreads the prototype's unity sum across L branches).
    for (auto& c : proto_) c *= static_cast<float>(L_);

    // History must cover taps_per_phase_ input samples plus the few extra the
    // commutator may reach back for between outputs.
    hist_size_ = taps_per_phase_ + M_ + 2u;
    hist_.assign(hist_size_, cf{0.0f, 0.0f});
}

Resampler::~Resampler() = default;

std::span<const std::complex<float>>
Resampler::process(std::span<const std::complex<float>> in) {
    out_.clear();
    // Upper bound on outputs: one per L/M input samples, +1 slack.
    out_.reserve(in.size() * L_ / M_ + 1);

    const std::size_t K = taps_per_phase_;
    const std::size_t H = hist_size_;
    const std::uint64_t L = L_;
    const std::uint64_t M = M_;

    for (const cf& x : in) {
        hist_[wpos_] = x;
        wpos_ = (wpos_ + 1) % H;
        ++in_count_;
        const std::uint64_t newest = in_count_ - 1; // absolute index of x

        // Emit every output whose base input index is now available.
        while ((next_out_ * M) / L <= newest) {
            const std::uint64_t u = next_out_ * M;
            const std::uint64_t base = u / L;          // newest input it taps
            const std::size_t branch = static_cast<std::size_t>(u % L);
            cf acc{0.0f, 0.0f};
            for (std::size_t k = 0; k < K; ++k) {
                if (base < k) break;                   // before stream start
                const std::uint64_t a = base - k;      // absolute input index
                const cf& xv = hist_[static_cast<std::size_t>(a % H)];
                acc += proto_[branch + k * L_] * xv;
            }
            out_.push_back(acc);
            ++next_out_;
        }
    }
    return std::span<const cf>(out_.data(), out_.size());
}

} // namespace mrf::dsp
