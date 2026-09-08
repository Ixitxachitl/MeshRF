// SPDX-License-Identifier: GPL-3.0-or-later
//
// What a preamble line says about the signal it was found in.
//
// Two figures ride on it, and both used to be wrong in ways nothing in the
// app could see. The level was not on the line at all: it was asked of the
// receiver later, by which time the frame had ended and the answer was the
// noise that followed. The SNR was the dechirped peak over the other bins,
// which is the ratio *after* despreading and so carries the spreading gain --
// tens of dB, and a different number of them per spreading factor.

#include "mrf/modem/LoraModem.h"
#include "mrf/modem/RxListenerChain.h"

#include <gtest/gtest.h>

#include <cmath>
#include <complex>
#include <cstdint>
#include <numbers>
#include <optional>
#include <span>
#include <string>
#include <vector>

using namespace mrf::modem;

namespace {

using cf = std::complex<float>;
constexpr double kTwoPi = 2.0 * std::numbers::pi;

std::vector<std::uint8_t> payload_of(std::size_t n) {
    std::vector<std::uint8_t> d(n);
    for (std::size_t i = 0; i < n; ++i) d[i] = static_cast<std::uint8_t>(0x30 + (i * 5) % 0x50);
    return d;
}

// One frame at the chip rate over a noise floor of its own. With 
// left at zero the floor follows the signal, so the pair can be played louder
// or softer at one SNR; given a value it stands still while the signal moves,
// which is a link getting weaker rather than the volume changing.
std::vector<cf> frame_at(const LoraParams& params, float amplitude, float noise) {
    auto modem = make_modem(params);
    auto iq = modem->encode(std::span<const std::uint8_t>(payload_of(24).data(), 24));

    std::vector<cf> out;
    out.reserve(iq.size() + 4096);
    std::uint32_t lcg = 0x2545F491u;
    auto uniform = [&lcg] {
        lcg = lcg * 1664525u + 1013904223u;
        return (static_cast<double>(lcg >> 8) + 0.5) / 16777216.0;
    };
    if (noise <= 0.0f) noise = 0.01f * amplitude;
    for (auto s : iq) {
        const double r = std::sqrt(-2.0 * std::log(uniform()));
        const double t = kTwoPi * uniform();
        out.push_back(s * amplitude +
                      cf(static_cast<float>(r * std::cos(t)),
                         static_cast<float>(r * std::sin(t))) * noise);
    }
    out.resize(out.size() + 4096, cf{0.0f, 0.0f});
    return out;
}

// Pulls a "name=<number>" out of a line, whatever suffix follows it.
std::optional<double> field(const std::string& line, const std::string& name) {
    const auto at = line.find(name + "=");
    if (at == std::string::npos) return std::nullopt;
    return std::stod(line.substr(at + name.size() + 1));
}

// The first preamble line a chain reports for one frame played at `amplitude`.
std::optional<std::string> preamble_for(const LoraParams& params, float amplitude,
                                        float noise = 0.0f) {
    const RxListenerChain::Member m{0, params};
    RxListenerChain chain(params.bandwidth_hz * 4u, 0, params.bandwidth_hz,
                          std::span<const RxListenerChain::Member>(&m, 1));

    std::optional<std::string> first;
    chain.set_event_callback([&first](int, std::string msg) {
        if (!first && msg.find("preamble") != std::string::npos) first = msg;
    });

    const auto capture = frame_at(params, amplitude, noise);
    constexpr std::size_t kBlock = 4096;
    for (std::size_t i = 0; i < capture.size(); i += kBlock) {
        const std::size_t n = std::min(kBlock, capture.size() - i);
        chain.process(std::span<const cf>(capture.data() + i, n));
    }
    return first;
}

} // namespace

// The correction is exactly the spreading gain, so a preset's SNR no longer
// reads better than another's for no reason but its spreading factor.
TEST(SignalReporting, SnrIsThePeakLessTheSpreadingGain) {
    for (const auto preset : {Preset::ShortFast, Preset::MediumFast, Preset::LongFast}) {
        const auto params = params_for(preset);
        const auto line = preamble_for(params, 1.0f);
        ASSERT_TRUE(line.has_value()) << "no preamble for SF" << int(params.spreading_factor);

        const auto peak = field(*line, "peak");
        const auto snr  = field(*line, "snr");
        ASSERT_TRUE(peak.has_value() && snr.has_value()) << *line;

        const double gain = 10.0 * std::log10(double(1u << params.spreading_factor));
        EXPECT_NEAR(*peak - *snr, gain, 0.15)
            << "SF" << int(params.spreading_factor) << ": " << *line;
    }
}

