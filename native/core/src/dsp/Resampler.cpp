// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Resampler.h"

#include <cmath>
#include <numbers>
#include <algorithm>
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

    auto proto = design_lowpass(nh, cutoff);
    // Scale so each polyphase branch has ~unity DC gain (interpolation by L
    // spreads the prototype's unity sum across L branches).
    for (auto& c : proto) c *= static_cast<float>(L_);

    // Split the prototype into its phases now, and reverse each phase, so the
    // inner loop reads both taps and samples forwards: an output is
    // sum over j of branch[j] * x[base - K + 1 + j].
    const std::size_t K = taps_per_phase_;
    branches_.assign(static_cast<std::size_t>(L_) * K, 0.0f);
    for (std::uint32_t b = 0; b < L_; ++b)
        for (std::size_t j = 0; j < K; ++j)
            branches_[b * K + j] = proto[b + (K - 1 - j) * L_];

    // History must cover taps_per_phase_ input samples plus the few extra the
    // commutator may reach back for between outputs.
    hist_len_ = K - 1u + M_ + 2u;
    hist_.assign(hist_len_, cf{0.0f, 0.0f});

    step_base_ = M_ / L_;
    step_branch_ = M_ % L_;
}

Resampler::~Resampler() = default;

std::span<const std::complex<float>>
Resampler::process(std::span<const std::complex<float>> in) {
    out_.clear();
    if (in.empty()) return {};

    const std::size_t K = taps_per_phase_;
    const std::size_t n = in.size();
    const std::uint64_t block_start = in_count_;
    const std::uint64_t newest = block_start + n - 1;

    // The block's head joined onto the tail of the one before it. Only the
    // first few outputs of a block straddle that seam; the rest read the
    // caller's buffer directly, which is why nothing here copies the block.
    const std::size_t head = std::min(n, hist_len_);
    edge_.resize(hist_len_ + head);
    std::copy(hist_.begin(), hist_.end(), edge_.begin());
    std::copy(in.begin(), in.begin() + static_cast<std::ptrdiff_t>(head),
              edge_.begin() + static_cast<std::ptrdiff_t>(hist_len_));
    const std::int64_t edge_base = static_cast<std::int64_t>(block_start)
                                 - static_cast<std::int64_t>(hist_len_);

    out_.reserve(n * L_ / M_ + 2u);

    while (base_ <= newest) {
        const cf* x;
        if (base_ >= block_start + (K - 1)) {
            x = in.data() + static_cast<std::size_t>(base_ - block_start - (K - 1));
        } else {
            x = edge_.data() + static_cast<std::size_t>(
                    static_cast<std::int64_t>(base_) - static_cast<std::int64_t>(K - 1) - edge_base);
        }

        // Read as interleaved floats: the taps are real, so this is two
        // independent dot products over one run of memory.
        const float* h = branches_.data() + static_cast<std::size_t>(branch_) * K;
        const float* xf = reinterpret_cast<const float*>(x);
        float ar = 0.0f, ai = 0.0f;
        for (std::size_t j = 0; j < K; ++j) {
            ar += h[j] * xf[2 * j];
            ai += h[j] * xf[2 * j + 1];
        }
        out_.emplace_back(ar, ai);

        base_ += step_base_;
        branch_ += step_branch_;
        if (branch_ >= L_) { branch_ -= L_; ++base_; }
    }

    // Carry the tail forward for the next block.
    if (n >= hist_len_) {
        std::copy(in.end() - static_cast<std::ptrdiff_t>(hist_len_), in.end(), hist_.begin());
    } else {
        std::copy(hist_.begin() + static_cast<std::ptrdiff_t>(n), hist_.end(), hist_.begin());
        std::copy(in.begin(), in.end(),
                  hist_.end() - static_cast<std::ptrdiff_t>(n));
    }
    in_count_ += n;

    return std::span<const cf>(out_.data(), out_.size());
}

} // namespace mrf::dsp
