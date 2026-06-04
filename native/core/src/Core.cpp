// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/Core.h"
#include "mrf/dsp/DcBlocker.h"
#include "mrf/dsp/Fft.h"
#include "mrf/dsp/Resampler.h"
#include "mrf/dsp/SignalStats.h"
#include "mrf/dsp/Spectrum.h"
#include "mrf/modem/LoraModem.h"

#include <atomic>
#include <cmath>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <numbers>
#include <string>
#include <vector>

namespace mrf {

namespace {
constexpr std::size_t kSpectrumFftSize = 1024;
constexpr std::size_t kMaxQueuedEvents = 256;
// HackRF One has a strong DC spike + 1/f LO leakage at the tuned frequency.
// We tune the radio +kLoOffsetHz off-channel and digitally mix back so the
// LoRa signal sits at baseband DC while the spike sits at -kLoOffsetHz where
// the decimator's antialias filter rejects it.
constexpr double kLoOffsetHz = 500'000.0;
// Device sample rate used for live RX and raw IQ capture. 2.4 MS/s matches
// SDRangel's HackRF setup (2.4 MHz, decimation 2 -> 1.2 MHz channel) so a raw
// capture is directly comparable to an SDRangel .sdriq recording. The minimum
// legal HackRF rate is 2 MS/s; 2.4 MS/s keeps a clean guard band around the
// 250 kHz LoRa channel after the offset-tuning mix.
constexpr std::uint32_t kDeviceRateHz = 2'400'000u;
}

struct Core::Impl {
    std::unique_ptr<hal::IRadioDevice> radio;
    hal::DeviceKind requested_kind{hal::DeviceKind::Auto};
    std::string device_name{"(none)"};
    std::uint8_t lna_db{24};
    std::uint8_t vga_db{20};
    bool         amp_enable{false};
    std::unique_ptr<modem::ILoraModem> modem;
    std::unique_ptr<dsp::Resampler> resampler;
    dsp::DcBlocker dc_blocker;
    dsp::SignalStats stats;
    std::unique_ptr<dsp::Spectrum> spectrum;
    router::FloodingRouter flooder{};
    std::vector<hal::SampleType> work; // scratch for DC-blocking after decimation
    std::atomic<bool> running{false};
    std::mutex start_mu;
    // Offset-tuning NCO state (mixes LoRa channel from +LoOffset back to 0).
    double mix_phase{0.0};
    double mix_phase_inc{0.0};

    // Optional raw IQ capture of the modem-input (decimated) stream, gated by
    // the MRF_IQ_CAPTURE env var (=output path) or the start_capture() API.
    // Interleaved float32 I/Q (.cf32), capped so we don't fill the disk.
    std::mutex   capture_mu;
    std::FILE*   capture_file{nullptr};
    std::size_t  capture_remaining{0};
    std::uint32_t modem_rate{0}; // modem working sample rate (post-decimate)
    std::uint32_t device_rate{0}; // radio sample rate (raw capture rate)
    std::uint64_t last_drops_reported{0}; // throttle RX-overrun log spam

    // Rolling raw-IQ ring at the modem rate, used to compute a high-time-
    // resolution spectrogram of the last detected packet on demand. ~0.5 s.
    std::mutex iq_mu;
    std::vector<std::complex<float>> iq_ring;
    std::size_t iq_pos{0};
    std::size_t iq_filled{0};

