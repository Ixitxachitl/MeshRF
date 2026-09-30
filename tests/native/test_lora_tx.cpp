// SPDX-License-Identifier: GPL-3.0-or-later
//
// Loopback test for the LoRa TX pipeline: encode a known PHY payload into IQ
// via LoraModem::encode (PHY FEC + chirp modulation) and decode it back
// through MeshtasticRx, asserting the header and payload (CRC) recover the
// exact bytes. This proves the encoder is the exact inverse of the receiver.

#include "mrf/modem/LoraModem.h"
#include "mrf/modem/LoraEncoder.h"
#include "mrf/modem/ChirpChatTx.h"
#include "mrf/modem/MeshtasticRx.h"
#include "../../native/core/src/hal/Sx126x.h"

#include <gtest/gtest.h>

#include <complex>
#include <cstdint>
#include <cmath>
#include <random>
#include <vector>

using namespace mrf::modem;

namespace {

using cf = std::complex<float>;

struct LoopResult {
    bool header_fired = false;
    bool header_ok = false;
    std::uint8_t length = 0;
    std::uint8_t cr = 0;
    bool has_crc = false;
    bool payload_fired = false;
    bool crc_ok = false;
    int sync_word = -2;
    std::vector<std::uint8_t> bytes;
};

// rx_sync_word is what the receiver was configured for, which need not be
// what the transmitter sent: the demodulator does not gate on it, and reading
// the transmitted one back is how a frame from another network is recognised.
LoopResult run_loopback(const LoraParams& params,
                        const std::vector<std::uint8_t>& data,
                        std::uint8_t rx_sync_word) {
    constexpr int kOs = 4; // must match LoraModem::kOversampling

    auto modem = make_modem(params);
    const auto frame = modem->encode(
        std::span<const std::uint8_t>(data.data(), data.size()));
    EXPECT_FALSE(frame.empty());

    // Build the input stream: a little lead silence, the frame, and trailing
    // silence so the RX state machine can flush the last symbols.
    const int N = 1 << params.spreading_factor;
    const int sym_samples = N * kOs;
    std::vector<cf> stream;
    stream.reserve(frame.size() + static_cast<std::size_t>(sym_samples) * 20);
    for (int i = 0; i < sym_samples; ++i) stream.emplace_back(0.0f, 0.0f);
    for (const auto& s : frame) stream.emplace_back(s.real(), s.imag());
    for (int i = 0; i < sym_samples * 16; ++i) stream.emplace_back(0.0f, 0.0f);

    MeshtasticRx rx(params.spreading_factor, params.bandwidth_hz, kOs,
                    rx_sync_word);
    LoopResult res;
    rx.set_header_callback([&](const HeaderEvent& ev) {
        if (res.header_fired) return;
        res.header_fired = true;
        res.header_ok = ev.parity_ok;
        res.length = ev.payload_length;
        res.cr = ev.coding_rate;
        res.has_crc = ev.has_crc;
    });
    rx.set_payload_callback([&](const PayloadEvent& ev) {
        if (res.payload_fired) return;
        res.payload_fired = true;
        res.crc_ok = ev.crc_ok;
        res.sync_word = ev.sync_word;
        res.bytes.assign(ev.bytes, ev.bytes + ev.length);
    });
    rx.process(std::span<const cf>(stream.data(), stream.size()));
    return res;
}

LoopResult run_loopback(const LoraParams& params,
                        const std::vector<std::uint8_t>& data) {
    return run_loopback(params, data, params.sync_word);
}

std::vector<std::uint8_t> make_payload(std::size_t n) {
    // A plausible on-air frame: 16-byte L1 header + body. The exact contents
    // don't matter for the PHY loopback, only that they round-trip.
    std::vector<std::uint8_t> d(n);
    for (std::size_t i = 0; i < n; ++i)
        d[i] = static_cast<std::uint8_t>(0x40 + (i * 7 + 3) % 0xB0);
    return d;
}

// A custom mesh on SF5 or SF6, which only SX126x-class radios transmit.
LoraParams low_sf(std::uint8_t sf, std::uint32_t bw_hz, std::uint8_t cr) {
    LoraParams p = params_for(Preset::ShortTurbo);
    p.spreading_factor = sf;
    p.bandwidth_hz = bw_hz;
    p.coding_rate = cr;
    p.low_data_rate_optimize = false;
    return p;
}

} // namespace

// A Meshtastic frame reports the sync word it was sent under, so a listener
// can say a frame is one of ours rather than assuming it.
TEST(LoraTx, ReportsMeshtasticSyncWord) {
    LoraParams p = params_for(Preset::MediumFast);
    auto res = run_loopback(p, make_payload(32));
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.sync_word, 0x2B);
}

