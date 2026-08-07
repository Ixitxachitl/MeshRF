// SPDX-License-Identifier: GPL-3.0-or-later
//
// Deterministic end-to-end synthesis test for MeshtasticRx.
//
// We synthesize a *clean* LoRa SF9/BW250k frame (preamble + sync word + 2.25
// SFD downchirps + an explicit header encoding a known length/CR/CRC) at a
// configurable oversampling factor, push it through MeshtasticRx, and assert
// the header decodes. This reproduces the os=1 vs os=4 behaviour offline,
// turning the live-signal regression into a fixturized, debuggable case.

#include "mrf/modem/MeshtasticRx.h"
#include "mrf/modem/LoraDecoder.h"
#include "mrf/dsp/Fir.h"
#include "mrf/dsp/Resampler.h"
#include "mrf/dsp/DcBlocker.h"

#include <gtest/gtest.h>

#include <algorithm>
#include <cmath>
#include <complex>
#include <cstdio>
#include <cstdlib>
#include <numbers>
#include <span>
#include <string>
#include <vector>

using namespace mrf::modem;
using namespace mrf::modem::lora;

namespace {

using cf = std::complex<float>;
constexpr double kPi = std::numbers::pi;

// ---- TX-encode helpers (mirror tests/native/test_lora_decoder.cpp) --------

std::uint8_t hamming_encode_test(std::uint8_t nib, std::uint8_t cr) {
    const std::uint8_t d0 = nib & 1, d1 = (nib >> 1) & 1, d2 = (nib >> 2) & 1, d3 = (nib >> 3) & 1;
    const std::uint8_t p1 = d3 ^ d2 ^ d1;
    const std::uint8_t p2 = d3 ^ d2 ^ d0;
    const std::uint8_t p3 = d3 ^ d1 ^ d0;
    const std::uint8_t p4 = d2 ^ d1 ^ d0;
    std::uint8_t cw = static_cast<std::uint8_t>(
        (p1 << 7) | (p2 << 6) | (p3 << 5) | (p4 << 4) |
        (d3 << 3) | (d2 << 2) | (d1 << 1) | d0);
    if (cr == 1) cw &= 0x1F;
    else if (cr == 2) cw &= 0x3F;
    else if (cr == 3) cw &= 0x7F;
    return cw;
}

// Inverse of `deinterleave` (LoRa-SDR diagonalInterleaveSx):
//   symbols[k] bit m = codewords[(m + k) mod PPM] bit k
std::vector<std::uint16_t> interleave_test(std::span<const std::uint8_t> codewords,
                                           std::uint8_t sf_app, std::uint8_t cr_app) {
    std::vector<std::uint16_t> syms(cr_app, 0);
    for (std::uint8_t k = 0; k < cr_app; ++k) {
        for (std::uint8_t m = 0; m < sf_app; ++m) {
            const std::uint8_t i = static_cast<std::uint8_t>((m + k) % sf_app);
            const std::uint8_t bit = static_cast<std::uint8_t>((codewords[i] >> k) & 1u);
            syms[k] = static_cast<std::uint16_t>(syms[k] | (bit << m));
        }
    }
    return syms;
}

// Inverse of `symbol_to_bits` (ldro/spread=4): pick canonical v = reduced * 4.
std::uint16_t bits_to_symbol_test(std::uint16_t bits, std::uint8_t sf) {
    const int N = 1 << sf;
    const std::uint16_t reduced = from_gray(bits);
    return static_cast<std::uint16_t>((static_cast<int>(reduced) << 2) % N);
}

// Build the 8 raw header FFT bins for a given length/cr/crc at SF.
std::vector<std::uint16_t> make_header_bins(std::uint8_t sf, std::uint8_t length,
                                            std::uint8_t cr, bool crc) {
    const std::uint8_t sf_app = static_cast<std::uint8_t>(sf - 2);
    const std::uint8_t cr_app = 8;
    const std::uint8_t fec = static_cast<std::uint8_t>((cr << 1) | (crc ? 1 : 0));
    const std::uint8_t chk = header_crc5(length, fec);

    std::vector<std::uint8_t> nibbles(sf_app, 0);
    nibbles[0] = static_cast<std::uint8_t>((length >> 4) & 0x0F);
    nibbles[1] = static_cast<std::uint8_t>(length & 0x0F);
    nibbles[2] = static_cast<std::uint8_t>(fec & 0x0F);
    nibbles[3] = static_cast<std::uint8_t>((chk >> 4) & 0x0F);
    nibbles[4] = static_cast<std::uint8_t>(chk & 0x0F);
    // nibbles[5..] remain 0.

    std::vector<std::uint8_t> codewords(sf_app);
    for (std::size_t i = 0; i < sf_app; ++i)
        codewords[i] = hamming_encode_test(nibbles[i], 4);

    auto sym_bits = interleave_test(
        std::span<const std::uint8_t>(codewords.data(), codewords.size()), sf_app, cr_app);

    std::vector<std::uint16_t> bins(cr_app);
    for (std::size_t i = 0; i < cr_app; ++i)
        bins[i] = bits_to_symbol_test(sym_bits[i], sf);
    return bins;
}

// ---- Chirp synthesis ------------------------------------------------------

// Generate one LoRa symbol at oversampling `os`, length N*os samples.
// `v` is the symbol value (FFT bin) for upchirps; `down` flips the slope.
// A single-fold continuous-phase chirp matching a real LoRa TX symbol.
std::vector<cf> gen_symbol(int N, int os, double v, bool down) {
    std::vector<cf> out(static_cast<std::size_t>(N) * os);
    const double slope = down ? -1.0 : 1.0;
    double phase = 0.0;
    for (int m = 0; m < N * os; ++m) {
        out[static_cast<std::size_t>(m)] =
            cf{static_cast<float>(std::cos(phase)), static_cast<float>(std::sin(phase))};
        const double t = static_cast<double>(m) / os;       // chip time
        double ph = std::fmod(v + slope * t, static_cast<double>(N));
        if (ph < 0) ph += N;
        const double f = (ph - N / 2.0) / N;                 // cycles per chip
        phase += 2.0 * kPi * f / os;
    }
    return out;
}

struct DecodeResult {
    bool         header_fired = false;
    bool         parity_ok = false;
    std::uint8_t length = 0;
    std::uint8_t cr = 0;
    bool         has_crc = false;
    std::uint16_t raw_symbols[8] = {};
    int          k_hat = 0;
    int          cfo_int = 0;
    int          down_val = 0;
};

// Impairments applied to the synthesized stream to model a real capture.
struct Impairment {
    double cfo_hz = 0.0;       // carrier-frequency offset
    int    lead_samples = 0;   // integer timing offset (frame not at index 0)
    double sto_frac = 0.0;     // fractional-sample timing offset (chips)
};

DecodeResult run_frame(int os, const Impairment& imp = {}) {
    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    const int N = 1 << SF;

    const std::uint8_t kLen = 20, kCr = 1;
    const bool kCrc = true;
    const auto bins = make_header_bins(SF, kLen, kCr, kCrc);

    std::vector<cf> stream;
    auto append = [&](const std::vector<cf>& s) {
        stream.insert(stream.end(), s.begin(), s.end());
    };

    // Optional fractional-sample timing offset: regenerate symbols sampled at
    // (m + sto_frac*os)/os. We fold it into gen_symbol by phase continuity is
    // hard, so instead we just shift the whole stream by interpolation below.

    // Leading samples (integer STO): low-level noise floor before the frame.
    for (int i = 0; i < imp.lead_samples; ++i) stream.push_back(cf{0.0f, 0.0f});

    // Preamble: 10 base upchirps.
    const auto up0 = gen_symbol(N, os, 0.0, false);
    for (int i = 0; i < 10; ++i) append(up0);

    // Sync word 0x2B -> netid bins {0x2*8, 0xB*8} = {16, 88}.
    append(gen_symbol(N, os, 16.0, false));
    append(gen_symbol(N, os, 88.0, false));

    // SFD: 2.25 downchirps.
    const auto down0 = gen_symbol(N, os, 0.0, true);
    append(down0);
    append(down0);
    stream.insert(stream.end(), down0.begin(),
                  down0.begin() + (static_cast<std::ptrdiff_t>(N) * os) / 4);

    // Header: 8 data symbols.
    for (auto b : bins) append(gen_symbol(N, os, static_cast<double>(b), false));

    // Payload padding so the sync state machine always has >= 3 symbols ahead.
    for (int i = 0; i < 40; ++i) append(up0);

    // Apply CFO across the whole stream: multiply by exp(j*2*pi*f*n/fs).
    if (imp.cfo_hz != 0.0) {
        const double fs = static_cast<double>(BW) * os;
        for (std::size_t n = 0; n < stream.size(); ++n) {
            const double ph = 2.0 * kPi * imp.cfo_hz * static_cast<double>(n) / fs;
            const cf rot{static_cast<float>(std::cos(ph)), static_cast<float>(std::sin(ph))};
            stream[n] *= rot;
        }
    }

    MeshtasticRx rx(SF, BW, os, /*sync_word*/ 0x2B);
    DecodeResult res{};
    rx.set_header_callback([&](const HeaderEvent& ev) {
        if (res.header_fired) return;       // capture first header only
        res.header_fired = true;
        res.parity_ok = ev.parity_ok;
        res.length = ev.payload_length;
        res.cr = ev.coding_rate;
        res.has_crc = ev.has_crc;
        for (int i = 0; i < 8; ++i) res.raw_symbols[i] = ev.raw_symbols[i];
    });

    rx.process({stream.data(), stream.size()});
    return res;
}

void report(const char* tag, int os, const DecodeResult& r,
            const std::vector<std::uint16_t>& expect_bins) {
    std::printf("[%s os=%d] fired=%d parity=%d len=%u cr=%u crc=%d\n",
                tag, os, r.header_fired, r.parity_ok, r.length, r.cr, r.has_crc);
    std::printf("  expect bins:");
    for (auto b : expect_bins) std::printf(" %u", b);
    std::printf("\n  got    bins:");
    for (int i = 0; i < 8; ++i) std::printf(" %u", r.raw_symbols[i]);
    std::printf("\n");
}

} // namespace

