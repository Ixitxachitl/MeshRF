// SPDX-License-Identifier: GPL-3.0-or-later
//
// Verifies that ChirpChatRx correctly detects a synthesized LoRa preamble.

#include "mrf/modem/ChirpChatRx.h"

#include <gtest/gtest.h>

#include <cmath>
#include <complex>
#include <numbers>
#include <vector>

namespace {

using cf = std::complex<float>;
constexpr double kPi = std::numbers::pi;

// Generate one LoRa upchirp symbol with shift `s`, length N = 2^SF, at chip
// rate. Same convention as ChirpChatRx::make_downchirp.
std::vector<cf> make_upchirp(int sf, int s) {
    const int N = 1 << sf;
    std::vector<cf> out(N);
    // f_inst(n) = (((s+n) mod N) - N/2) / N    (in cycles per chip)
    // phase[n+1] = phase[n] + 2*pi*f_inst(n)
    double phase = 0.0;
    for (int n = 0; n < N; ++n) {
        out[n] = cf{static_cast<float>(std::cos(phase)),
                    static_cast<float>(std::sin(phase))};
        const double f = (static_cast<double>(((s + n) % N)) - N / 2.0) / N;
        phase += 2.0 * kPi * f;
    }
    return out;
}

} // namespace

TEST(ChirpChatRx, DetectsCleanPreamble) {
    constexpr std::uint8_t SF = 7;            // small for fast test
    constexpr std::uint32_t BW = 125'000;
    const int N = 1 << SF;

    mrf::modem::ChirpChatRx rx(SF, BW);
    int events = 0;
    int locked_bin = -1;
    rx.set_event_callback([&](const mrf::modem::PreambleEvent& ev) {
        ++events;
        locked_bin = ev.symbol_value;
    });

    // 8 unmodulated upchirps = canonical LoRa preamble.
    std::vector<cf> stream;
    stream.reserve(static_cast<std::size_t>(8 * N));
    auto upchirp0 = make_upchirp(SF, 0);
    for (int i = 0; i < 8; ++i)
        stream.insert(stream.end(), upchirp0.begin(), upchirp0.end());

    rx.process({stream.data(), stream.size()});

    EXPECT_GE(events, 1) << "expected preamble lock";
    EXPECT_EQ(locked_bin, 0) << "no CFO -> peak should be in bin 0";
    EXPECT_GE(rx.symbols_processed(), 8u);
}

TEST(ChirpChatRx, ReportsCfoAsBinShift) {
    constexpr std::uint8_t SF = 7;
    constexpr std::uint32_t BW = 125'000;
    const int N = 1 << SF;

    mrf::modem::ChirpChatRx rx(SF, BW);
    float cfo_seen = 0.0f;
    int locked_bin = -1;
    rx.set_event_callback([&](const mrf::modem::PreambleEvent& ev) {
        cfo_seen = ev.cfo_hz;
        locked_bin = ev.symbol_value;
    });

    // 8 upchirps modulated with shift = 5 simulate a +5/N * BW CFO.
    const int shift = 5;
    std::vector<cf> stream;
    auto upchirp_s = make_upchirp(SF, shift);
    for (int i = 0; i < 8; ++i)
        stream.insert(stream.end(), upchirp_s.begin(), upchirp_s.end());

    rx.process({stream.data(), stream.size()});

    EXPECT_EQ(locked_bin, shift);
    const float expected = static_cast<float>(shift) * BW / static_cast<float>(N);
    EXPECT_NEAR(cfo_seen, expected, expected * 0.05f + 1.0f);
}

TEST(ChirpChatRx, IgnoresPureNoise) {
    constexpr std::uint8_t SF = 7;
    constexpr std::uint32_t BW = 125'000;
    const int N = 1 << SF;

    mrf::modem::ChirpChatRx rx(SF, BW);
    int events = 0;
    rx.set_event_callback([&](const mrf::modem::PreambleEvent&) { ++events; });

    // Deterministic pseudo-noise via xorshift.
    std::uint32_t state = 0xC0FFEEu;
    auto rnd = [&]() {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return (static_cast<float>(state) / static_cast<float>(0xFFFFFFFFu)) * 2.0f - 1.0f;
    };
    std::vector<cf> stream(static_cast<std::size_t>(20 * N));
    for (auto& s : stream) s = cf{rnd() * 0.5f, rnd() * 0.5f};

    rx.process({stream.data(), stream.size()});

    EXPECT_EQ(events, 0) << "noise should not trigger preamble lock";
}

// Verify the CFO/STO disentanglement math used at the SFD lock. The preamble
// up-chirp peak is (CFO - STO) mod N and the SFD down-chirp peak is
// (CFO + STO) mod N, so the decoder must recover CFO = (up+down)/2 and
// STO = (down-up)/2 (each folded into the signed range [-N/2, N/2)).
TEST(ChirpChatRx, DisentanglesCfoAndSto) {
    constexpr int N = 128; // SF7
    struct Case { int cfo; int sto; };
    const Case cases[] = {
        {0, 0}, {5, 0}, {0, 7}, {5, 7}, {-5, 7}, {5, -7},
        {-20, 13}, {30, -11}, {-30, 11}, {20, 20}, {-20, -20},
    };
    auto wrap = [](int v) { v %= N; if (v < 0) v += N; return v; };
    for (const auto& c : cases) {
        const int up_bin   = wrap(c.cfo - c.sto);
        const int down_bin = wrap(c.cfo + c.sto);
        int cfo = 0, sto = 0;
        mrf::modem::ChirpChatRx::disentangle_cfo_sto(up_bin, down_bin, N, cfo, sto);
        EXPECT_EQ(cfo, c.cfo) << "cfo=" << c.cfo << " sto=" << c.sto;
        EXPECT_EQ(sto, c.sto) << "cfo=" << c.cfo << " sto=" << c.sto;
    }
}
