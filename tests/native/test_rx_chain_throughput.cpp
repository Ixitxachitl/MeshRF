// SPDX-License-Identifier: GPL-3.0-or-later
#include <gtest/gtest.h>

#include "mrf/modem/Preset.h"
#include "mrf/modem/RxListenerChain.h"

#include <chrono>
#include <complex>
#include <cstdio>
#include <memory>
#include <random>
#include <span>
#include <thread>
#include <vector>

using namespace mrf::modem;

namespace {

// Noise rather than silence: a chain fed exact zeros takes a different path
// through the demodulator's preamble search, so it would not be timing the
// work a real capture makes it do.
std::vector<std::complex<float>> Noise(std::size_t n) {
    std::mt19937 rng(12345);
    std::normal_distribution<float> g(0.0f, 0.05f);
    std::vector<std::complex<float>> v(n);
    for (auto& s : v) s = {g(rng), g(rng)};
    return v;
}

constexpr std::uint32_t kDeviceRate = 10'000'000u;
constexpr std::size_t kBlock = 262'144;

double RealTimeFactor(RxListenerChain& chain, std::span<const std::complex<float>> block,
                      double seconds) {
    const auto total = static_cast<std::size_t>(kDeviceRate * seconds);
    const auto t0 = std::chrono::steady_clock::now();
    for (std::size_t done = 0; done < total; done += block.size()) chain.process(block);
    const auto t1 = std::chrono::steady_clock::now();
    return seconds / std::chrono::duration<double>(t1 - t0).count();
}

} // namespace

/// <summary>
/// A chain has to demodulate faster than the radio delivers, or the queue in
/// front of it fills and whole blocks are dropped — which is what the app
/// reports as a channel not being demodulated fast enough.
///
/// The floor here is deliberately far below what any machine that can run the
/// app manages, because this is a catastrophe detector rather than a
/// performance target: it exists because a version of the resampler that did a
/// pair of integer divisions per input sample ran at a third of the speed of
/// one that does none, and no correctness test could tell the difference.
/// Nothing in CI runs these tests, so the number is for whoever is working on
/// the DSP; the printed figure is the useful part.
///
/// The cost is almost the same for every preset, and that is not a
/// coincidence: the prototype filter is a fixed number of taps per period of
/// the new Nyquist rate, so a narrower channel needs proportionally more taps
/// and produces proportionally fewer outputs. The two cancel.
/// </summary>
TEST(RxChainThroughput, EachChainOutrunsTheRadio) {
    const auto block = Noise(kBlock);
    struct { const char* name; Preset preset; } cases[] = {
        {"ShortTurbo (500 kHz)", Preset::ShortTurbo},
        {"LongFast (250 kHz)",   Preset::LongFast},
        {"TinySlow (15.6 kHz)",  Preset::TinySlow},
    };

    for (const auto& c : cases) {
        const auto params = params_for(c.preset);
        RxListenerChain::Member member{0, params};
        RxListenerChain chain(kDeviceRate, 1'000'000, params.bandwidth_hz, std::span(&member, 1));

        const double factor = RealTimeFactor(chain, block, 1.0);
        std::printf("  %-22s %5.2fx real time\n", c.name, factor);
        std::fflush(stdout);
        EXPECT_GT(factor, 2.0) << c.name << " cannot keep up with a 10 MS/s capture";
    }
}

/// <summary>
/// The same work in the shape the app makes it: every preset that fits a wide
/// capture, each on its own thread. Printed rather than asserted beyond a bare
/// floor, since how much better than real time this runs is a fact about the
/// machine's cores rather than about the code.
/// </summary>
TEST(RxChainThroughput, EveryPresetAtOnce) {
    const Preset presets[] = {
        Preset::ShortTurbo, Preset::ShortFast, Preset::MediumFast, Preset::LongTurbo,
        Preset::LongFast, Preset::LongModerate, Preset::LongSlow, Preset::LiteFast,
        Preset::NarrowFast, Preset::TinySlow,
    };
    const auto block = Noise(kBlock);
    const double seconds = 2.0;
    const auto total = static_cast<std::size_t>(kDeviceRate * seconds);

    std::vector<std::unique_ptr<RxListenerChain>> chains;
    std::int64_t offset = -2'000'000;
    for (auto preset : presets) {
        const auto params = params_for(preset);
        RxListenerChain::Member member{0, params};
        chains.push_back(std::make_unique<RxListenerChain>(
            kDeviceRate, offset, params.bandwidth_hz, std::span(&member, 1)));
        offset += 400'000;
    }

    const auto t0 = std::chrono::steady_clock::now();
    std::vector<std::thread> threads;
    for (auto& chain : chains)
        threads.emplace_back([&chain, &block, total] {
            for (std::size_t done = 0; done < total; done += block.size()) chain->process(block);
        });
    for (auto& t : threads) t.join();
    const auto t1 = std::chrono::steady_clock::now();

    const double factor = seconds / std::chrono::duration<double>(t1 - t0).count();
    std::printf("  %zu chains on %u hardware threads: %5.2fx real time\n",
                chains.size(), std::thread::hardware_concurrency(), factor);
    std::fflush(stdout);
    EXPECT_GT(factor, 1.0) << "the whole listener set cannot keep up with the radio";
}