    std::mutex events_mu;
    std::deque<std::string> events; // produced by modem callback
};

Core::Core() : impl_(std::make_unique<Impl>()) {
    // Probe the radio at construction so the UI can display which backend
    // (real HackRF or synthetic null device) is in use even before RX starts.
    impl_->radio = hal::open_default_device();
    if (impl_->radio) impl_->device_name = impl_->radio->info().board_name;
}
Core::~Core() { stop(); }

void Core::start_rx(modem::Preset preset, std::uint64_t center_freq_hz) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->running) return;

    const auto params = modem::params_for(preset);
    impl_->modem = modem::make_modem(params);
    impl_->modem->set_event_callback([this](std::string msg) {
        std::lock_guard<std::mutex> lk(impl_->events_mu);
        if (impl_->events.size() >= kMaxQueuedEvents) impl_->events.pop_front();
        impl_->events.push_back(std::move(msg));
    });

    if (!impl_->radio) {
        impl_->radio = hal::open_device(impl_->requested_kind);
        if (impl_->radio) impl_->device_name = impl_->radio->info().board_name;
    }
    hal::RxConfig rx{};
    rx.center_freq_hz = center_freq_hz + static_cast<std::uint64_t>(kLoOffsetHz);
    rx.lna_gain_db = impl_->lna_db;
    rx.vga_gain_db = impl_->vga_db;
    rx.amp_enable  = impl_->amp_enable;
    const std::uint32_t target = impl_->modem->working_sample_rate_hz();
    // Run the radio at SDRangel's rate (2.4 MS/s) so a raw capture is directly
    // comparable to an SDRangel .sdriq. Never go below the modem rate.
    rx.sample_rate_hz = std::max(kDeviceRateHz, target);

    impl_->resampler = std::make_unique<dsp::Resampler>(rx.sample_rate_hz, target);
    impl_->dc_blocker.reset();
    impl_->stats.reset();
    impl_->spectrum = std::make_unique<dsp::Spectrum>(kSpectrumFftSize);
    impl_->mix_phase     = 0.0;
    impl_->last_drops_reported = 0;
    // Allocate ~0.5 s of modem-rate IQ history for the last-packet spectrogram.
    {
        std::lock_guard<std::mutex> lk(impl_->iq_mu);
        impl_->iq_ring.assign(static_cast<std::size_t>(target) / 2u,
                              std::complex<float>{0.0f, 0.0f});
        impl_->iq_pos = 0;
        impl_->iq_filled = 0;
    }
    // Offset tuning: the radio is tuned kLoOffsetHz ABOVE the channel, so the
    // LoRa signal lands at -kLoOffsetHz in baseband. Mix it back to DC by
    // multiplying with exp(+j*2*pi*kLoOffsetHz*t). The DC spike (now at raw DC,
    // removed by the DC blocker) shifts to +kLoOffsetHz, outside the channel.
    impl_->mix_phase_inc =
        2.0 * std::numbers::pi * kLoOffsetHz / static_cast<double>(rx.sample_rate_hz);

    impl_->modem_rate = target;
    impl_->device_rate = rx.sample_rate_hz;
    // Optional raw IQ capture for offline replay/debugging. The path can come
    // from the MRF_IQ_CAPTURE env var (auto-start) or be toggled at runtime
    // via start_capture(). Capture is the raw post-mix stream at the DEVICE
    // rate (kDeviceRateHz, 2.4 MS/s) so it matches an SDRangel recording.
    if (const char* path = std::getenv("MRF_IQ_CAPTURE"); path && *path) {
        start_capture(path);
    }

    impl_->radio->start_rx(rx, [this](const hal::SampleType* s, std::size_t n) {
        // 0. Strip the DC offset from the raw zero-IF input so the rest of
        //    the pipeline (stats, spectrum, modem) doesn't see a giant
        //    center-bin spike. HackRF / RTL-SDR / etc. all leak LO into the
        //    baseband; without this, the waterfall has a permanent vertical
        //    line at the tuned frequency.
        impl_->work.assign(s, s + n);
        impl_->dc_blocker.process(std::span<hal::SampleType>(impl_->work));
        // 0b. Offset-tuning mix-back. The DC blocker above just removed the LO
        //     leakage spike at raw DC; now shift the LoRa channel from
        //     -kLoOffsetHz up to baseband DC with an incremental complex NCO.
        //     Single-precision and fused into the work-buffer to keep the
        //     2.4 MS/s consumer real-time (a double-precision per-sample mix
        //     was the dominant cost causing RX overruns).
        {
            const float inc_f = static_cast<float>(impl_->mix_phase_inc);
            const float step_re = std::cos(inc_f);
            const float step_im = std::sin(inc_f);
            float osc_re = static_cast<float>(std::cos(impl_->mix_phase));
            float osc_im = static_cast<float>(std::sin(impl_->mix_phase));
            for (std::size_t i = 0; i < n; ++i) {
                const float xr = impl_->work[i].real();
                const float xi = impl_->work[i].imag();
                impl_->work[i] = hal::SampleType{xr * osc_re - xi * osc_im,
                                                 xr * osc_im + xi * osc_re};
                const float nr = osc_re * step_re - osc_im * step_im;
                const float ni = osc_re * step_im + osc_im * step_re;
                osc_re = nr;
                osc_im = ni;
                // Renormalize occasionally to counter drift; cheap reciprocal
                // sqrt suffices and avoids a divide.
                if ((i & 0xFFF) == 0xFFF) {
                    const float mag = std::sqrt(osc_re * osc_re + osc_im * osc_im);
                    if (mag > 0.0f) {
                        const float inv = 1.0f / mag;
                        osc_re *= inv;
                        osc_im *= inv;
                    }
                }
            }
            double phase = impl_->mix_phase + impl_->mix_phase_inc * static_cast<double>(n);
            phase = std::fmod(phase, 2.0 * std::numbers::pi);
            impl_->mix_phase = phase;
        }
        const hal::SampleType* clean = impl_->work.data();
        // 1. Stats over DC-corrected input.
        impl_->stats.process({clean, n});
        // 2. Spectrum / waterfall.
        impl_->spectrum->push({clean, n});
        // 2b. Optional raw IQ capture: the post-mix baseband stream at the
        //     DEVICE rate (before any resampling), so the recording matches
        //     SDRangel's raw HackRF input for apples-to-apples comparison.
        {
            std::lock_guard<std::mutex> clk(impl_->capture_mu);
            if (impl_->capture_file && impl_->capture_remaining > 0) {
                const std::size_t take = std::min(impl_->capture_remaining, n);
                std::fwrite(clean, sizeof(hal::SampleType), take,
                            impl_->capture_file);
                impl_->capture_remaining -= take;
                if (impl_->capture_remaining == 0) {
                    std::fflush(impl_->capture_file);
                    std::fclose(impl_->capture_file);
                    impl_->capture_file = nullptr;
                }
            }
        }
        // 3. Decimate to the modem's working rate, then hand to the modem.
        auto resampled = impl_->resampler->process({clean, n});
        impl_->modem->process_rx({resampled.data(), resampled.size()});

        // 3b. Append to the rolling IQ ring for the last-packet spectrogram.
        {
            std::lock_guard<std::mutex> lk(impl_->iq_mu);
            const std::size_t cap = impl_->iq_ring.size();
            if (cap > 0) {
                for (const auto& s : resampled) {
                    impl_->iq_ring[impl_->iq_pos] =
                        std::complex<float>{s.real(), s.imag()};
                    impl_->iq_pos = (impl_->iq_pos + 1u) % cap;
                    if (impl_->iq_filled < cap) ++impl_->iq_filled;
                }
            }
        }

        // 4. Report dropped samples (ring overflow = consumer can't keep up).
        //    Throttled to once per ~0.5 s of new drops so the log isn't spammed.
        if (impl_->radio) {
            const std::uint64_t drops = impl_->radio->dropped_samples();
            if (drops > impl_->last_drops_reported + impl_->device_rate / 2) {
                impl_->last_drops_reported = drops;
                std::lock_guard<std::mutex> lk(impl_->events_mu);
                if (impl_->events.size() >= kMaxQueuedEvents) impl_->events.pop_front();
                impl_->events.push_back("WARNING: dropped " +
                                        std::to_string(drops) +
                                        " samples (RX overrun)");
            }
        }
    });

    impl_->running = true;
}

