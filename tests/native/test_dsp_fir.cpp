// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/dsp/Fir.h"

#include <gtest/gtest.h>

#include <cmath>
#include <complex>
#include <numbers>
#include <vector>

using mrf::dsp::FirDecimator;

namespace {

constexpr float kPi = std::numbers::pi_v<float>;

std::vector<std::complex<float>> tone(std::size_t n, float freq_normalized) {
    std::vector<std::complex<float>> out(n);
    for (std::size_t i = 0; i < n; ++i) {
        const float ph = 2.0f * kPi * freq_normalized * static_cast<float>(i);
        out[i] = std::complex<float>(std::cos(ph), std::sin(ph));
    }
    return out;
}

float mean_power(const std::vector<std::complex<float>>& x) {
    double s = 0.0;
    for (auto v : x) s += static_cast<double>(v.real() * v.real() + v.imag() * v.imag());
    return static_cast<float>(s / std::max<std::size_t>(1, x.size()));
}

} // namespace

TEST(FirDecimator, M1IsApproximatelyUnity) {
    FirDecimator fir(/*decimation*/ 1u);
    EXPECT_EQ(fir.decimation(), 1u);

    auto in = tone(2048, 0.05f);
    auto out_span = fir.process(in);
    std::vector<std::complex<float>> out(out_span.begin(), out_span.end());

    // M=1 -> same length as input (after filter group delay; input/output
    // sizes match because we feed sample-by-sample and emit one per input).
    EXPECT_EQ(out.size(), in.size());

    // Power should be preserved within a few dB once past the transient.
    std::vector<std::complex<float>> tail(out.end() - 1024, out.end());
    const float in_p = mean_power({in.end() - 1024, in.end()});
    const float out_p = mean_power(tail);
    EXPECT_NEAR(out_p, in_p, 0.05f);
}

TEST(FirDecimator, RejectsAliasingTone) {
    // Decimate by 4: new Nyquist = 0.125 (in cycles/input-sample). A tone at
    // 0.20 should be heavily attenuated (it lives in the stopband).
    FirDecimator fir(/*decimation*/ 4u);

    const std::size_t N = 16384;
    auto in_pass = tone(N, 0.02f);   // well within passband
    auto in_stop = tone(N, 0.20f);   // in stopband

    auto p_span = fir.process(in_pass);
    std::vector<std::complex<float>> pass(p_span.begin(), p_span.end());

    FirDecimator fir2(4u); // fresh instance to avoid filter memory bleed
    auto s_span = fir2.process(in_stop);
    std::vector<std::complex<float>> stop(s_span.begin(), s_span.end());

    EXPECT_EQ(pass.size(), N / 4);
    EXPECT_EQ(stop.size(), N / 4);

    // Discard transient (filter group delay ~num_taps / 2 input samples;
    // num_taps for M=4 is 33 -> ~4 output samples).
    auto tail_pass = std::vector<std::complex<float>>(pass.begin() + 64, pass.end());
    auto tail_stop = std::vector<std::complex<float>>(stop.begin() + 64, stop.end());

    const float pp = mean_power(tail_pass);
    const float sp = mean_power(tail_stop);

    // Passband near unity (input power ~1, allow generous slack).
    EXPECT_GT(pp, 0.5f);
    // Stopband attenuated by at least 30 dB.
    EXPECT_LT(sp, pp * 1e-3f);
}

TEST(FirDecimator, ProducesCorrectOutputCount) {
    FirDecimator fir(8u);
    std::vector<std::complex<float>> in(8000, std::complex<float>{1.0f, 0.0f});
    auto out = fir.process(in);
    EXPECT_EQ(out.size(), in.size() / 8u);
}