TEST(MeshtasticRxFrame, HeaderDecodesOs2) {
    const auto expect = make_header_bins(9, 20, 1, true);
    const auto r = run_frame(2);
    report("os2", 2, r, expect);
    ASSERT_TRUE(r.header_fired) << "no header event";
    EXPECT_TRUE(r.parity_ok);
    EXPECT_EQ(r.length, 20u);
    EXPECT_EQ(r.cr, 1u);
    EXPECT_TRUE(r.has_crc);
}

TEST(MeshtasticRxFrame, HeaderDecodesOs4) {
    const auto expect = make_header_bins(9, 20, 1, true);
    const auto r = run_frame(4);
    report("os4", 4, r, expect);
    ASSERT_TRUE(r.header_fired) << "no header event";
    EXPECT_TRUE(r.parity_ok);
    EXPECT_EQ(r.length, 20u);
    EXPECT_EQ(r.cr, 1u);
    EXPECT_TRUE(r.has_crc);
}

// Diagnostic CFO sweep at os=4. A real capture at 913.125 MHz carries a
// carrier-frequency offset; the live preamble peak landed at +/- 50..115 kHz.
// This sweeps CFO and prints whether the header still decodes, to localize
// the regression (clean os=4 already decodes exactly).
TEST(MeshtasticRxFrame, CfoSweepOs4) {
    const double bin_hz = 250000.0 / 512.0; // ~488.28 Hz per FFT bin
    const double cfos[] = {
        0.0, 50.0, 120.0, 200.0, 244.0,           // sub-bin
        bin_hz, 2 * bin_hz, 5 * bin_hz,            // small integer
        bin_hz + 200.0, 5 * bin_hz + 244.0,        // integer + fraction
        20 * bin_hz, 60 * bin_hz, 100 * bin_hz,    // large (live-like)
        100 * bin_hz + 244.0,                      // large + half-bin
        -bin_hz, -5 * bin_hz - 200.0, -60 * bin_hz // negative
    };
    int n_ok = 0, n_total = 0;
    for (double cfo : cfos) {
        Impairment imp; imp.cfo_hz = cfo;
        const auto r = run_frame(4, imp);
        const bool ok = r.header_fired && r.parity_ok &&
                        r.length == 20u && r.cr == 1u && r.has_crc;
        ++n_total; n_ok += ok ? 1 : 0;
        std::printf("[cfo %+9.1f Hz = %+8.3f bins] fired=%d parity=%d len=%u cr=%u -> %s\n",
                    cfo, cfo / bin_hz, r.header_fired, r.parity_ok, r.length, r.cr,
                    ok ? "OK" : "FAIL");
    }
    std::printf("CFO sweep: %d/%d decoded\n", n_ok, n_total);
    // This is a diagnostic sweep (not every impairment level is expected to
    // decode), but it must never regress to zero: cfos[0] == 0.0 is the exact
    // same clean signal HeaderDecodesOs4 asserts decodes correctly, so at
    // least that case — and in practice most of the small-offset cases —
    // must still succeed.
    EXPECT_GT(n_ok, 0) << "CFO sweep: none of " << n_total
                        << " cases decoded (including cfo=0) — likely a real regression";
}