void Core::stop() {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (!impl_->running) return;
    if (impl_->radio) impl_->radio->stop_rx();
    // The RX callback has now stopped; safe to close any capture.
    stop_capture();
    impl_->running = false;
}

bool Core::start_capture(const char* path) {
    if (!path || !*path) return false;
    std::lock_guard<std::mutex> clk(impl_->capture_mu);
    if (impl_->capture_file) {
        std::fclose(impl_->capture_file);
        impl_->capture_file = nullptr;
    }
    impl_->capture_file = std::fopen(path, "wb");
    if (!impl_->capture_file) {
        impl_->capture_remaining = 0;
        return false;
    }
    // Cap the capture to ~60 s at the device (raw) rate so a forgotten
    // capture can't fill the disk. device_rate is set when RX starts.
    const std::uint32_t rate = impl_->device_rate ? impl_->device_rate : kDeviceRateHz;
    impl_->capture_remaining = static_cast<std::size_t>(rate) * 60u;
    return true;
}

void Core::stop_capture() {
    std::lock_guard<std::mutex> clk(impl_->capture_mu);
    if (impl_->capture_file) {
        std::fflush(impl_->capture_file);
        std::fclose(impl_->capture_file);
        impl_->capture_file = nullptr;
    }
    impl_->capture_remaining = 0;
}

bool Core::is_capturing() const noexcept {
    std::lock_guard<std::mutex> clk(impl_->capture_mu);
    return impl_->capture_file != nullptr;
}

