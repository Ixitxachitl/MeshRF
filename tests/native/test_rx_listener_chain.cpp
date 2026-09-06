// SPDX-License-Identifier: GPL-3.0-or-later
//
// RxListenerChain: several LoRa channels demodulated off one wide capture.
// Each test synthesises frames with the production encoder, places them at
// offsets in a device-rate capture the way an SDR would deliver them, and
// checks that each listener receives its own frame and no other's.

#include "mrf/dsp/Resampler.h"
#include "mrf/modem/LoraModem.h"
#include "mrf/modem/RxListenerChain.h"

#include <gtest/gtest.h>

#include <cmath>
#include <complex>
#include <cstdint>
#include <map>
#include <numbers>
#include <span>
#include <string>
#include <vector>

using namespace mrf::modem;

namespace {

using cf = std::complex<float>;
constexpr double kTwoPi = 2.0 * std::numbers::pi;

std::vector<std::uint8_t> make_payload(std::size_t n, std::uint8_t seed) {
    std::vector<std::uint8_t> d(n);
    for (std::size_t i = 0; i < n; ++i)
        d[i] = static_cast<std::uint8_t>(seed + (i * 7 + 3) % 0xB0);
    return d;
}

// One frame for `params`, modulated at the chip rate, brought up to the
// device rate and mixed to `offset_hz`, the way it would arrive off the air.
std::vector<cf> frame_at(const LoraParams& params,
                         std::uint32_t device_rate_hz,
                         double offset_hz,
                         const std::vector<std::uint8_t>& payload,
                         float amplitude) {
    auto modem = make_modem(params);
    auto iq = modem->encode(std::span<const std::uint8_t>(payload.data(), payload.size()));
    const std::uint32_t modem_rate = modem->working_sample_rate_hz();
    // Silence either side, so the resampler's warm-up and the frame's tail
    // do not overlap the chirps.
    iq.insert(iq.begin(), modem_rate / 50u, cf{0.0f, 0.0f});
    iq.insert(iq.end(), modem_rate / 200u, cf{0.0f, 0.0f});

    mrf::dsp::Resampler up(modem_rate, device_rate_hz);
    const auto rs = up.process(std::span<const cf>(iq.data(), iq.size()));

    std::vector<cf> out;
    out.reserve(rs.size());
    const double inc = kTwoPi * offset_hz / device_rate_hz;
    double phase = 0.0;
    for (const auto& s : rs) {
        out.push_back(s * amplitude * cf(static_cast<float>(std::cos(phase)),
                                         static_cast<float>(std::sin(phase))));
        phase += inc;
    }
    return out;
}

// Signals summed sample by sample over a low noise floor, then padded so the
// receivers can flush their last symbols. The noise is not decoration: a
// stretch of exact zeros dechirps to the same peak bin symbol after symbol,
// which the preamble detector takes for a preamble and locks on, and the
// real frame behind it is then mis-framed. No capture off an SDR is ever
// exactly zero, so the synthesis should not be either.
std::vector<cf> capture_of(const std::vector<std::vector<cf>>& parts,
                           std::uint32_t device_rate_hz) {
    std::size_t longest = 0;
    for (const auto& p : parts) longest = std::max(longest, p.size());
    std::vector<cf> sum(longest + device_rate_hz / 2u, cf{0.0f, 0.0f});
    std::uint32_t lcg = 0x2545F491u;
    auto uniform = [&lcg] {
        lcg = lcg * 1664525u + 1013904223u;
        return (static_cast<double>(lcg >> 8) + 0.5) / 16777216.0;
    };
    constexpr float kNoiseAmplitude = 0.01f; // 40 dB under a unit-amplitude chirp
    for (auto& s : sum) {
        const double r = std::sqrt(-2.0 * std::log(uniform()));
        const double t = kTwoPi * uniform();
        s = cf(static_cast<float>(r * std::cos(t)), static_cast<float>(r * std::sin(t))) * kNoiseAmplitude;
    }
    for (const auto& p : parts)
        for (std::size_t i = 0; i < p.size(); ++i) sum[i] += p[i];
    return sum;
}

struct Received {
    std::map<int, std::vector<std::vector<std::uint8_t>>> frames;
    std::map<int, int> payload_lines;
};

void collect(RxListenerChain& chain, Received& r) {
    chain.set_frame_callback([&r](int listener, const DecodedFrame& f) {
        r.frames[listener].push_back(f.payload);
    });
    chain.set_event_callback([&r](int listener, std::string msg) {
        if (msg.rfind("  payload", 0) == 0) ++r.payload_lines[listener];
    });
}

// Feed the capture through every chain in blocks of `block` samples: the
// device delivers blocks, and the mixer's phase has to carry across them.
void run(std::vector<RxListenerChain*> chains, const std::vector<cf>& capture, std::size_t block) {
    for (std::size_t i = 0; i < capture.size(); i += block) {
        const std::size_t n = std::min(block, capture.size() - i);
        for (auto* c : chains) c->process(std::span<const cf>(capture.data() + i, n));
    }
}

} // namespace

