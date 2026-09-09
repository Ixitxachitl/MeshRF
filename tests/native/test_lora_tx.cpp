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

#include <gtest/gtest.h>

#include <complex>
#include <cstdint>
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