// Full radio-path reproduction: synthesize the frame at the HackRF rate
// (4 MS/s = BW * 16 for BW=250k), push it through the SAME FirDecimator the
// live pipeline uses (4 MHz -> 1 MHz, decimate by 4), then feed the modem at
// os=4. This is the decisive test: if the clean synthetic decodes at os=4 but
// FAILS through the decimator, the regression is in the resampler.
TEST(MeshtasticRxFrame, RadioPathDecimatedOs4) {
    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    constexpr int kModemOs = 4;
    constexpr int kRadioMul = 4;            // 4 MHz / 1 MHz
    const int gen_os = kModemOs * kRadioMul; // 16 -> 4 MHz at BW=250k
    const int N = 1 << SF;

    const auto expect = make_header_bins(SF, 20, 1, true);

    std::vector<cf> stream;
    auto append_sym = [&](double v, bool down, bool quarter = false) {
        auto s = gen_symbol(N, gen_os, v, down);
        const std::size_t count = quarter ? s.size() / 4 : s.size();
        stream.insert(stream.end(), s.begin(), s.begin() + static_cast<std::ptrdiff_t>(count));
    };

    for (int i = 0; i < 10; ++i) append_sym(0.0, false);  // preamble
    append_sym(16.0, false);                              // netid1
    append_sym(88.0, false);                              // netid2
    append_sym(0.0, true);                                // SFD down 1
    append_sym(0.0, true);                                // SFD down 2
    append_sym(0.0, true, /*quarter*/ true);              // 0.25 down
    for (auto b : expect) append_sym(static_cast<double>(b), false); // header
    for (int i = 0; i < 40; ++i) append_sym(0.0, false);  // payload pad

    // Decimate 4 MHz -> 1 MHz through the production FIR decimator.
    mrf::dsp::FirDecimator decim(4'000'000u, 1'000'000u);
    auto deci = decim.process(std::span<const cf>(stream.data(), stream.size()));
    std::vector<cf> modem_in(deci.begin(), deci.end());

    MeshtasticRx rx(SF, BW, kModemOs, /*sync_word*/ 0x2B);
    DecodeResult res{};
    rx.set_header_callback([&](const HeaderEvent& ev) {
        if (res.header_fired) return;
        res.header_fired = true;
        res.parity_ok = ev.parity_ok;
        res.length = ev.payload_length;
        res.cr = ev.coding_rate;
        res.has_crc = ev.has_crc;
        for (int i = 0; i < 8; ++i) res.raw_symbols[i] = ev.raw_symbols[i];
    });
    rx.process({modem_in.data(), modem_in.size()});

    report("radio", kModemOs, res, expect);
    ASSERT_TRUE(res.header_fired) << "no header event through decimator";
    EXPECT_TRUE(res.parity_ok);
    EXPECT_EQ(res.length, 20u);
    EXPECT_EQ(res.cr, 1u);
    EXPECT_TRUE(res.has_crc);
}