// MeshCore sends under RadioLib's private sync word rather than Meshtastic's.
// Sharing a channel, its frames demodulate here in full — the receiver never
// gated on the sync word — so the only thing that marks one as not ours is the
// sync word read back off the air.
TEST(LoraTx, ReportsForeignSyncWordOnAMeshtasticListener) {
    LoraParams p = params_for(Preset::MediumFast);
    p.sync_word = 0x12u; // RADIOLIB_SX126X_SYNC_WORD_PRIVATE, what MeshCore uses
    auto data = make_payload(32);
    auto res = run_loopback(p, data, /*rx_sync_word=*/0x2B);

    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
    EXPECT_EQ(res.sync_word, 0x12);
}

// Nibbles resolve across the bin space, the low nibble including 0, whose
// chirp sits at bin zero and so can be read either side of the wrap at the top.
// A *high* nibble of 0 is left out: frame sync absorbs a first sync chirp at
// bin 0 as another preamble chirp, since at that bin the two are the same
// thing, so such a sync word cannot be received at all and no network uses one.
TEST(LoraTx, ResolvesSyncWordsAcrossTheBinSpace) {
    constexpr std::uint8_t kSyncWords[] = {0x12, 0x2B, 0x34, 0xF0, 0xFF};
    for (const std::uint8_t sw : kSyncWords) {
        LoraParams p = params_for(Preset::ShortFast);
        p.sync_word = sw;
        auto res = run_loopback(p, make_payload(24), /*rx_sync_word=*/0x2B);
        ASSERT_TRUE(res.payload_fired) << "sync word " << static_cast<int>(sw);
        EXPECT_EQ(res.sync_word, sw) << "sync word " << static_cast<int>(sw);
    }
}

TEST(LoraTx, RoundTripShortFastSf7) {
    LoraParams p = params_for(Preset::ShortFast); // SF7 / 250k / 4-5
    auto data = make_payload(24);
    auto res = run_loopback(p, data);
    ASSERT_TRUE(res.header_fired);
    EXPECT_TRUE(res.header_ok);
    EXPECT_EQ(res.length, data.size());
    EXPECT_EQ(res.cr, params_for(Preset::ShortFast).coding_rate - 4);
    EXPECT_TRUE(res.has_crc);
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
}

TEST(LoraTx, RoundTripMediumFastSf9) {
    LoraParams p = params_for(Preset::MediumFast); // SF9 / 250k / 4-5
    auto data = make_payload(32);
    auto res = run_loopback(p, data);
    ASSERT_TRUE(res.header_fired);
    EXPECT_TRUE(res.header_ok);
    EXPECT_EQ(res.length, data.size());
    EXPECT_TRUE(res.has_crc);
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
}

TEST(LoraTx, RoundTripLongFastSf11) {
    LoraParams p = params_for(Preset::LongFast); // SF11 / 250k / 4-5 (default)
    auto data = make_payload(40);
    auto res = run_loopback(p, data);
    ASSERT_TRUE(res.header_fired);
    EXPECT_TRUE(res.header_ok);
    EXPECT_EQ(res.length, data.size());
    EXPECT_TRUE(res.has_crc);
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
}

TEST(LoraTx, RoundTripLongModerateSf11Ldro) {
    LoraParams p = params_for(Preset::LongModerate); // SF11 / 125k / 4-8, LDRO
    auto data = make_payload(28);
    auto res = run_loopback(p, data);
    ASSERT_TRUE(res.header_fired);
    EXPECT_TRUE(res.header_ok);
    EXPECT_EQ(res.length, data.size());
    EXPECT_TRUE(res.has_crc);
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
}