// A despread peak is always strongly positive; a chip-level SNR at a
// realistic link is not. Reporting the first as the second is what made every
// node look like it was shouting.
TEST(SignalReporting, TheReportedSnrIsFarBelowTheDespreadPeak) {
    const auto params = params_for(Preset::LongFast);
    const auto line = preamble_for(params, 1.0f);
    ASSERT_TRUE(line.has_value());

    const auto peak = field(*line, "peak");
    const auto snr  = field(*line, "snr");
    ASSERT_TRUE(peak.has_value() && snr.has_value());
    EXPECT_LT(*snr, *peak - 30.0) << *line;
}

// The level is on the line, in dBFS, and it follows the signal rather than
// standing still -- which is what it did when it was read off the receiver
// after the frame had ended.
TEST(SignalReporting, TheLevelIsOnTheLineAndTracksTheSignal) {
    const auto params = params_for(Preset::MediumFast);

    const auto loud  = preamble_for(params, 1.0f);
    const auto quiet = preamble_for(params, 0.1f);
    ASSERT_TRUE(loud.has_value() && quiet.has_value());

    EXPECT_NE(loud->find("dBFS"), std::string::npos) << *loud;
    EXPECT_EQ(loud->find("dBm"), std::string::npos) << *loud;

    const auto loud_rssi  = field(*loud, "rssi");
    const auto quiet_rssi = field(*quiet, "rssi");
    ASSERT_TRUE(loud_rssi.has_value() && quiet_rssi.has_value());

    // A tenth of the amplitude is a hundredth of the power: 20 dB down.
    EXPECT_NEAR(*loud_rssi - *quiet_rssi, 20.0, 2.0)
        << *loud << " / " << *quiet;
}

// And the SNR does not move with it. Playing the same link louder does not
// make it a better link, which is the whole difference between a level and a
// ratio.
TEST(SignalReporting, TheSnrDoesNotFollowTheVolume) {
    const auto params = params_for(Preset::MediumFast);

    const auto loud  = preamble_for(params, 1.0f);
    const auto quiet = preamble_for(params, 0.1f);
    ASSERT_TRUE(loud.has_value() && quiet.has_value());

    const auto loud_snr  = field(*loud, "snr");
    const auto quiet_snr = field(*quiet, "snr");
    ASSERT_TRUE(loud_snr.has_value() && quiet_snr.has_value());
    EXPECT_NEAR(*loud_snr, *quiet_snr, 3.0) << *loud << " / " << *quiet;
}

// The one that matters for a real mesh.
//
// LoRa is decodable well below the noise floor, so for a distant node almost
// everything in the channel is noise. Reporting the channel's level reports
// that noise and calls it the node's signal strength -- and every weak node
// then reads the same number, which is what a broken measurement looks like
// from the outside. Holding the noise still and moving the signal is the test
// that tells the two apart: the level has to follow the signal, not the floor
// it is sitting on.
TEST(SignalReporting, AWeakSignalIsToldApartFromTheNoiseItSitsIn) {
    const auto params = params_for(Preset::LongFast);
    constexpr float kNoise = 0.05f;

    const auto stronger = preamble_for(params, 0.20f, kNoise);
    const auto weaker   = preamble_for(params, 0.02f, kNoise);
    ASSERT_TRUE(stronger.has_value()) << "no preamble for the stronger signal";
    ASSERT_TRUE(weaker.has_value()) << "no preamble for the weaker signal";

    const auto strong_rssi = field(*stronger, "rssi");
    const auto weak_rssi   = field(*weaker, "rssi");
    ASSERT_TRUE(strong_rssi.has_value() && weak_rssi.has_value());

    // A tenth of the amplitude is 20 dB less signal. The channel these were
    // heard in barely moved -- the noise in it is the same and dominates both
    // blocks -- so anything reporting the channel would report them alike.
    EXPECT_NEAR(*strong_rssi - *weak_rssi, 20.0, 5.0)
        << *stronger << " / " << *weaker;

    // And both sit below full scale, as any real level must.
    EXPECT_LT(*strong_rssi, 0.0) << *stronger;
    EXPECT_LT(*weak_rssi, *strong_rssi) << *weaker;
}