// Full production front-end: synthesize at 4 MS/s, apply DC offset + DC
// blocker + CFO + noise like the live capture chain, decimate, decode.
TEST(MeshtasticRxFrame, FullFrontEndOs4) {
    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    constexpr int kModemOs = 4;
    const int gen_os = kModemOs * 4;        // 16 -> 4 MHz
    const int N = 1 << SF;
    const double fs = static_cast<double>(BW) * gen_os; // 4 MHz

    const auto expect = make_header_bins(SF, 20, 1, true);

    auto build = [&](double cfo_hz, double dc_offset, float noise, bool dc_block) {
        std::vector<cf> stream;
        auto append_sym = [&](double v, bool down, bool quarter = false) {
            auto s = gen_symbol(N, gen_os, v, down);
            const std::size_t count = quarter ? s.size() / 4 : s.size();
            stream.insert(stream.end(), s.begin(),
                          s.begin() + static_cast<std::ptrdiff_t>(count));
        };
        for (int i = 0; i < N * gen_os * 3; ++i) stream.push_back(cf{0.0f, 0.0f});
        for (int i = 0; i < 10; ++i) append_sym(0.0, false);
        append_sym(16.0, false);
        append_sym(88.0, false);
        append_sym(0.0, true);
        append_sym(0.0, true);
        append_sym(0.0, true, true);
        for (auto b : expect) append_sym(static_cast<double>(b), false);
        for (int i = 0; i < 40; ++i) append_sym(0.0, false);

        if (cfo_hz != 0.0) {
            for (std::size_t n = 0; n < stream.size(); ++n) {
                const double ph = 2.0 * kPi * cfo_hz * static_cast<double>(n) / fs;
                stream[n] *= cf{static_cast<float>(std::cos(ph)),
                                static_cast<float>(std::sin(ph))};
            }
        }
        if (dc_offset != 0.0)
            for (auto& s : stream) s += cf{static_cast<float>(dc_offset), 0.0f};
        if (noise > 0.0f) {
            std::uint32_t rng = 0x12345678u;
            auto rnd = [&]() {
                rng = rng * 1664525u + 1013904223u;
                return (static_cast<float>(rng >> 8) / 16777216.0f - 0.5f) * 2.0f;
            };
            for (auto& s : stream) s += cf{noise * rnd(), noise * rnd()};
        }
        if (dc_block) {
            mrf::dsp::DcBlocker dcb;
            dcb.process(std::span<cf>(stream.data(), stream.size()));
        }
        return stream;
    };

    auto decode = [&](std::vector<cf>& stream) {
        mrf::dsp::FirDecimator decim(4'000'000u, 1'000'000u);
        auto deci = decim.process(std::span<const cf>(stream.data(), stream.size()));
        std::vector<cf> modem_in(deci.begin(), deci.end());
        MeshtasticRx rx(SF, BW, kModemOs, 0x2B);
        DecodeResult res{};
        rx.set_header_callback([&](const HeaderEvent& ev) {
            if (res.header_fired) return;
            res.header_fired = true;
            res.parity_ok = ev.parity_ok;
            res.length = ev.payload_length;
            res.cr = ev.coding_rate;
            res.has_crc = ev.has_crc;
            for (int i = 0; i < 8; ++i) res.raw_symbols[i] = ev.raw_symbols[i];
        });
        rx.process({modem_in.data(), modem_in.size()});
        return res;
    };

    struct Case { double cfo; double dc; float noise; bool dcb; const char* name; };
    const Case cases[] = {
        {0.0,       0.0, 0.0f,  false, "baseline"},
        {0.0,       0.0, 0.0f,  true,  "dcblock"},
        {0.0,       0.3, 0.0f,  true,  "dcoffset+dcblock"},
        {95'000.0,  0.0, 0.0f,  true,  "cfo95k+dcblock"},
        {95'000.0,  0.3, 0.05f, true,  "cfo95k+dc+noise"},
        {58'000.0,  0.3, 0.05f, true,  "cfo58k+dc+noise"},
        {-114'000.0,0.3, 0.05f, true,  "cfo-114k+dc+noise"},
    };
    int baseline_ok = 0;
    for (const auto& c : cases) {
        auto s = build(c.cfo, c.dc, c.noise, c.dcb);
        auto r = decode(s);
        const bool ok = r.header_fired && r.parity_ok && r.length == 20u &&
                        r.cr == 1u && r.has_crc;
        if (std::string(c.name) == "baseline") baseline_ok = ok;
        std::printf("[%-20s cfo=%+8.0f] fired=%d parity=%d len=%u cr=%u -> %s | got:",
                    c.name, c.cfo, r.header_fired, r.parity_ok, r.length, r.cr,
                    ok ? "OK" : "FAIL");
        for (int i = 0; i < 8; ++i) std::printf(" %u", r.raw_symbols[i]);
        std::printf("\n");
    }
    EXPECT_TRUE(baseline_ok);
}

// Timing-offset (STO) sweep — reproduces the ACTUAL live condition: the live
// frames showed a large k_hat (preamble bin ~195) but a SMALL down_val (2..46
// => cfo_int 1..23, in range, no alias). That large k_hat comes from the frame
// arriving at an arbitrary sample offset (timing), not frequency. Sweep the
// frame's start offset at 4 MS/s and check whether the header still decodes,
// printing k_hat / cfo_int / down_val to see where it breaks.
TEST(MeshtasticRxFrame, TimingOffsetSweepOs4) {
    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    constexpr int kModemOs = 4;
    const int gen_os = kModemOs * 4;        // 16 -> 4 MHz
    const int N = 1 << SF;

    const auto expect = make_header_bins(SF, 20, 1, true);

    auto build = [&](int lead_4mhz) {
        std::vector<cf> stream;
        auto append_sym = [&](double v, bool down, bool quarter = false) {
            auto s = gen_symbol(N, gen_os, v, down);
            const std::size_t count = quarter ? s.size() / 4 : s.size();
            stream.insert(stream.end(), s.begin(),
                          s.begin() + static_cast<std::ptrdiff_t>(count));
        };
        for (int i = 0; i < lead_4mhz; ++i) stream.push_back(cf{0.0f, 0.0f});
        for (int i = 0; i < 12; ++i) append_sym(0.0, false);
        append_sym(16.0, false);
        append_sym(88.0, false);
        append_sym(0.0, true);
        append_sym(0.0, true);
        append_sym(0.0, true, true);
        for (auto b : expect) append_sym(static_cast<double>(b), false);
        for (int i = 0; i < 40; ++i) append_sym(0.0, false);
        return stream;
    };
    auto decode = [&](std::vector<cf>& stream) {
        mrf::dsp::FirDecimator decim(4'000'000u, 1'000'000u);
        auto deci = decim.process(std::span<const cf>(stream.data(), stream.size()));
        std::vector<cf> modem_in(deci.begin(), deci.end());
        MeshtasticRx rx(SF, BW, kModemOs, 0x2B);
        DecodeResult res{};
        rx.set_header_callback([&](const HeaderEvent& ev) {
            if (res.header_fired) return;
            res.header_fired = true;
            res.parity_ok = ev.parity_ok;
            res.length = ev.payload_length;
            res.cr = ev.coding_rate;
            res.has_crc = ev.has_crc;
            res.k_hat = ev.k_hat;
            res.cfo_int = ev.cfo_int;
            res.down_val = ev.down_val;
            for (int i = 0; i < 8; ++i) res.raw_symbols[i] = ev.raw_symbols[i];
        });
        rx.process({modem_in.data(), modem_in.size()});
        return res;
    };

    int n_ok = 0, n_total = 0;
    // Sweep a full symbol period at 4 MS/s in steps to cover all timing phases.
    for (int lead = 0; lead < N * gen_os; lead += (N * gen_os) / 32) {
        auto s = build(lead);
        auto r = decode(s);
        const bool ok = r.header_fired && r.parity_ok && r.length == 20u &&
                        r.cr == 1u && r.has_crc;
        ++n_total; n_ok += ok ? 1 : 0;
        std::printf("[lead %5d] fired=%d parity=%d k_hat=%4d cfo_int=%4d dv=%4d len=%u cr=%u -> %s | got:",
                    lead, r.header_fired, r.parity_ok, r.k_hat, r.cfo_int,
                    r.down_val, r.length, r.cr, ok ? "OK" : "FAIL");
        for (int i = 0; i < 8; ++i) std::printf(" %u", r.raw_symbols[i]);
        std::printf("\n");
    }
    std::printf("Timing sweep: %d/%d decoded\n", n_ok, n_total);
    // lead=0 is the exact construction RadioPathDecimatedOs4 already asserts
    // decodes, so this sweep must never regress to zero decodes.
    EXPECT_GT(n_ok, 0) << "Timing sweep: none of " << n_total
                        << " lead offsets decoded (including lead=0) — likely a real regression";
}

// Combined CFO + timing sweep — the TRUE live condition. Live frames had BOTH
// a moderate real CFO (cfo_int 1..23 from dv 2..46) AND a large timing offset
// (k_hat 119..277). Neither alone reproduces the failure. Inject CFO ~9.8 kHz
// (=20 bins, matching live frame 4's cfo_int=20) and sweep the frame start.
TEST(MeshtasticRxFrame, CfoPlusTimingSweepOs4) {
    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    constexpr int kModemOs = 4;
    const int gen_os = kModemOs * 4;        // 16 -> 4 MHz
    const int N = 1 << SF;
    const double fs = static_cast<double>(BW) * gen_os; // 4 MHz

    const auto expect = make_header_bins(SF, 20, 1, true);

    auto build = [&](int lead_4mhz, double cfo_hz) {
        std::vector<cf> stream;
        auto append_sym = [&](double v, bool down, bool quarter = false) {
            auto s = gen_symbol(N, gen_os, v, down);
            const std::size_t count = quarter ? s.size() / 4 : s.size();
            stream.insert(stream.end(), s.begin(),
                          s.begin() + static_cast<std::ptrdiff_t>(count));
        };
        for (int i = 0; i < lead_4mhz; ++i) stream.push_back(cf{0.0f, 0.0f});
        for (int i = 0; i < 12; ++i) append_sym(0.0, false);
        append_sym(16.0, false);
        append_sym(88.0, false);
        append_sym(0.0, true);
        append_sym(0.0, true);
        append_sym(0.0, true, true);
        for (auto b : expect) append_sym(static_cast<double>(b), false);
        for (int i = 0; i < 40; ++i) append_sym(0.0, false);
        if (cfo_hz != 0.0) {
            for (std::size_t n = 0; n < stream.size(); ++n) {
                const double ph = 2.0 * kPi * cfo_hz * static_cast<double>(n) / fs;
                stream[n] *= cf{static_cast<float>(std::cos(ph)),
                                static_cast<float>(std::sin(ph))};
            }
        }
        return stream;
    };
    auto decode = [&](std::vector<cf>& stream) {
        mrf::dsp::FirDecimator decim(4'000'000u, 1'000'000u);
        auto deci = decim.process(std::span<const cf>(stream.data(), stream.size()));
        std::vector<cf> modem_in(deci.begin(), deci.end());
        MeshtasticRx rx(SF, BW, kModemOs, 0x2B);
        DecodeResult res{};
        rx.set_header_callback([&](const HeaderEvent& ev) {
            if (res.header_fired) return;
            res.header_fired = true;
            res.parity_ok = ev.parity_ok;
            res.length = ev.payload_length;
            res.cr = ev.coding_rate;
            res.has_crc = ev.has_crc;
            res.k_hat = ev.k_hat;
            res.cfo_int = ev.cfo_int;
            res.down_val = ev.down_val;
            for (int i = 0; i < 8; ++i) res.raw_symbols[i] = ev.raw_symbols[i];
        });
        rx.process({modem_in.data(), modem_in.size()});
        return res;
    };

    const double cfo = 9766.0; // ~20 bins
    int n_ok = 0, n_total = 0;
    for (int lead = 0; lead < N * gen_os; lead += (N * gen_os) / 32) {
        auto s = build(lead, cfo);
        auto r = decode(s);
        const bool ok = r.header_fired && r.parity_ok && r.length == 20u &&
                        r.cr == 1u && r.has_crc;
        ++n_total; n_ok += ok ? 1 : 0;
        std::printf("[lead %5d cfo=%.0f] k_hat=%4d cfo_int=%4d dv=%4d -> %s | got:",
                    lead, cfo, r.k_hat, r.cfo_int, r.down_val, ok ? "OK" : "FAIL");
        for (int i = 0; i < 8; ++i) std::printf(" %u", r.raw_symbols[i]);
        std::printf("\n");
    }
    std::printf("CFO+timing sweep: %d/%d decoded\n", n_ok, n_total);
    // Diagnostic sweep across timing phases at a fixed ~20-bin CFO; must not
    // regress to zero decodes across every phase.
    EXPECT_GT(n_ok, 0) << "CFO+timing sweep: none of " << n_total
                        << " lead offsets decoded — likely a real regression";
}

// Reproduce live packet 1 PRECISELY through the full 4 MHz front end. After
// offset tuning centered the channel the live frame showed: small integer CFO
// (cfo_int=2) but a LARGE fractional CFO (cf=-0.452) and fractional STO
// (sf=0.299), k_hat~6, and still header[BAD]. Sweep fractional CFO across
// [-0.5,+0.5] at small integer CFO with a fine timing offset to find where the
// fractional-CFO + fractional-STO interaction scrambles the header symbols.
TEST(MeshtasticRxFrame, FractionalCfoStoSweepOs4) {
    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    constexpr int kModemOs = 4;
    const int gen_os = kModemOs * 4;        // 16 -> 4 MHz
    const int N = 1 << SF;
    const double fs = static_cast<double>(BW) * gen_os; // 4 MHz
    const double bin_hz = static_cast<double>(BW) / N;   // 488.28 Hz/bin

    const auto expect = make_header_bins(SF, 20, 1, true);

    auto build = [&](int lead_4mhz, double cfo_bins) {
        std::vector<cf> stream;
        auto append_sym = [&](double v, bool down, bool quarter = false) {
            auto s = gen_symbol(N, gen_os, v, down);
            const std::size_t count = quarter ? s.size() / 4 : s.size();
            stream.insert(stream.end(), s.begin(),
                          s.begin() + static_cast<std::ptrdiff_t>(count));
        };
        for (int i = 0; i < lead_4mhz; ++i) stream.push_back(cf{0.0f, 0.0f});
        for (int i = 0; i < 12; ++i) append_sym(0.0, false);
        append_sym(16.0, false);
        append_sym(88.0, false);
        append_sym(0.0, true);
        append_sym(0.0, true);
        append_sym(0.0, true, true);
        for (auto b : expect) append_sym(static_cast<double>(b), false);
        for (int i = 0; i < 40; ++i) append_sym(0.0, false);
        const double cfo_hz = cfo_bins * bin_hz;
        for (std::size_t n = 0; n < stream.size(); ++n) {
            const double ph = 2.0 * kPi * cfo_hz * static_cast<double>(n) / fs;
            stream[n] *= cf{static_cast<float>(std::cos(ph)),
                            static_cast<float>(std::sin(ph))};
        }
        return stream;
    };
    auto decode = [&](std::vector<cf>& stream) {
        mrf::dsp::FirDecimator decim(4'000'000u, 1'000'000u);
        auto deci = decim.process(std::span<const cf>(stream.data(), stream.size()));
        std::vector<cf> modem_in(deci.begin(), deci.end());
        MeshtasticRx rx(SF, BW, kModemOs, 0x2B);
        DecodeResult res{};
        rx.set_header_callback([&](const HeaderEvent& ev) {
            if (res.header_fired) return;
            res.header_fired = true;
            res.parity_ok = ev.parity_ok;
            res.length = ev.payload_length;
            res.cr = ev.coding_rate;
            res.has_crc = ev.has_crc;
            res.k_hat = ev.k_hat;
            res.cfo_int = ev.cfo_int;
            res.down_val = ev.down_val;
            for (int i = 0; i < 8; ++i) res.raw_symbols[i] = ev.raw_symbols[i];
        });
        rx.process({modem_in.data(), modem_in.size()});
        return res;
    };

    int n_ok = 0, n_total = 0;
    // Small integer CFO=2 plus fractional sweep, with a few fine timing phases.
    const int leads[] = {0, 4, 8, 16, 24};
    for (double frac = -0.49; frac <= 0.49; frac += 0.1) {
        for (int lead : leads) {
            const double cfo_bins = 2.0 + frac;
            auto s = build(lead, cfo_bins);
            auto r = decode(s);
            const bool ok = r.header_fired && r.parity_ok && r.length == 20u &&
                            r.cr == 1u && r.has_crc;
            ++n_total; n_ok += ok ? 1 : 0;
            std::printf("[cfo=2%+.2f lead=%2d] k_hat=%4d cfo_int=%4d dv=%4d -> %s | got:",
                        frac, lead, r.k_hat, r.cfo_int, r.down_val, ok ? "OK" : "FAIL");
            for (int i = 0; i < 8; ++i) std::printf(" %u", r.raw_symbols[i]);
            std::printf("\n");
        }
    }
    std::printf("Fractional CFO/STO sweep: %d/%d decoded\n", n_ok, n_total);
    // Diagnostic sweep across fractional CFO/STO combinations; must not
    // regress to zero decodes across every combination.
    EXPECT_GT(n_ok, 0) << "Fractional CFO/STO sweep: none of " << n_total
                        << " combinations decoded — likely a real regression";
}

// Offline replay of a real captured modem-input stream. Capture with the app:
//   $env:MRF_IQ_CAPTURE="C:\path\capture.cf32"; dotnet run ...   (then stop RX)
// Replay here:
//   $env:MRF_IQ_REPLAY="C:\path\capture.cf32"
//   mrf_core_tests.exe --gtest_filter=MeshtasticRxFrame.ReplayCapturedIq
// The file is interleaved float32 I/Q at the DEVICE capture rate (2.4 MHz by
// default, matching the live HackRF/SDRangel setup). We resample to the modem
// working rate (1 MHz for SF9/BW250k os=4) through the SAME dsp::Resampler the
// live pipeline uses, then decode. Override the source rate with
// MRF_IQ_REPLAY_RATE if replaying an older 1 MHz capture. Every HeaderEvent
// (good or bad) is printed with full diagnostics.
TEST(MeshtasticRxFrame, ReplayCapturedIq) {
    const char* path = std::getenv("MRF_IQ_REPLAY");
    if (!path || !*path) {
        GTEST_SKIP() << "set MRF_IQ_REPLAY to a .cf32 capture path";
    }
    std::FILE* f = std::fopen(path, "rb");
    ASSERT_NE(f, nullptr) << "cannot open " << path;
    std::fseek(f, 0, SEEK_END);
    const long bytes = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    const std::size_t count = static_cast<std::size_t>(bytes) / sizeof(cf);
    std::vector<cf> iq(count);
    const std::size_t got = std::fread(iq.data(), sizeof(cf), count, f);
    std::fclose(f);

    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    constexpr int kModemOs = 4;
    constexpr std::uint32_t kModemRate = BW * kModemOs; // 1.0 MHz

    // Source (capture) sample rate: 2.4 MHz device rate by default.
    std::uint32_t src_rate = 2'400'000u;
    if (const char* rs = std::getenv("MRF_IQ_REPLAY_RATE"); rs && *rs) {
        src_rate = static_cast<std::uint32_t>(std::strtoul(rs, nullptr, 10));
    }
    std::printf("Replay: %zu samples (%.2f s @%.3f MHz) from %s\n", got,
                static_cast<double>(got) / src_rate, src_rate / 1e6, path);

    // Resample to the modem rate exactly as Core.cpp does for the live stream.
    std::vector<cf> work(iq.begin(), iq.begin() + got);
    if (src_rate != kModemRate) {
        mrf::dsp::Resampler resampler(src_rate, kModemRate);
        auto out = resampler.process({work.data(), work.size()});
        work.assign(out.begin(), out.end());
        std::printf("Replay: resampled %u -> %u Hz -> %zu samples (%.2f s)\n",
                    src_rate, kModemRate, work.size(),
                    static_cast<double>(work.size()) / kModemRate);
    }
    const std::size_t n_samples = work.size();

    MeshtasticRx rx(SF, BW, kModemOs, 0x2B);
    int n_headers = 0, n_ok = 0;
    rx.set_header_callback([&](const HeaderEvent& ev) {
        ++n_headers;
        const bool ok = ev.parity_ok && ev.has_crc && ev.coding_rate >= 1 &&
                        ev.coding_rate <= 4 && ev.payload_length >= 1;
        if (ok) ++n_ok;
        std::printf("[hdr %d] %s len=%u cr=%u crc=%d parity=%d k=%d cfo_int=%d "
                    "cf=%.3f sf=%.3f dv=%d nid=%d,%d delta=%d start=%d | hsym:",
                    n_headers, ok ? "OK " : "BAD", ev.payload_length,
                    ev.coding_rate, ev.has_crc, ev.parity_ok, ev.k_hat,
                    ev.cfo_int, ev.cfo_frac, ev.sto_frac, ev.down_val,
                    ev.net_id0, ev.net_id1,
                    ev.chosen_delta, ev.chosen_start);
        for (int i = 0; i < 8; ++i) std::printf(" %u", ev.raw_symbols[i]);
        std::printf("\n");
    });
    rx.set_payload_callback([&](const PayloadEvent& ev) {
        std::printf("[pay] len=%zu crc_ok=%d rx=%04X calc=%04X psym(%zu):",
                    ev.length, ev.crc_ok, ev.crc_received, ev.crc_computed,
                    ev.raw_symbol_count);
        for (std::size_t i = 0; i < ev.raw_symbol_count; ++i)
            std::printf(" %u", ev.raw_symbols[i]);
        std::printf("\n");
    });
    // Feed in modest chunks to mimic the live streaming cadence.
    constexpr std::size_t kChunk = 8192;
    for (std::size_t off = 0; off < n_samples; off += kChunk) {
        const std::size_t n = std::min(kChunk, n_samples - off);
        rx.process({work.data() + off, n});
    }
    std::printf("Replay: %d headers, %d plausible\n", n_headers, n_ok);
    SUCCEED();
}

// Same captured stream, but decimated 4x (1 MHz -> 250 kHz) and replayed
// through MeshtasticRx at os=1 — the path historically proven to decode this
// OTA signal. If os=1 decodes a frame that os=4 cannot, the bug is isolated to
// the os>1 front end (and we recover the ground-truth payload for comparison).
TEST(MeshtasticRxFrame, ReplayCapturedIqOs1) {
    const char* path = std::getenv("MRF_IQ_REPLAY");
    if (!path || !*path) {
        GTEST_SKIP() << "set MRF_IQ_REPLAY to a .cf32 capture path";
    }
    std::FILE* f = std::fopen(path, "rb");
    ASSERT_NE(f, nullptr) << "cannot open " << path;
    std::fseek(f, 0, SEEK_END);
    const long bytes = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    const std::size_t count = static_cast<std::size_t>(bytes) / sizeof(cf);
    std::vector<cf> iq(count);
    const std::size_t got = std::fread(iq.data(), sizeof(cf), count, f);
    std::fclose(f);

    // Source (capture) sample rate: 2.4 MHz device rate by default.
    std::uint32_t src_rate = 2'400'000u;
    if (const char* rs = std::getenv("MRF_IQ_REPLAY_RATE"); rs && *rs) {
        src_rate = static_cast<std::uint32_t>(std::strtoul(rs, nullptr, 10));
    }
    // Resample straight to the chip rate (250 kHz, os=1) with the production
    // polyphase resampler — the path historically proven to decode this OTA
    // signal. If os=1 decodes a frame os=4 cannot, the bug is in the os>1 front
    // end (and we recover the ground-truth payload for comparison).
    std::vector<cf> dec;
    {
        std::vector<cf> work(iq.begin(), iq.begin() + got);
        mrf::dsp::Resampler resampler(src_rate, 250'000u);
        auto out = resampler.process({work.data(), work.size()});
        dec.assign(out.begin(), out.end());
    }
    std::printf("ReplayOs1: %zu samples @%.3f MHz -> %zu @250kHz (%.2f s)\n",
                got, src_rate / 1e6, dec.size(),
                static_cast<double>(dec.size()) / 250'000.0);

    constexpr std::uint8_t SF = 9;
    constexpr std::uint32_t BW = 250'000;
    MeshtasticRx rx(SF, BW, /*os*/ 1, 0x2B);
    int n_headers = 0, n_ok = 0;
    rx.set_header_callback([&](const HeaderEvent& ev) {
        ++n_headers;
        const bool ok = ev.parity_ok && ev.has_crc && ev.coding_rate >= 1 &&
                        ev.coding_rate <= 4 && ev.payload_length >= 1;
        if (ok) ++n_ok;
        std::printf("[hdr %d] %s len=%u cr=%u crc=%d parity=%d k=%d cfo_int=%d "
                    "cf=%.3f sf=%.3f dv=%d delta=%d start=%d | hsym:",
                    n_headers, ok ? "OK " : "BAD", ev.payload_length,
                    ev.coding_rate, ev.has_crc, ev.parity_ok, ev.k_hat,
                    ev.cfo_int, ev.cfo_frac, ev.sto_frac, ev.down_val,
                    ev.chosen_delta, ev.chosen_start);
        for (int i = 0; i < 8; ++i) std::printf(" %u", ev.raw_symbols[i]);
        std::printf("\n");
    });
    rx.set_payload_callback([&](const PayloadEvent& ev) {
        std::printf("[pay] len=%zu crc_ok=%d rx=%04X calc=%04X psym(%zu):",
                    ev.length, ev.crc_ok, ev.crc_received, ev.crc_computed,
                    ev.raw_symbol_count);
        for (std::size_t i = 0; i < ev.raw_symbol_count; ++i)
            std::printf(" %u", ev.raw_symbols[i]);
        std::printf("\n");
    });
    constexpr std::size_t kChunk = 8192;
    for (std::size_t off = 0; off < dec.size(); off += kChunk) {
        const std::size_t n = std::min(kChunk, dec.size() - off);
        rx.process({dec.data() + off, n});
    }
    std::printf("ReplayOs1: %d headers, %d plausible\n", n_headers, n_ok);
    SUCCEED();
}