bool Core::is_running() const noexcept { return impl_->running; }

void Core::set_gains(std::uint8_t lna_db, std::uint8_t vga_db, bool amp) {
    // Clamp to HackRF-legal ranges.
    if (lna_db > 40) lna_db = 40;
    if (vga_db > 62) vga_db = 62;
    impl_->lna_db = lna_db;
    impl_->vga_db = vga_db;
    impl_->amp_enable = amp;
    if (impl_->radio) impl_->radio->set_rx_gains(lna_db, vga_db, amp);
}

std::size_t Core::spectrum_size() const noexcept {
    return impl_->spectrum ? impl_->spectrum->fft_size() : 0u;
}

std::uint32_t Core::sample_rate_hz() const noexcept {
    return impl_->running ? impl_->device_rate : 0u;
}

bool Core::latest_spectrum(std::span<float> out) const {
    if (!impl_->spectrum) return false;
    return impl_->spectrum->latest(out);
}

std::uint32_t Core::pull_packet_spectrogram(std::span<float> out,
                                            std::uint32_t n_time,
                                            std::uint32_t n_freq) const {
    if (n_time == 0u || n_freq == 0u) return 0u;
    if (out.size() < static_cast<std::size_t>(n_time) * n_freq) return 0u;

    // Snapshot the rolling IQ ring in chronological order.
    std::vector<std::complex<float>> snap;
    std::size_t filled = 0;
    {
        std::lock_guard<std::mutex> lk(impl_->iq_mu);
        const std::size_t cap = impl_->iq_ring.size();
        filled = impl_->iq_filled;
        if (cap == 0u || filled < 64u) return 0u;
        snap.resize(filled);
        const std::size_t start = (impl_->iq_pos + cap - filled) % cap;
        for (std::size_t i = 0; i < filled; ++i)
            snap[i] = impl_->iq_ring[(start + i) % cap];
    }

    constexpr std::size_t kFft = 512u;
    if (filled < kFft) return 0u;

    // Window length: cover the preamble (a run of identical up-chirps) plus the
    // sync word and start of the header, scaled to the *symbol* duration so the
    // individual chirps are always resolvable regardless of spreading factor.
    // A fixed millisecond window would squash fast (low-SF) packets into a few
    // pixels; sizing by symbols keeps each chirp several rows tall.
    const std::size_t rate = impl_->modem_rate ? impl_->modem_rate : 1'000'000u;
    std::uint8_t sf = 11;
    std::uint32_t bw = 250'000u;
    std::uint16_t preamble = 16u;
    if (impl_->modem) {
        const auto& p = impl_->modem->params();
        sf = p.spreading_factor;
        if (p.bandwidth_hz) bw = p.bandwidth_hz;
        preamble = p.preamble_symbols;
    }
    // Samples per LoRa symbol at the modem rate (= 2^SF * oversampling).
    const std::size_t sym_samples =
        (static_cast<std::size_t>(1u) << sf) * (rate / std::max<std::uint32_t>(1u, bw));
    // Show the whole preamble plus ~12 symbols of sync/header.
    const std::size_t window_symbols = static_cast<std::size_t>(preamble) + 12u;
    std::size_t window = std::min<std::size_t>(filled, window_symbols * sym_samples);
    if (window < kFft) window = kFft;

    // Auto-locate the packet by energy instead of relying on capture timing.
    // Find the highest-energy region of the ring and anchor the window so the
    // packet sits near the start. This is robust to event-drain latency that
    // would otherwise make a fixed "newest N ms" window miss the packet (and
    // snapshot post-packet noise instead).
    std::size_t off0 = filled - window; // default: newest window
    if (filled > window) {
        constexpr std::size_t kBlk = 2048u;
        const std::size_t nblk = filled / kBlk;
        std::size_t peak_blk = 0;
        float peak_e = -1.0f;
        for (std::size_t b = 0; b < nblk; ++b) {
            float e = 0.0f;
            const std::size_t base = b * kBlk;
            for (std::size_t i = 0; i < kBlk; ++i) {
                const auto& v = snap[base + i];
                e += v.real() * v.real() + v.imag() * v.imag();
            }
            if (e > peak_e) { peak_e = e; peak_blk = b; }
        }
        const std::size_t peak_sample = peak_blk * kBlk;
        // Place the peak ~15% into the window so the preamble that precedes it
        // is visible and the payload that follows fits in the remainder.
        const std::size_t lead = window * 15ull / 100ull;
        off0 = (peak_sample > lead) ? peak_sample - lead : 0u;
        if (off0 + window > filled) off0 = filled - window;
    }

    const std::size_t hop = (n_time > 1u)
        ? std::max<std::size_t>(1u, (window - kFft) / (n_time - 1u))
        : 1u;

    // Hann window.
    std::vector<float> win(kFft);
    constexpr float kPi = std::numbers::pi_v<float>;
    for (std::size_t i = 0; i < kFft; ++i)
        win[i] = 0.5f * (1.0f - std::cos(2.0f * kPi * static_cast<float>(i) /
                                         static_cast<float>(kFft - 1)));

    dsp::Fft fft(kFft);
    std::vector<std::complex<float>> buf(kFft);
    std::vector<float> fulldb(kFft);

    // Channel crop: keep the central band around DC. Size it to ~1.3x the LoRa
    // bandwidth so the chirps fill the view (the modem runs oversampled, so the
    // channel only occupies bw/modem_rate of the full FFT span).
    const float cropFrac =
        std::clamp(0.65f * static_cast<float>(bw) / static_cast<float>(rate),
                   0.05f, 0.5f);
    const std::size_t half = kFft / 2u;
    const std::size_t cropHalf =
        std::max<std::size_t>(1u, static_cast<std::size_t>(cropFrac * kFft));
    const std::size_t cropLo = half - cropHalf;
    const std::size_t cropN = cropHalf * 2u;

    const float norm = 1.0f / static_cast<float>(kFft);
    for (std::uint32_t t = 0; t < n_time; ++t) {
        const std::size_t base =
            off0 + std::min<std::size_t>(static_cast<std::size_t>(t) * hop,
                                         window - kFft);
        for (std::size_t i = 0; i < kFft; ++i)
            buf[i] = snap[base + i] * win[i];
        fft.forward(std::span<std::complex<float>>(buf.data(), kFft));

        // Same fftshift + display mirror the live waterfall uses, so the
        // snapshot's frequency axis matches.
        for (std::size_t k = 0; k < kFft; ++k) {
            const std::size_t shifted = (k + half) % kFft;
            const std::size_t mirror  = (kFft - shifted) % kFft;
            const auto v = buf[k] * norm;
            const float p = v.real() * v.real() + v.imag() * v.imag();
            fulldb[mirror] = (p > 1e-20f) ? 10.0f * std::log10(p) : -200.0f;
        }

        float* row = &out[static_cast<std::size_t>(t) * n_freq];
        for (std::uint32_t f = 0; f < n_freq; ++f) {
            const std::size_t c = (n_freq > 1u)
                ? static_cast<std::size_t>(static_cast<std::uint64_t>(f) *
                                           (cropN - 1u) / (n_freq - 1u))
                : 0u;
            row[f] = fulldb[cropLo + c];
        }
    }
    return n_time;
}