// `meshtastic --set lora.spread_factor 5 --set lora.bandwidth 500
// --set lora.coding_rate 8`: the header block at full rate and the two
// fine-sync chirps after the SFD.
TEST(LoraTx, RoundTripSf5) {
    LoraParams p = low_sf(5, 500'000, 8);
    auto data = make_payload(40);
    auto res = run_loopback(p, data);
    ASSERT_TRUE(res.header_fired);
    EXPECT_TRUE(res.header_ok);
    EXPECT_EQ(res.length, data.size());
    EXPECT_EQ(res.cr, 4);
    EXPECT_TRUE(res.has_crc);
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
}

// SF6 carries one payload nibble in the header block, as SF8 does.
TEST(LoraTx, RoundTripSf6) {
    LoraParams p = low_sf(6, 250'000, 5);
    auto data = make_payload(33);
    auto res = run_loopback(p, data);
    ASSERT_TRUE(res.header_fired);
    EXPECT_TRUE(res.header_ok);
    EXPECT_EQ(res.length, data.size());
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.bytes, data);
}

// At SF5 the sync chirps wrap: 0x2B's B nibble lands on the bin an 3 would.
// Read against the listener's own sync word it is still Meshtastic, which is
// what keeps the app from dropping every frame as foreign.
TEST(LoraTx, ReportsMeshtasticSyncWordAtSf5) {
    auto res = run_loopback(low_sf(5, 500'000, 8), make_payload(32));
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.sync_word, 0x2B);
}

// A bin that does not fit the expected nibble is ambiguous at SF5, so it is
// reported as unknown rather than guessed.
TEST(LoraTx, ReportsAnAmbiguousForeignSyncWordAsUnknownAtSf5) {
    LoraParams p = low_sf(5, 500'000, 8);
    p.sync_word = 0x12u;
    auto res = run_loopback(p, make_payload(32), /*rx_sync_word=*/0x2B);
    ASSERT_TRUE(res.payload_fired);
    EXPECT_TRUE(res.crc_ok);
    EXPECT_EQ(res.sync_word, -1);
}

// The frame we transmit is exactly as long as a radio would make it: the
// datasheet's time on air, which counts the SF5/6 fine sync and full-rate
// header block, times the modem's sample rate.
TEST(LoraTx, FrameLengthMatchesTheRadiosTimeOnAir) {
    constexpr int kOs = 4; // LoraModem::kOversampling
    const LoraParams cases[] = {
        low_sf(5, 500'000, 8), low_sf(6, 250'000, 5),
        params_for(Preset::ShortFast), params_for(Preset::LongModerate)};
    for (const auto& p : cases) {
        for (const std::size_t len : {16u, 37u, 200u}) {
            const auto data = make_payload(len);
            const auto frame = make_modem(p)->encode(
                std::span<const std::uint8_t>(data.data(), data.size()));
            const double expected =
                mrf::hal::lora_airtime_seconds(p, len) * p.bandwidth_hz * kOs;
            EXPECT_NEAR(static_cast<double>(frame.size()), expected, 0.5)
                << "SF" << int(p.spreading_factor) << " " << len << " B";
        }
    }
}

// With 32 bins, noise lands six symbols running within a bin of each other
// every few seconds, which read as a preamble and logged a bad header each
// time. SF5 asks for a longer run so it false-alarms no more than SF7 does.
TEST(LoraTx, NoiseIsNotAPreambleAtLowSpreadingFactors) {
    constexpr int kOs = 4;
    for (const std::uint8_t sf : {5, 6}) {
        const std::uint32_t bw = 500'000;
        MeshtasticRx rx(sf, bw, kOs);
        std::mt19937 rng(1234 + sf);
        std::normal_distribution<float> g(0.0f, 1.0f);
        std::vector<cf> block(1u << 16);
        const std::uint64_t total = 30ull * bw * kOs; // 30 s of air
        for (std::uint64_t done = 0; done < total; done += block.size()) {
            for (auto& s : block) s = {g(rng), g(rng)};
            rx.process(std::span<const cf>(block.data(), block.size()));
        }
        EXPECT_EQ(rx.preambles_detected(), 0u) << "SF" << int(sf);
    }
}

// And asking for that longer run does not cost frames a shorter one decodes:
// both lose some at +3 dB and none at +6.
TEST(LoraTx, Sf5StillDecodesInNoise) {
    constexpr int kOs = 4;
    const LoraParams p = low_sf(5, 500'000, 8);
    const auto data = make_payload(40);
    const auto frame = make_modem(p)->encode(
        std::span<const std::uint8_t>(data.data(), data.size()));
    std::mt19937 rng(99);
    // SNR in the channel bandwidth: noise power spread over kOs times it.
    const float snr_db = 6.0f;
    const float sigma = std::sqrt(kOs * std::pow(10.0f, -snr_db / 10.0f) / 2.0f);
    std::normal_distribution<float> g(0.0f, sigma);

    int decoded = 0;
    constexpr int kFrames = 20;
    for (int f = 0; f < kFrames; ++f) {
        MeshtasticRx rx(p.spreading_factor, p.bandwidth_hz, kOs);
        bool ok = false;
        rx.set_payload_callback([&](const PayloadEvent& ev) { ok = ok || ev.crc_ok; });
        std::vector<cf> stream;
        const int lead = 1000 + f * 37; // arbitrary timing
        for (int i = 0; i < lead; ++i) stream.emplace_back(g(rng), g(rng));
        for (const auto& s : frame) stream.emplace_back(s.real() + g(rng), s.imag() + g(rng));
        for (int i = 0; i < 32 * kOs * 20; ++i) stream.emplace_back(g(rng), g(rng));
        rx.process(std::span<const cf>(stream.data(), stream.size()));
        decoded += ok ? 1 : 0;
    }
    EXPECT_GE(decoded, kFrames - 1);
}