TEST(RxListenerChain, TwoPresetsAtOffsetsReachTheirOwnListeners) {
    constexpr std::uint32_t kRate = 2'400'000u;
    const auto medium = params_for(Preset::MediumFast);
    const auto lng    = params_for(Preset::LongFast);
    const auto a = make_payload(32, 0x40);
    const auto b = make_payload(40, 0x21);

    const auto capture = capture_of({frame_at(medium, kRate, +600'000.0, a, 1.0f),
                                     frame_at(lng,    kRate, -600'000.0, b, 1.0f)}, kRate);

    const RxListenerChain::Member m0{0, medium};
    const RxListenerChain::Member m1{1, lng};
    RxListenerChain c0(kRate, +600'000, medium.bandwidth_hz, std::span<const RxListenerChain::Member>(&m0, 1));
    RxListenerChain c1(kRate, -600'000, lng.bandwidth_hz,    std::span<const RxListenerChain::Member>(&m1, 1));
    Received r;
    collect(c0, r);
    collect(c1, r);
    run({&c0, &c1}, capture, 32768);

    ASSERT_EQ(r.frames[0].size(), 1u) << "listener 0 should hear its MediumFast frame once";
    EXPECT_EQ(r.frames[0][0], a);
    ASSERT_EQ(r.frames[1].size(), 1u) << "listener 1 should hear its LongFast frame once";
    EXPECT_EQ(r.frames[1][0], b);
}

TEST(RxListenerChain, WiderCaptureAtFourMegasamples) {
    constexpr std::uint32_t kRate = 4'000'000u;
    const auto medium = params_for(Preset::MediumFast);
    const auto shrt   = params_for(Preset::ShortFast);
    const auto a = make_payload(24, 0x10);
    const auto b = make_payload(24, 0x55);

    const auto capture = capture_of({frame_at(medium, kRate, +1'200'000.0, a, 1.0f),
                                     frame_at(shrt,   kRate, -1'200'000.0, b, 1.0f)}, kRate);

    const RxListenerChain::Member m0{0, medium};
    const RxListenerChain::Member m1{1, shrt};
    RxListenerChain c0(kRate, +1'200'000, medium.bandwidth_hz, std::span<const RxListenerChain::Member>(&m0, 1));
    RxListenerChain c1(kRate, -1'200'000, shrt.bandwidth_hz,   std::span<const RxListenerChain::Member>(&m1, 1));
    Received r;
    collect(c0, r);
    collect(c1, r);
    run({&c0, &c1}, capture, 32768);

    ASSERT_EQ(r.frames[0].size(), 1u);
    EXPECT_EQ(r.frames[0][0], a);
    ASSERT_EQ(r.frames[1].size(), 1u);
    EXPECT_EQ(r.frames[1][0], b);
}

TEST(RxListenerChain, ListenersSharingAChannelHearOnlyTheirOwnPreset) {
    constexpr std::uint32_t kRate = 2'400'000u;
    const auto medium = params_for(Preset::MediumFast);
    const auto lng    = params_for(Preset::LongFast);
    const auto a = make_payload(32, 0x40);

    const auto capture = capture_of({frame_at(medium, kRate, +300'000.0, a, 1.0f)}, kRate);

    // Same channel, two spreading factors: one chain, two demodulators.
    const RxListenerChain::Member members[] = {{0, medium}, {1, lng}};
    RxListenerChain chain(kRate, +300'000, medium.bandwidth_hz,
                          std::span<const RxListenerChain::Member>(members, 2));
    ASSERT_TRUE(chain.has_listener(0));
    ASSERT_TRUE(chain.has_listener(1));
    Received r;
    collect(chain, r);
    run({&chain}, capture, 32768);

    ASSERT_EQ(r.frames[0].size(), 1u);
    EXPECT_EQ(r.frames[0][0], a);
    EXPECT_TRUE(r.frames[1].empty()) << "a LongFast demodulator must not decode a MediumFast frame";
    EXPECT_EQ(r.payload_lines[1], 0) << "nor report a payload for it";
}

TEST(RxListenerChain, NonIntegerRateRatioDecodes) {
    // 12.5 MS/s to the 1 MS/s chip stream is 2/25, not a whole decimation.
    constexpr std::uint32_t kRate = 12'500'000u;
    const auto medium = params_for(Preset::MediumFast);
    const auto a = make_payload(24, 0x33);

    const auto capture = capture_of({frame_at(medium, kRate, +2'000'000.0, a, 1.0f)}, kRate);

    const RxListenerChain::Member m0{0, medium};
    RxListenerChain chain(kRate, +2'000'000, medium.bandwidth_hz,
                          std::span<const RxListenerChain::Member>(&m0, 1));
    EXPECT_EQ(chain.working_rate_hz(), 1'000'000u);
    Received r;
    collect(chain, r);
    run({&chain}, capture, 65536);

    ASSERT_EQ(r.frames[0].size(), 1u);
    EXPECT_EQ(r.frames[0][0], a);
}

TEST(RxListenerChain, MixerPhaseCarriesAcrossOddBlocks) {
    // Blocks that never line up with the rotator's renormalisation period,
    // so a phase discontinuity at a block edge would land mid-frame.
    constexpr std::uint32_t kRate = 2'400'000u;
    const auto lng = params_for(Preset::LongFast);
    const auto a = make_payload(40, 0x21);

    const auto capture = capture_of({frame_at(lng, kRate, -700'000.0, a, 1.0f)}, kRate);

    for (const std::size_t block : {std::size_t{1000}, std::size_t{4097}, std::size_t{777}}) {
        const RxListenerChain::Member m0{0, lng};
        RxListenerChain chain(kRate, -700'000, lng.bandwidth_hz,
                              std::span<const RxListenerChain::Member>(&m0, 1));
        Received r;
        collect(chain, r);
        run({&chain}, capture, block);
        ASSERT_EQ(r.frames[0].size(), 1u) << "block size " << block;
        EXPECT_EQ(r.frames[0][0], a) << "block size " << block;
    }
}

TEST(RxListenerChain, ChannelAtDeviceCentreNeedsNoMixing) {
    constexpr std::uint32_t kRate = 2'400'000u;
    const auto medium = params_for(Preset::MediumFast);
    const auto a = make_payload(32, 0x40);

    const auto capture = capture_of({frame_at(medium, kRate, 0.0, a, 1.0f)}, kRate);

    const RxListenerChain::Member m0{0, medium};
    RxListenerChain chain(kRate, 0, medium.bandwidth_hz,
                          std::span<const RxListenerChain::Member>(&m0, 1));
    Received r;
    collect(chain, r);
    run({&chain}, capture, 32768);

    ASSERT_EQ(r.frames[0].size(), 1u);
    EXPECT_EQ(r.frames[0][0], a);
}

TEST(RxListenerChain, AdjacentChannelTwentyDecibelsStronger) {
    // Two 250 kHz channels one slot apart, the neighbour 20 dB stronger. The
    // channel filter passes twice the bandwidth, so the neighbour reaches the
    // demodulator; this records what that costs rather than asserting it,
    // since the outcome is the demodulator's business.
    constexpr std::uint32_t kRate = 2'400'000u;
    const auto medium = params_for(Preset::MediumFast);
    const auto lng    = params_for(Preset::LongFast);
    const auto weak   = make_payload(32, 0x40);
    const auto strong = make_payload(40, 0x21);

    const auto capture = capture_of({frame_at(medium, kRate, 0.0,        weak,   0.1f),
                                     frame_at(lng,    kRate, +250'000.0, strong, 1.0f)}, kRate);

    const RxListenerChain::Member m0{0, medium};
    const RxListenerChain::Member m1{1, lng};
    RxListenerChain c0(kRate, 0,        medium.bandwidth_hz, std::span<const RxListenerChain::Member>(&m0, 1));
    RxListenerChain c1(kRate, +250'000, lng.bandwidth_hz,    std::span<const RxListenerChain::Member>(&m1, 1));
    Received r;
    collect(c0, r);
    collect(c1, r);
    run({&c0, &c1}, capture, 32768);

    ASSERT_EQ(r.frames[1].size(), 1u) << "the strong neighbour must decode";
    EXPECT_EQ(r.frames[1][0], strong);
    const bool weak_decoded = r.frames[0].size() == 1u && r.frames[0][0] == weak;
    RecordProperty("weak_channel_decoded_beside_20dB_neighbour", weak_decoded ? "yes" : "no");
}

TEST(RxListenerChain, RefusesAChannelOutsideTheCapture) {
    const auto medium = params_for(Preset::MediumFast);
    const RxListenerChain::Member m0{0, medium};
    EXPECT_THROW(RxListenerChain(2'400'000u, +1'150'000, medium.bandwidth_hz,
                                 std::span<const RxListenerChain::Member>(&m0, 1)),
                 std::invalid_argument);
    EXPECT_THROW(RxListenerChain(2'400'000u, 0, 0u,
                                 std::span<const RxListenerChain::Member>(&m0, 1)),
                 std::invalid_argument);
    const auto turbo = params_for(Preset::ShortTurbo);
    const RxListenerChain::Member mixed[] = {{0, medium}, {1, turbo}};
    EXPECT_THROW(RxListenerChain(2'400'000u, 0, medium.bandwidth_hz,
                                 std::span<const RxListenerChain::Member>(mixed, 2)),
                 std::invalid_argument);
}