bool Core::set_device(hal::DeviceKind kind) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->running) return false;
    impl_->requested_kind = kind;
    impl_->radio = hal::open_device(kind);
    impl_->device_name = impl_->radio ? impl_->radio->info().board_name : "(none)";
    return true;
}

hal::DeviceKind Core::device_kind() const noexcept {
    return impl_->radio ? impl_->radio->kind() : hal::DeviceKind::Null;
}

bool Core::is_device_available(hal::DeviceKind kind) const noexcept {
    return hal::device_available(kind);
}

const char* Core::device_name() const noexcept {
    return impl_->device_name.c_str();
}

const char* Core::device_status() const noexcept {
    return hal::open_default_device_status();
}

std::size_t Core::pull_event(std::span<char> out) noexcept {
    std::lock_guard<std::mutex> lk(impl_->events_mu);
    if (impl_->events.empty() || out.empty()) return 0;
    const auto& front = impl_->events.front();
    if (front.size() + 1 > out.size()) return 0; // include room for NUL
    std::size_t n = front.size();
    std::memcpy(out.data(), front.data(), n);
    out[n] = '\0';
    impl_->events.pop_front();
    return n;
}

CoreSignalStats Core::signal_stats() const noexcept {
    const auto s = impl_->stats.snapshot();
    return CoreSignalStats{
        s.rssi_dbfs,
        s.peak_dbfs,
        s.dc_re,
        s.dc_im,
        s.total_samples,
    };
}

} // namespace mrf
