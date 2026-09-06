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
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <mutex>
#include <numbers>
#include <string>
#include <thread>
#include <vector>

namespace mrf {

namespace {
constexpr std::size_t kSpectrumFftSize = 1024;
constexpr std::size_t kMaxQueuedEvents = 1024;
// Fill level past which the queue keeps only the lines the app acts on; see
// Impl::is_diagnostic_line.
constexpr std::size_t kShedDiagnosticsAbove = kMaxQueuedEvents / 2;
constexpr std::uint32_t kDefaultDeviceRateHz = 2'400'000u;
constexpr std::uint32_t kWaterfallTargetFps = 60u;
constexpr std::uint32_t kWaterfallMaxFramesToPull = 64u;
constexpr std::uint32_t kHackRfStableMaxRateHz = 16'000'000u;
constexpr std::uint32_t kRtlSdrDecodeSafeMaxRateHz = 2'560'000u;
constexpr std::uint32_t kHackRfRatesHz[] = {
    2'000'000u,
    2'400'000u,
    4'000'000u,
    8'000'000u,
    10'000'000u,
    12'500'000u,
    16'000'000u,
    20'000'000u,
};
constexpr std::uint32_t kRtlSdrRatesHz[] = {
    960'000u,
    1'024'000u,
    1'200'000u,
    1'440'000u,
    1'600'000u,
    1'800'000u,
    1'920'000u,
    2'048'000u,
    2'400'000u,
    2'560'000u,
    2'880'000u,
    3'200'000u,
};

constexpr std::uint32_t kWaterfallHistoryMaxFrameRate =
    kWaterfallMaxFramesToPull * kWaterfallTargetFps;

std::size_t compute_history_frame_stride(std::uint32_t sample_rate_hz) {
    const std::uint32_t raw_frame_rate =
        std::max<std::uint32_t>(1u, sample_rate_hz / static_cast<std::uint32_t>(kSpectrumFftSize));
    return std::max<std::size_t>(
        1u,
        (raw_frame_rate + kWaterfallHistoryMaxFrameRate - 1u) / kWaterfallHistoryMaxFrameRate);
}

std::uint32_t nearest_supported_rate(std::span<const std::uint32_t> rates,
                                     std::uint32_t requested,
                                     std::uint32_t minimum) {
    std::uint32_t best = 0;
    std::uint64_t best_delta = 0;
    for (std::uint32_t rate : rates) {
        if (rate < minimum) continue;
        const std::uint64_t delta = rate >= requested
            ? static_cast<std::uint64_t>(rate - requested)
            : static_cast<std::uint64_t>(requested - rate);
        if (best == 0 || delta < best_delta) {
            best = rate;
            best_delta = delta;
        }
    }
    return best != 0 ? best : std::max(minimum, rates.empty() ? requested : rates.back());
}

std::uint32_t normalize_rx_sample_rate(hal::DeviceKind kind,
                                       std::uint32_t requested,
                                       std::uint32_t minimum) {
    if (requested == 0) requested = kDefaultDeviceRateHz;
    switch (kind) {
    case hal::DeviceKind::HackRf:
        return nearest_supported_rate(kHackRfRatesHz,
                                      std::min(requested, kHackRfStableMaxRateHz),
                                      minimum);
    case hal::DeviceKind::RtlSdr:
        return nearest_supported_rate(kRtlSdrRatesHz,
                                      std::min(requested, kRtlSdrDecodeSafeMaxRateHz),
                                      minimum);
    default:
        return std::max(requested, minimum);
    }
}
}

struct Core::Impl {
    std::unique_ptr<hal::IRadioDevice> rx_radio;
    std::unique_ptr<hal::IRadioDevice> tx_radio;
    // Packet transmitter (CH341+SX1262 stick). Mutually exclusive with
    // tx_radio: at most one of the two is non-null, chosen by
    // tx_requested_kind.
    //
    // shared_ptr, not unique_ptr, because a burst can run for seconds on a
    // slow preset and transmit() must not hold start_mu for that long (the UI
    // polls device names and kinds through it). transmit() takes a reference
    // under the lock and then works through its own copy, so a concurrent
    // set_tx_device() can release the member without freeing a device that is
    // mid-burst. packet_radio_mu separately serializes the bursts themselves:
    // Sx126xRadio drives one SPI conversation at a time and is not reentrant.
    std::shared_ptr<hal::IPacketRadio> packet_radio;
    std::mutex packet_radio_mu;
    // No board until the user says so — see Sx126xBoard::Unspecified.
    hal::Sx126xBoard sx1262_board{hal::Sx126xBoard::Unspecified};
    // Which stick, when several are attached. Empty takes the first that
    // answers. The EEPROM serial is the only thing that distinguishes them.
    std::string      sx1262_serial;
    std::int8_t      tx_power_dbm{22};
    // Declared transmit band, from the operator's region. Zero until the
    // caller says otherwise — see Core::set_tx_band_limits().
    std::uint64_t    tx_band_min_hz{0};
    std::uint64_t    tx_band_max_hz{0};
    // Signal quality from the last packet the stick received. Real numbers off
    // the radio rather than estimates off an IQ stream, and the only source of
    // either when no SDR is running.
    std::atomic<float> packet_rssi_dbm{0.0f};
    std::atomic<bool>  have_packet_rssi{false};
    hal::DeviceKind rx_requested_kind{hal::DeviceKind::Null};
    hal::DeviceKind tx_requested_kind{hal::DeviceKind::HackRf};
    std::string rx_device_name{"(none)"};
    std::string tx_device_name{"(none)"};
    std::uint8_t lna_db{24};
    std::uint8_t vga_db{20};
    bool         amp_enable{false};
    bool         bias_tee{false};
    std::uint32_t requested_rx_sample_rate_hz{kDefaultDeviceRateHz};
    std::unique_ptr<modem::ILoraModem> modem;
    std::unique_ptr<dsp::Resampler> resampler;
    dsp::DcBlocker dc_blocker;
    bool dc_block_enabled{true}; // toggleable via set_device_option("dc_block", 0/1)
    dsp::SignalStats stats;
    std::unique_ptr<dsp::Spectrum> spectrum;
    router::FloodingRouter flooder{};
    std::vector<hal::SampleType> work; // scratch for DC-blocking
    std::atomic<bool> running{false};
    std::mutex start_mu;

    // Last RX configuration, remembered so a transmit() burst can pause and
    // then resume the receiver on the same preset/frequency.
    modem::LoraParams last_rx_params{modem::params_for(modem::Preset::LongFast)};
    std::uint64_t last_rx_center{915'000'000};

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
    // resolution spectrogram of the last detected packet on demand.
    std::mutex iq_mu;
    std::vector<std::complex<float>> iq_ring;
    std::size_t iq_pos{0};
    std::size_t iq_filled{0};
    std::uint64_t iq_total_samples{0};
    std::uint64_t last_packet_start{0};
    std::uint64_t last_packet_end{0};

    std::mutex events_mu;
    std::deque<std::string> events; // produced by modem callback
    std::uint64_t events_dropped{0};
    std::uint64_t diagnostics_shed{0};

    // A line that only explains a decode — sync internals, raw symbol and
    // byte dumps, SFD-search candidates — as opposed to the preamble, header
    // and payload lines the app parses. A frame emits several of them, so
    // under load they are what fills the queue, and they are what the reader
    // can do without. The modem indents them deeper than the lines it
    // reports on, which is what tells them apart here.
    static bool is_diagnostic_line(const std::string& msg) {
        return msg.rfind("    ", 0) == 0 || msg.rfind("  sfd?", 0) == 0;
    }

    // Adds a line. Past half full, diagnostics are shed so the payload lines
    // behind them still fit; once full, the oldest line goes. Both counts
    // travel to the reader rather than being reported from here: a full
    // queue has no slot to put the notice in, which is the one line that
    // must not be lost. Caller holds events_mu.
    void queue_event_locked(std::string msg) {
        if (events.size() >= kShedDiagnosticsAbove && is_diagnostic_line(msg)) {
            ++diagnostics_shed;
            return;
        }
        if (events.size() >= kMaxQueuedEvents) {
            events.pop_front();
            ++events_dropped;
        }
        events.push_back(std::move(msg));
    }

    // Queue a line for the UI log. Used by the packet-radio path, which has no
    // modem callback to route its diagnostics through.
    void push_event(std::string msg) {
        std::lock_guard<std::mutex> lk(events_mu);
        queue_event_locked(std::move(msg));
    }

    // Open or release the SX1262 stick to match the current RX/TX selections.
    // One stick backs both directions — it is a half-duplex transceiver, and
    // the single-stick, no-SDR setup is the whole point of the receive path —
    // so the radio is opened once when either role asks for it and released
    // only when neither does. Caller must hold start_mu.
    void sync_packet_radio() {
        const bool wanted = rx_requested_kind == hal::DeviceKind::Sx1262 ||
                            tx_requested_kind == hal::DeviceKind::Sx1262;
        // Wait for any burst still on the air before swapping the device out.
        // transmit() keeps its own reference so nothing is freed underneath it,
        // but the old transport would still hold the CH341 handle, and
        // reopening the same index against an exclusive claim fails.
        std::lock_guard<std::mutex> burst(packet_radio_mu);
        if (!wanted) {
            // Releasing it hands the stick back to meshtasticd or another tool
            // without restarting MeshRF.
            packet_radio.reset();
            return;
        }
        if (packet_radio) return; // already open on the right board
        packet_radio = hal::open_packet_radio(sx1262_board, sx1262_serial);
        push_event(std::string("SX1262: ") + hal::packet_radio_status());
    }

    // Name to show for whichever role the stick is filling. Separates "you have
    // not said which stick this is" from "no hardware found" — they need
    // completely different actions from the user.
    std::string packet_radio_name() const {
        if (packet_radio) return packet_radio->info().board_name;
        if (sx1262_board == hal::Sx126xBoard::Unspecified) return "SX1262 (select a board)";
        return "(none)";
    }
};

Core::Core() : impl_(std::make_unique<Impl>()) {
    // RX starts disabled. TX defaults to HackRF so transmit controls appear
    // when a HackRF is connected, without any auto-detected synthetic fallback.
    impl_->rx_device_name = "(none)";
    impl_->tx_radio = hal::open_device(hal::DeviceKind::HackRf);
    impl_->tx_device_name = impl_->tx_radio ? impl_->tx_radio->info().board_name : "(none)";
}
Core::~Core() { stop(); }

void Core::start_rx(modem::Preset preset, std::uint64_t center_freq_hz) {
    start_rx(modem::params_for(preset), center_freq_hz);
}

void Core::start_rx(const modem::LoraParams& params, std::uint64_t center_freq_hz) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->running) return;

    // Remember the active RX config so transmit() can resume it after a burst.
    impl_->last_rx_params = params;
    impl_->last_rx_center = center_freq_hz;

    // Hardware modem path. None of the pipeline below exists here: there is no
    // IQ, so no resampler, no spectrum, no waterfall, no packet spectrogram and
    // no IQ capture. The radio hands up whole frames, which are turned into the
    // same event lines the software demodulator emits so every layer above —
    // decrypt, protobuf, routing, MQTT, UI — is unchanged.
    if (impl_->rx_requested_kind == hal::DeviceKind::Sx1262) {
        impl_->sync_packet_radio();
        if (!impl_->packet_radio) {
            impl_->push_event(std::string("SX1262: ") + hal::packet_radio_status());
            throw std::runtime_error("No RX device selected or available");
        }

        hal::PacketRadioConfig cfg{};
        cfg.center_freq_hz = center_freq_hz;
        cfg.params         = params;
        cfg.power_dbm      = impl_->tx_power_dbm;

        auto radio = impl_->packet_radio;
        std::string error;
        if (!radio->start_rx(cfg, [this](const hal::ReceivedPacket& p) {
                impl_->packet_rssi_dbm.store(p.rssi_dbm, std::memory_order_relaxed);
                impl_->have_packet_rssi.store(true, std::memory_order_relaxed);

                // Two lines, in the order the software demodulator produces
                // them, because the app parses that stream rather than a
                // structured callback. The preamble line carries the SNR the
                // payload is attributed to; here it is the radio's own
                // measurement instead of a peak-above-noise estimate.
                char head[128];
                std::snprintf(head, sizeof(head),
                              "preamble: SF%u BW%uk hardware peak=%.1fdB",
                              static_cast<unsigned>(impl_->last_rx_params.spreading_factor),
                              static_cast<unsigned>(impl_->last_rx_params.bandwidth_hz / 1000u),
                              p.snr_db);
                impl_->push_event(head);

                std::string hex;
                hex.reserve(p.payload.size() * 2);
                char byte_hex[3];
                for (const auto b : p.payload) {
                    std::snprintf(byte_hex, sizeof(byte_hex), "%02X", b);
                    hex += byte_hex;
                }
                // The radio checked the CRC before handing this up, so it is
                // OK by construction; the received/computed pair is echoed as
                // matching because the hardware does not report the values.
                char line[96];
                std::snprintf(line, sizeof(line), "payload[OK] len=%zu crc=0000/0000 ",
                              p.payload.size());
                impl_->push_event(std::string(line) + hex);
            }, error)) {
            impl_->push_event("SX1262: could not start receive \xE2\x80\x94 " + error);
            throw std::runtime_error("SX1262 receive failed to start: " + error);
        }

        impl_->modem_rate = 0;
        impl_->device_rate = 0;
        impl_->running = true;
        impl_->push_event("SX1262: receiving \xE2\x80\x94 no spectrum or waterfall "
                          "from a hardware modem");
        return;
    }

    impl_->modem = modem::make_modem(params);
    impl_->modem->set_frame_callback([this](const modem::DecodedFrame& frame) {
        std::lock_guard<std::mutex> iq_lk(impl_->iq_mu);
        impl_->last_packet_start = frame.sample_index;
        impl_->last_packet_end = frame.end_sample_index;
    });
    impl_->modem->set_event_callback([this](std::string msg) {
        std::lock_guard<std::mutex> ev_lk(impl_->events_mu);
        impl_->queue_event_locked(std::move(msg));
    });

    if (!impl_->rx_radio) {
        impl_->rx_radio = hal::open_device(impl_->rx_requested_kind);
        if (impl_->rx_radio) impl_->rx_device_name = impl_->rx_radio->info().board_name;
    }
    if (!impl_->rx_radio)
        throw std::runtime_error("No RX device selected or available");
    hal::RxConfig rx{};
    rx.center_freq_hz = center_freq_hz;
    rx.lna_gain_db = impl_->lna_db;
    rx.vga_gain_db = impl_->vga_db;
    rx.amp_enable  = impl_->amp_enable;
    const std::uint32_t target = impl_->modem->working_sample_rate_hz();
    // Keep the radio at a supported device rate chosen by the user, but never
    // below the modem's working rate.
    rx.sample_rate_hz = normalize_rx_sample_rate(
        impl_->rx_radio->kind(), impl_->requested_rx_sample_rate_hz, target);

    impl_->resampler = std::make_unique<dsp::Resampler>(rx.sample_rate_hz, target);
    impl_->dc_blocker.reset();
    impl_->stats.reset();
    impl_->spectrum = std::make_unique<dsp::Spectrum>(kSpectrumFftSize);
    impl_->spectrum->set_history_frame_stride(compute_history_frame_stride(rx.sample_rate_hz));
    impl_->last_drops_reported = 0;
    // Allocate enough modem-rate IQ history for full-frame packet snapshots.
    // Long SF12 frames can run ~10 seconds for max-length packets, and the UI
    // only commits the snapshot after CRC OK, so a short ring can lose the
    // preamble before the snapshot is requested.
    {
        constexpr std::size_t kPacketHistorySeconds = 12u;
        std::lock_guard<std::mutex> ring_init_lk(impl_->iq_mu);
        impl_->iq_ring.assign(static_cast<std::size_t>(target) * kPacketHistorySeconds,
                              std::complex<float>{0.0f, 0.0f});
        impl_->iq_pos = 0;
        impl_->iq_filled = 0;
        impl_->iq_total_samples = 0;
        impl_->last_packet_start = 0;
        impl_->last_packet_end = 0;
    }
    impl_->modem_rate = target;
    impl_->device_rate = rx.sample_rate_hz;
    // Optional raw IQ capture for offline replay/debugging. The path can come
    // from the MRF_IQ_CAPTURE env var (auto-start) or be toggled at runtime
    // via start_capture(). Capture is the raw post-mix stream at the selected
    // device rate so it matches the live RX stream.
    if (const char* path = std::getenv("MRF_IQ_CAPTURE"); path && *path) {
        start_capture(path);
    }

    impl_->rx_radio->start_rx(rx, [this](const hal::SampleType* s, std::size_t n) {
        // 0. Strip the DC offset from the raw zero-IF input so the rest of
        //    the pipeline (stats, spectrum, modem) doesn't see a giant
        //    center-bin spike. HackRF / RTL-SDR / etc. all leak LO into the
        //    baseband; without this, the waterfall has a permanent vertical
        //    line at the tuned frequency.
        impl_->work.assign(s, s + n);
        if (impl_->dc_block_enabled)
            impl_->dc_blocker.process(std::span<hal::SampleType>(impl_->work));
        // 1. Spectrum / waterfall on the DC-blocked signal.
        impl_->spectrum->push({impl_->work.data(), n});
        const hal::SampleType* clean = impl_->work.data();
        // 2. Stats.
        impl_->stats.process({clean, n});
        // 3. Optional raw IQ capture.
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
            std::lock_guard<std::mutex> ring_lk(impl_->iq_mu);
            const std::size_t cap = impl_->iq_ring.size();
            if (cap > 0) {
                for (const auto& sample : resampled) {
                    impl_->iq_ring[impl_->iq_pos] =
                        std::complex<float>{sample.real(), sample.imag()};
                    impl_->iq_pos = (impl_->iq_pos + 1u) % cap;
                    if (impl_->iq_filled < cap) ++impl_->iq_filled;
                    ++impl_->iq_total_samples;
                }
            }
        }

        // 4. Report dropped samples (ring overflow = consumer can't keep up).
        //    Throttled to once per ~0.5 s of new drops so the log isn't spammed.
        if (impl_->rx_radio) {
            const std::uint64_t drops = impl_->rx_radio->dropped_samples();
            if (drops > impl_->last_drops_reported + impl_->device_rate / 2) {
                impl_->last_drops_reported = drops;
                std::lock_guard<std::mutex> lk(impl_->events_mu);
                impl_->queue_event_locked("WARNING: dropped " +
                                          std::to_string(drops) +
                                          " samples (RX overrun)");
            }
        }
    });

    // Re-apply cached device-specific options now that the radio is open and
    // the stream is running (RTL-SDR only acquires its handle in start_rx).
    impl_->rx_radio->set_rx_option("bias_tee", impl_->bias_tee ? 1 : 0);

    impl_->running = true;
}

void Core::stop() {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (!impl_->running) return;
    // The packet radio owns a receive thread; stopping it joins that thread and
    // parks the RF switch. Safe when it was never started.
    if (impl_->packet_radio) impl_->packet_radio->stop_rx();
    if (impl_->rx_radio) impl_->rx_radio->stop_rx();
    // The RX callback has now stopped; safe to close any capture.
    stop_capture();
    impl_->running = false;
}

bool Core::can_transmit() const noexcept {
    // Locked because packet_tx is a shared_ptr that set_tx_device() can
    // reassign; nothing inside Core calls this while holding start_mu, so
    // taking it here cannot deadlock.
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->tx_requested_kind == hal::DeviceKind::Sx1262)
        return impl_->packet_radio != nullptr;
    const hal::IRadioDevice* tx =
        (!impl_->tx_radio && impl_->rx_radio &&
         impl_->rx_radio->kind() == impl_->tx_requested_kind)
            ? impl_->rx_radio.get()
            : impl_->tx_radio.get();
    return tx && tx->kind() == hal::DeviceKind::HackRf;
}

bool Core::transmit(modem::Preset preset, std::uint64_t center_freq_hz,
                    std::span<const std::uint8_t> payload,
                    std::uint8_t txvga_gain_db, bool amp_enable) {
    return transmit(modem::params_for(preset), center_freq_hz, payload, txvga_gain_db, amp_enable);
}

bool Core::transmit(const modem::LoraParams& params, std::uint64_t center_freq_hz,
                    std::span<const std::uint8_t> payload,
                    std::uint8_t txvga_gain_db, bool amp_enable) {
    // Nothing transmits unless we are listening, on every backend. Two reasons,
    // and the first is why this lives here rather than only in the UI: the app
    // has several unsolicited senders — auto-replies, auto-reports, scheduled
    // scripts, ack retransmits — and at startup they would otherwise key up
    // before the operator has knowingly put the node on the air. Second, no
    // receiver means no listen-before-talk: transmit() has no idea whether the
    // channel is busy, so it would talk over whatever is already there.
    //
    // `running` covers both paths — the SDR stream and the stick's continuous
    // receive both set it — and is atomic, so this needs no lock.
    if (!impl_->running) {
        impl_->push_event("Transmit refused: start RX first \xE2\x80\x94 "
                          "nothing transmits while the receiver is stopped");
        return false;
    }
    // Hardware modem path: hand the framed bytes straight to the SX1262 and
    // skip modulation, resampling and the whole IQ pipeline. RX is deliberately
    // left running — the stick is a separate USB device, so the SDR keeps its
    // spectrum and waterfall live through the burst (and will hear it).
    //
    // Snapshot the device and its settings under start_mu, then release the
    // lock before the burst: a slow preset takes seconds, and the UI polls
    // device names and kinds through that same mutex.
    bool                                  use_packet_tx = false;
    std::shared_ptr<hal::IPacketRadio> packet_device;
    hal::PacketRadioConfig                   packet_cfg{};
    {
        std::lock_guard<std::mutex> lk(impl_->start_mu);
        use_packet_tx = impl_->tx_requested_kind == hal::DeviceKind::Sx1262;
        if (use_packet_tx) {
            packet_device = impl_->packet_radio;
            packet_cfg.center_freq_hz = center_freq_hz;
            packet_cfg.params         = params;
            packet_cfg.power_dbm      = impl_->tx_power_dbm;
            packet_cfg.tx_band_min_hz = impl_->tx_band_min_hz;
            packet_cfg.tx_band_max_hz = impl_->tx_band_max_hz;
        }
    }
    if (use_packet_tx) {
        if (!packet_device) {
            impl_->push_event("SX1262: no transmitter open");
            return false;
        }
        if (payload.empty()) return false;

        std::lock_guard<std::mutex> burst(impl_->packet_radio_mu);
        std::string error;
        if (!packet_device->transmit(packet_cfg, payload, error)) {
            impl_->push_event("SX1262 transmit failed: " + error);
            return false;
        }
        return true;
    }


    hal::IRadioDevice* tx_radio = nullptr;
    bool tx_uses_rx_radio = false;
    if (!impl_->tx_radio && impl_->rx_radio &&
        impl_->rx_radio->kind() == impl_->tx_requested_kind) {
        tx_radio = impl_->rx_radio.get();
        tx_uses_rx_radio = true;
    } else {
        tx_radio = impl_->tx_radio.get();
    }
    if (!tx_radio || tx_radio->kind() != hal::DeviceKind::HackRf)
        return false;
    if (payload.empty()) return false;

    // 1. Modulate the on-air bytes into a LoRa IQ frame at the modem rate.
    auto modem = modem::make_modem(params);
    auto iq = modem->encode(payload);
    if (iq.empty()) return false;

    const std::uint32_t modem_rate = modem->working_sample_rate_hz();
    const std::uint32_t dev_rate = std::max(kDefaultDeviceRateHz, modem_rate);

    // Pad the modem-rate frame with leading + trailing zeros.
    //   * Lead-in (~20 ms): hackrf_start_tx primes the USB pipe and the PA/VGA
    //     bias takes time to settle, so the first samples clocked out of the
    //     DAC carry a startup transient. Without a lead-in that transient lands
    //     squarely on the LoRa preamble, corrupting the very symbols a strict
    //     receiver (e.g. SDRangel, RadioLib) needs to detect/lock the frame —
    //     the "energy on the waterfall but never decodes" failure. Prepending
    //     silence burns the transient off into dead air so the preamble is
    //     clean. (Zeros also flush the resampler's filter warm-up state.)
    //   * Tail (~5 ms): lets the resampler filter flush so the burst ends
    //     cleanly (no abrupt cut mid-symbol).
    const std::size_t modem_lead = modem_rate / 50u;  // ~20 ms of zeros
    const std::size_t modem_tail = modem_rate / 200u; // ~5 ms of zeros
    iq.insert(iq.begin(), modem_lead, hal::SampleType{0.0f, 0.0f});
    iq.insert(iq.end(), modem_tail, hal::SampleType{0.0f, 0.0f});

    // 2. Upsample modem-rate -> device (radio) rate.
    dsp::Resampler up(modem_rate, dev_rate);
    auto rs = up.process(std::span<const hal::SampleType>(iq.data(), iq.size()));
    std::vector<hal::SampleType> tx(rs.begin(), rs.end());
    if (tx.empty()) return false;

    // 3. Offset-tuning mix removed — radio is tuned directly to the channel.
    // 4. Normalize to ~0.95 full-scale so the int8 DAC is driven hard (more
    //    radiated power) while keeping a little headroom against the resampler
    //    + offset-mix overshoot so the chirp envelope doesn't clip to a square.
    float max_mag = 0.0f;
    for (const auto& s : tx)
        max_mag = std::max(max_mag, std::abs(s));
    if (max_mag > 0.0f) {
        const float scale = 0.95f / max_mag;
        for (auto& s : tx) s *= scale;
    }

    // 4b. Optional diagnostic: dump the final device-rate TX IQ (post resample,
    //     offset-mix and normalization — i.e. exactly what is handed to the
    //     DAC) to a .cf32 file when MRF_TX_CAPTURE is set. The stream is at
    //     dev_rate with the LoRa channel sitting at -kLoOffsetHz, so it can be
    //     replayed/analyzed offline (e.g. through scripts/analyze_capture.py or
    //     our own RX) to verify the transmit chain end-to-end.
    if (const char* tx_path = std::getenv("MRF_TX_CAPTURE"); tx_path && *tx_path) {
        if (std::FILE* f = std::fopen(tx_path, "wb")) {
            // std::complex<float> is stored as interleaved {re, im} floats.
            std::fwrite(tx.data(), sizeof(hal::SampleType), tx.size(), f);
            std::fclose(f);
        }
    }

    // 5. Pause RX only when TX shares the same HackRF handle. If RX is on a
    // separate device (for example RTL-SDR), keep receiving during TX.
    const bool was_running = is_running();
    const auto rx_params = impl_->last_rx_params;
    const std::uint64_t rx_center = impl_->last_rx_center;
    if (was_running && tx_uses_rx_radio) stop();

    // 6. Stream the buffer once via the HackRF TX callback, blocking until the
    //    burst has physically clocked out of the DAC.
    //
    //    hackrf_start_tx primes ~8 USB transfers (~2 MB) up front, which is far
    //    larger than a short LoRa frame. If we stop the moment the last real
    //    sample is *handed to* libhackrf, the DAC has not yet emitted anything
    //    and nothing reaches the antenna (the app still logs "Sent"). To make
    //    the RF actually radiate we keep the TX stream alive, feeding zeros
    //    after the payload, until enough wall-clock time has elapsed for the
    //    whole burst (plus margin) to be clocked out at dev_rate.
    {
        hal::TxConfig cfg{};
        cfg.center_freq_hz = center_freq_hz;
        cfg.sample_rate_hz = dev_rate;
        cfg.txvga_gain_db  = txvga_gain_db;
        cfg.amp_enable     = amp_enable;

        std::mutex m;
        std::condition_variable cv;
        bool done = false;
        std::size_t pos = 0;

        // How long the real payload takes to play out at the device rate, plus
        // a generous margin to cover the USB/DAC pipeline latency and the
        // amp/VGA settling. We keep keying (sending zeros) until this elapses.
        const double burst_secs = static_cast<double>(tx.size()) / dev_rate;
        const auto hold = std::chrono::microseconds(
            static_cast<long long>((burst_secs + 0.050) * 1e6));
        const auto t_start = std::chrono::steady_clock::now();

        tx_radio->start_tx(cfg, [&](hal::SampleType* out, std::size_t cap)
                                        -> std::size_t {
            // First, drain the real payload.
            if (pos < tx.size()) {
                const std::size_t take = std::min(cap, tx.size() - pos);
                std::memcpy(out, tx.data() + pos, take * sizeof(hal::SampleType));
                pos += take;
                // Zero-fill any remainder of this buffer.
                for (std::size_t i = take; i < cap; ++i)
                    out[i] = hal::SampleType{0.0f, 0.0f};
                return cap;
            }

            // Payload sent; keep keying with zeros until the hold elapses so the
            // DAC finishes emitting the burst before we cut the carrier.
            if (std::chrono::steady_clock::now() - t_start < hold) {
                for (std::size_t i = 0; i < cap; ++i)
                    out[i] = hal::SampleType{0.0f, 0.0f};
                return cap;
            }

            {
                std::lock_guard<std::mutex> l(m);
                done = true;
            }
            cv.notify_all();
            return 0; // ends the TX stream
        });

        {
            std::unique_lock<std::mutex> l(m);
            // Bound the wait so a stalled USB callback can't hang the UI thread.
            cv.wait_for(l, std::chrono::seconds(2), [&] { return done; });
        }
        tx_radio->stop_tx();
    }

    // 7. Resume RX if a shared-radio burst paused it.
    if (was_running && tx_uses_rx_radio) start_rx(rx_params, rx_center);
    return true;
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
    const std::uint32_t rate = impl_->device_rate ? impl_->device_rate : kDefaultDeviceRateHz;
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
    if (impl_->rx_radio) impl_->rx_radio->set_rx_gains(lna_db, vga_db, amp);
}

void Core::set_device_option(std::string_view key, int value) {
    // Cache so the option survives a stop/start cycle, then push live.
    if (key == "bias_tee")   impl_->bias_tee          = (value != 0);
    if (key == "rx_sample_rate_hz") {
        impl_->requested_rx_sample_rate_hz = value > 0
            ? static_cast<std::uint32_t>(value)
            : kDefaultDeviceRateHz;
        return;
    }
    if (key == "dc_block")  { impl_->dc_block_enabled  = (value != 0);
                              impl_->dc_blocker.reset(); return; }
    if (impl_->rx_radio) impl_->rx_radio->set_rx_option(key, value);
}

std::size_t Core::spectrum_size() const noexcept {
    return impl_->spectrum ? impl_->spectrum->fft_size() : 0u;
}

std::uint32_t Core::sample_rate_hz() const noexcept {
    return impl_->running ? impl_->device_rate : 0u;
}

std::uint64_t Core::spectrum_center_hz() const noexcept {
    return impl_->running ? impl_->last_rx_center : 0u;
}

bool Core::latest_spectrum(std::span<float> out) const {
    if (!impl_->spectrum) return false;
    return impl_->spectrum->latest(out);
}

std::uint64_t Core::spectrum_frame_count() const noexcept {
    if (!impl_->spectrum) return 0u;
    return impl_->spectrum->frame_count();
}

std::uint32_t Core::spectrum_history_frame_rate_hz() const noexcept {
    if (!impl_->running || !impl_->spectrum) return 0u;
    const std::uint32_t bins = static_cast<std::uint32_t>(impl_->spectrum->fft_size());
    if (bins == 0u) return 0u;

    const std::uint32_t raw_frame_rate =
        std::max<std::uint32_t>(1u, impl_->device_rate / bins);
    const std::size_t stride = compute_history_frame_stride(impl_->device_rate);
    return static_cast<std::uint32_t>(
        std::max<std::uint64_t>(1u, raw_frame_rate / static_cast<std::uint32_t>(stride)));
}

std::uint32_t Core::pull_spectrum_frames(
    std::uint64_t after_frame_idx,
    std::uint32_t max_count,
    std::span<float> out_frames) const {
    if (!impl_->spectrum || max_count == 0u)
        return 0u;
    return impl_->spectrum->pull_frames(after_frame_idx, max_count, out_frames);
}

std::uint32_t Core::pull_packet_spectrogram(std::span<float> out,
                                            std::uint32_t n_time,
                                            std::uint32_t n_freq) const {
    if (n_time == 0u || n_freq == 0u) return 0u;
    if (out.size() < static_cast<std::size_t>(n_time) * n_freq) return 0u;

    // Snapshot metadata for the rolling IQ ring. The expensive data copy is
    // deferred until we know how much history is actually needed.
    std::vector<std::complex<float>> snap;
    std::size_t cap = 0;
    std::size_t ring_start = 0;
    std::size_t ring_filled = 0;
    std::uint64_t total_samples = 0;
    std::uint64_t packet_start = 0;
    std::uint64_t packet_end = 0;
    {
        std::lock_guard<std::mutex> lk(impl_->iq_mu);
        cap = impl_->iq_ring.size();
        ring_filled = impl_->iq_filled;
        if (cap == 0u || ring_filled < 64u) return 0u;
        total_samples = impl_->iq_total_samples;
        packet_start = impl_->last_packet_start;
        packet_end = impl_->last_packet_end;
        ring_start = (impl_->iq_pos + cap - ring_filled) % cap;
    }

    std::size_t filled = ring_filled;

    const std::uint64_t history_begin = total_samples >= ring_filled
        ? total_samples - ring_filled
        : 0u;

    constexpr std::size_t kFft = 512u;
    if (filled < kFft) return 0u;

    // Minimum window: preamble + sync/header. The energy locator below expands
    // this to the whole burst when enough history is available.
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
    const std::size_t min_window_symbols = static_cast<std::size_t>(preamble) + 12u;
    std::size_t window = std::min<std::size_t>(filled, min_window_symbols * sym_samples);
    if (window < kFft) window = kFft;

    // Auto-locate the packet by energy instead of relying on capture timing.
    // Find the highest-energy region of the ring and anchor the window so the
    // packet sits near the start. This is robust to event-drain latency that
    // would otherwise make a fixed "newest N ms" window miss the packet (and
    // snapshot post-packet noise instead).
    //
    // Crucially, measure energy *inside the LoRa channel only* (a narrow band
    // around DC). The radio is offset-tuned so the channel sits at DC and only
    // occupies bw/modem_rate of the captured spectrum. A plain wideband power
    // sum is dominated by out-of-band static/interference, which would make the
    // locator lock onto noise and snapshot static instead of the frame. A
    // per-block FFT restricted to the channel bins fixes that.
    std::size_t off0 = filled - window; // default: newest window
    constexpr std::size_t kBlk = 2048u;
    bool window_anchored = false;
    if (packet_end > packet_start && packet_end > history_begin) {
        const std::size_t exact_start = packet_start > history_begin
            ? static_cast<std::size_t>(packet_start - history_begin)
            : 0u;
        const std::size_t exact_end = static_cast<std::size_t>(
            std::min<std::uint64_t>(packet_end - history_begin, filled));
        const std::size_t exact_len = exact_end > exact_start ? exact_end - exact_start : window;
        // Keep the full packet visible start-to-finish instead of clipping to
        // the decoded interior. Add generous pre-roll (preamble + sync) and
        // post-roll (the modem reports end_sample_index when decoding finishes,
        // but the actual RF transmission continues for several more symbols:
        // CRC, padding, and tail).
        const std::size_t lead_syms = static_cast<std::size_t>(preamble) + 8u;
        const std::size_t lead_margin = std::min<std::size_t>(
            filled / 2u, lead_syms * sym_samples);
        // The modem's end_sample_index is where payload decoding finished, but
        // the actual packet continues for CRC (2-4 symbols) plus tail ramp-down.
        // Use 4 symbols of post-roll to ensure the full transmission is visible.
        const std::size_t tail_syms = 6u;
        const std::size_t tail_margin = std::min<std::size_t>(
            filled / 2u, tail_syms * sym_samples);
        window = std::min<std::size_t>(filled, exact_len + lead_margin + tail_margin);
        if (window < kFft) window = kFft;
        off0 = (exact_start > lead_margin) ? (exact_start - lead_margin) : 0u;
        if (off0 + window > filled) off0 = filled - window;

        snap.resize(window);
        {
            std::lock_guard<std::mutex> lk(impl_->iq_mu);
            if (impl_->iq_ring.size() != cap || impl_->iq_filled < ring_filled)
                return 0u;

            const std::size_t src0 = (ring_start + off0) % cap;
            const std::size_t first = std::min<std::size_t>(window, cap - src0);
            std::copy_n(impl_->iq_ring.begin() + src0, first, snap.begin());
            if (window > first) {
                std::copy_n(impl_->iq_ring.begin(), window - first,
                            snap.begin() + first);
            }
        }

        filled = window;
        off0 = 0u;
        window_anchored = true;
    }

    if (!window_anchored) {
        snap.resize(filled);
        {
            std::lock_guard<std::mutex> lk(impl_->iq_mu);
            if (impl_->iq_ring.size() != cap || impl_->iq_filled < ring_filled)
                return 0u;

            const std::size_t first = std::min<std::size_t>(filled, cap - ring_start);
            std::copy_n(impl_->iq_ring.begin() + ring_start, first, snap.begin());
            if (filled > first) {
                std::copy_n(impl_->iq_ring.begin(), filled - first,
                            snap.begin() + first);
            }
        }

        if (filled > window && filled >= kBlk) {
            const std::size_t nblk = filled / kBlk;

            // Channel half-width in FFT bins: (bw/2) / rate of the kBlk spectrum.
            const std::size_t chanHalf = std::clamp<std::size_t>(
                static_cast<std::size_t>(
                    0.5 * static_cast<double>(bw) / static_cast<double>(rate) * kBlk),
                1u, kBlk / 2u - 1u);

            dsp::Fft locFft(kBlk);
            std::vector<std::complex<float>> locBuf(kBlk);

            std::size_t peak_blk = 0;
            float peak_e = -1.0f;
            // Track the channel-power distribution so we can reject the case where
            // there is no real packet (every block is just noise) and so we can
            // find the *onset* of the burst rather than its single hottest block.
            std::vector<float> blk_e(nblk, 0.0f);
            for (std::size_t b = 0; b < nblk; ++b) {
                const std::size_t base = b * kBlk;
                for (std::size_t i = 0; i < kBlk; ++i) locBuf[i] = snap[base + i];
                locFft.forward(std::span<std::complex<float>>(locBuf.data(), kBlk));
                // Sum power in bins [-chanHalf, +chanHalf] around DC (bin 0).
                float e = 0.0f;
                for (std::size_t k = 0; k <= chanHalf; ++k) {
                    const auto lo = locBuf[k];
                    e += lo.real() * lo.real() + lo.imag() * lo.imag();
                    if (k != 0) {
                        const auto hi = locBuf[kBlk - k];
                        e += hi.real() * hi.real() + hi.imag() * hi.imag();
                    }
                }
                blk_e[b] = e;
                if (e > peak_e) { peak_e = e; peak_blk = b; }
            }

            // Channel noise floor (median of block energies).
            float median = peak_e;
            if (nblk >= 2u) {
                std::vector<float> sorted = blk_e;
                std::nth_element(sorted.begin(), sorted.begin() + sorted.size() / 2,
                                 sorted.end());
                median = sorted[sorted.size() / 2];
            }

            // A block counts as "in a burst" when it rises above the channel
            // noise floor. This function is called after CRC OK, so prefer
            // showing the best available region over rejecting a weak packet.
            const float burst_thr = std::max(
                median * 1.8f, median + (peak_e - median) * 0.35f);

            // Prefer the *most recent* burst, not the globally strongest one.
            // The snapshot is triggered by the packet that just decoded, which
            // sits at the newest end of the ring; another node's stronger packet
            // earlier in the ring must not steal the view. Scan from newest
            // block back to find the latest above threshold, then walk back to
            // the burst onset so preamble timing is captured.
            std::size_t burst_end = peak_blk;
            for (std::size_t b = nblk; b-- > 0;) {
                if (blk_e[b] >= burst_thr) { burst_end = b; break; }
            }
            std::size_t burst_start = burst_end;
            std::size_t quiet = 0;
            while (burst_start > 0) {
                if (blk_e[burst_start - 1] >= burst_thr) {
                    --burst_start;
                    quiet = 0;
                } else if (++quiet <= 2u) {
                    --burst_start;
                } else {
                    break;
                }
            }

            std::size_t burst_stop = burst_end;
            quiet = 0;
            while (burst_stop + 1u < nblk) {
                if (blk_e[burst_stop + 1u] >= burst_thr) {
                    ++burst_stop;
                    quiet = 0;
                } else if (++quiet <= 2u) {
                    ++burst_stop;
                } else {
                    break;
                }
            }

            const std::size_t burst_first = burst_start * kBlk;
            const std::size_t burst_last =
                std::min<std::size_t>((burst_stop + 1u) * kBlk, filled);
            const std::size_t burst_len =
                burst_last > burst_first ? burst_last - burst_first : window;
            const std::size_t margin =
                std::max<std::size_t>(sym_samples / 2u, burst_len / 100u);
            window = std::min<std::size_t>(filled,
                                           std::max(window, burst_len + 2u * margin));
            off0 = (burst_first > margin) ? burst_first - margin : 0u;
            if (off0 + window > filled) off0 = filled - window;
        }
    }

    // Fixed STFT hop (75% overlap of the kFft window) so the snapshot's time
    // resolution reflects the actual located packet length captured in the IQ
    // history rather than always being stretched/compressed to a fixed row
    // count. n_time is treated as the maximum the caller's buffer can hold.
    const std::size_t hop = std::max<std::size_t>(1u, kFft / 4u);

    std::uint32_t rows = (window > kFft)
        ? static_cast<std::uint32_t>((window - kFft) / hop + 1u)
        : 1u;
    if (rows > n_time) rows = n_time;
    if (rows < 1u) rows = 1u;

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
    for (std::uint32_t t = 0; t < rows; ++t) {
        const std::size_t base =
            off0 + std::min<std::size_t>(static_cast<std::size_t>(t) * hop,
                                         window - kFft);
        for (std::size_t i = 0; i < kFft; ++i)
            buf[i] = snap[base + i] * win[i];
        fft.forward(std::span<std::complex<float>>(buf.data(), kFft));

        // fftshift so DC lands at bin kFft/2, matching the live waterfall.
        for (std::size_t k = 0; k < kFft; ++k) {
            const std::size_t shifted = (k + half) % kFft;
            const auto v = buf[k] * norm;
            const float p = v.real() * v.real() + v.imag() * v.imag();
            fulldb[shifted] = (p > 1e-20f) ? 10.0f * std::log10(p) : -200.0f;
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

    return rows;
}

bool Core::set_rx_device(hal::DeviceKind kind) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->running) return false;
    impl_->rx_requested_kind = kind;
    // Release the current device BEFORE opening the new one. Otherwise the old
    // HackRF still holds its USB handle while open_device() calls hackrf_open()
    // for the new selection, so the second open hits the already-claimed device
    // and fails with rc=-5 (HACKRF_ERROR_NOT_FOUND). Closing first guarantees a
    // clean re-open even when re-selecting the same HackRF.
    if (impl_->tx_requested_kind == kind) impl_->tx_radio.reset();
    impl_->rx_radio.reset();

    if (kind == hal::DeviceKind::Sx1262) {
        // A hardware modem, not an SDR: no IQ device to open, and none of the
        // spectrum pipeline will run. sync_packet_radio() opens the stick (or
        // reuses the one TX already has).
        impl_->sync_packet_radio();
        impl_->rx_device_name = impl_->packet_radio_name();
        return true;
    }

    impl_->rx_radio = hal::open_device(kind);
    impl_->rx_device_name = impl_->rx_radio ? impl_->rx_radio->info().board_name : "(none)";
    // Releases the stick if TX is not using it either.
    impl_->sync_packet_radio();

    // The SX1262 stick is on its own USB device and shares nothing with the
    // SDR, so an RX change must leave TX alone — reopening it here would push
    // it through hal::open_device(), which has no IQ device to return.
    if (impl_->tx_requested_kind == hal::DeviceKind::Sx1262) return true;

    if (impl_->rx_radio && impl_->rx_radio->kind() == impl_->tx_requested_kind) {
        impl_->tx_radio.reset();
        impl_->tx_device_name = impl_->rx_device_name;
    } else if (!impl_->tx_radio) {
        impl_->tx_radio = hal::open_device(impl_->tx_requested_kind);
        impl_->tx_device_name = impl_->tx_radio ? impl_->tx_radio->info().board_name : "(none)";
    }
    return true;
}

bool Core::set_tx_device(hal::DeviceKind kind) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->running) return false;
    impl_->tx_requested_kind = kind;

    if (kind == hal::DeviceKind::Sx1262) {
        impl_->tx_radio.reset();
        impl_->sync_packet_radio();
        impl_->tx_device_name = impl_->packet_radio_name();
        return true;
    }

    // Releases the stick unless RX is still using it.
    impl_->sync_packet_radio();

    if (impl_->rx_radio && impl_->rx_radio->kind() == impl_->tx_requested_kind) {
        impl_->tx_radio.reset();
        impl_->tx_device_name = impl_->rx_device_name;
    } else {
        impl_->tx_radio.reset();
        impl_->tx_radio = hal::open_device(kind);
        impl_->tx_device_name = impl_->tx_radio ? impl_->tx_radio->info().board_name : "(none)";
    }
    return true;
}

void Core::set_sx1262_board(hal::Sx126xBoard board) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->sx1262_board == board) return;
    impl_->sx1262_board = board;
    // Bring the stored power inside the new board's range now that there is
    // one. Matters most on the first real selection, where the value carried
    // over from settings was never clamped.
    {
        std::int8_t lo = 0, hi = 0;
        hal::packet_radio_power_range(board, lo, hi);
        if (board != hal::Sx126xBoard::Unspecified)
            impl_->tx_power_dbm = std::clamp(impl_->tx_power_dbm, lo, hi);
    }
    // Re-open against the new profile if the stick is already in use, so the
    // power model changes without the user having to reselect the device.
    // Deliberately not gated on `running`: that flag means the SDR receiver is
    // streaming, and the stick is a separate USB device with nothing to do
    // with it. Gating here would leave an open transmitter on the old profile
    // — off by the MeshToad's 8 dB of PA gain — whenever RX happened to be up.
    if (impl_->rx_requested_kind == hal::DeviceKind::Sx1262 ||
        impl_->tx_requested_kind == hal::DeviceKind::Sx1262) {
        {
            std::lock_guard<std::mutex> burst(impl_->packet_radio_mu);
            impl_->packet_radio.reset(); // force a re-open on the new profile
        }
        impl_->sync_packet_radio();
        const std::string name = impl_->packet_radio_name();
        if (impl_->tx_requested_kind == hal::DeviceKind::Sx1262) impl_->tx_device_name = name;
        if (impl_->rx_requested_kind == hal::DeviceKind::Sx1262) impl_->rx_device_name = name;
    }
}

void Core::set_sx1262_serial(std::string_view serial) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->sx1262_serial == serial) return;
    if (impl_->running) return; // switching sticks mid-stream is not supported
    impl_->sx1262_serial.assign(serial);
    if (impl_->rx_requested_kind == hal::DeviceKind::Sx1262 ||
        impl_->tx_requested_kind == hal::DeviceKind::Sx1262) {
        {
            std::lock_guard<std::mutex> burst(impl_->packet_radio_mu);
            impl_->packet_radio.reset();
        }
        impl_->sync_packet_radio();
        const std::string name = impl_->packet_radio_name();
        if (impl_->tx_requested_kind == hal::DeviceKind::Sx1262) impl_->tx_device_name = name;
        if (impl_->rx_requested_kind == hal::DeviceKind::Sx1262) impl_->rx_device_name = name;
    }
}

std::string Core::sx1262_serial() const {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    return impl_->sx1262_serial;
}

std::vector<std::string> Core::list_sx1262_serials() const {
    // Enumeration claims each device in turn, so it cannot run while we hold
    // one open. Returning the connected stick's own serial keeps the picker
    // showing a valid selection instead of going empty mid-session.
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->packet_radio) {
        // The device's own serial, not the requested preference: with no
        // preference set the preference is empty, and returning that would
        // leave the picker blank while a stick is plainly connected.
        std::vector<std::string> only;
        const std::string serial = impl_->packet_radio->info().serial;
        if (!serial.empty()) only.push_back(serial);
        return only;
    }
    return hal::list_packet_radio_serials();
}

hal::Sx126xBoard Core::sx1262_board() const noexcept {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    return impl_->sx1262_board;
}

void Core::set_tx_power_dbm(std::int8_t dbm) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    // With no board chosen there is no range to clamp against, and clamping to
    // the placeholder's empty 0..0 would destroy a perfectly good saved value
    // before the user has picked. Store it; set_sx1262_board() clamps once a
    // real range exists.
    if (impl_->sx1262_board == hal::Sx126xBoard::Unspecified) {
        impl_->tx_power_dbm = dbm;
        return;
    }
    std::int8_t lo = 0, hi = 0;
    hal::packet_radio_power_range(impl_->sx1262_board, lo, hi);
    impl_->tx_power_dbm = std::clamp(dbm, lo, hi);
}

std::int8_t Core::tx_power_dbm() const noexcept {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    return impl_->tx_power_dbm;
}

void Core::tx_power_range_dbm(std::int8_t& min_dbm, std::int8_t& max_dbm) const noexcept {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    hal::packet_radio_power_range(impl_->sx1262_board, min_dbm, max_dbm);
}

void Core::set_tx_band_limits(std::uint64_t min_hz, std::uint64_t max_hz) {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    // Reversed edges would refuse every frequency, turning a caller's mistake
    // into a radio that silently cannot transmit. Normalize instead.
    if (min_hz > max_hz) std::swap(min_hz, max_hz);
    impl_->tx_band_min_hz = min_hz;
    impl_->tx_band_max_hz = max_hz;
}

void Core::tx_band_limits(std::uint64_t& min_hz, std::uint64_t& max_hz) const noexcept {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    min_hz = impl_->tx_band_min_hz;
    max_hz = impl_->tx_band_max_hz;
}

hal::DeviceKind Core::rx_device_kind() const noexcept {
    // rx_radio/tx_radio/*_requested_kind are all mutated under start_mu by
    // set_rx_device()/set_tx_device()/start_rx(); take the same lock here so
    // a concurrent reconfiguration can't be observed mid-update.
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    // The packet radio is not an IRadioDevice, so it never appears in
    // rx_radio; without this the UI sees "None" while the stick is receiving.
    if (impl_->rx_requested_kind == hal::DeviceKind::Sx1262 && impl_->packet_radio)
        return hal::DeviceKind::Sx1262;
    return impl_->rx_radio ? impl_->rx_radio->kind() : hal::DeviceKind::Null;
}

hal::DeviceKind Core::tx_device_kind() const noexcept {
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    if (impl_->packet_radio) return impl_->packet_radio->kind();
    const hal::IRadioDevice* tx =
        (!impl_->tx_radio && impl_->rx_radio &&
         impl_->rx_radio->kind() == impl_->tx_requested_kind)
            ? impl_->rx_radio.get()
            : impl_->tx_radio.get();
    return tx ? tx->kind() : hal::DeviceKind::Null;
}

bool Core::is_device_available(hal::DeviceKind kind) const noexcept {
    return hal::device_available(kind);
}

const char* Core::device_name() const noexcept {
    // impl_->rx_device_name is mutated under start_mu, and callers (the C ABI
    // in particular) read through the returned pointer via a separate
    // strlen()+copy pass after this function returns — a raw pointer into
    // that shared std::string would be a use-after-free/torn-read hazard if
    // a reconfiguration reassigned it in between. Snapshot into a
    // thread_local buffer under the lock instead: the returned pointer then
    // refers to storage only this calling thread can mutate.
    thread_local std::string cache;
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    cache = impl_->rx_device_name;
    return cache.c_str();
}

const char* Core::tx_device_name() const noexcept {
    thread_local std::string cache;
    std::lock_guard<std::mutex> lk(impl_->start_mu);
    cache = impl_->tx_device_name;
    return cache.c_str();
}

const char* Core::device_status() const noexcept {
    return hal::open_default_device_status();
}

std::size_t Core::pull_event(std::span<char> out) noexcept {
    std::lock_guard<std::mutex> lk(impl_->events_mu);
    if (out.empty()) return 0;

    // Served ahead of the queue, and formatted into the caller's buffer
    // rather than built as a string: this function is noexcept, and an
    // allocation here would end the process on the one path that exists to
    // report running out of room. Reports what was lost since the last
    // notice, where the sample counter above reports a running total.
    if (impl_->events_dropped != 0) {
        int n = std::snprintf(out.data(), out.size(),
                              "WARNING: dropped %llu log events and shed %llu diagnostic lines (UI overrun)",
                              static_cast<unsigned long long>(impl_->events_dropped),
                              static_cast<unsigned long long>(impl_->diagnostics_shed));
        impl_->events_dropped = 0;
        impl_->diagnostics_shed = 0;
        if (n < 0) return 0;
        return std::min(static_cast<std::size_t>(n), out.size() - 1);
    }
    // Shedding alone is not an overrun: nothing the app acts on was lost.
    // Still worth one line, since the decode diagnostics for that stretch
    // are missing from the log and a reader looking for them should know why.
    if (impl_->diagnostics_shed != 0) {
        int n = std::snprintf(out.data(), out.size(),
                              "note: shed %llu diagnostic lines while the log queue was over half full",
                              static_cast<unsigned long long>(impl_->diagnostics_shed));
        impl_->diagnostics_shed = 0;
        if (n < 0) return 0;
        return std::min(static_cast<std::size_t>(n), out.size() - 1);
    }

    if (impl_->events.empty()) return 0;
    const auto& front = impl_->events.front();
    std::size_t n = std::min(front.size(), out.size() - 1);
    std::memcpy(out.data(), front.data(), n);
    out[n] = '\0';
    impl_->events.pop_front();
    return n;
}

CoreSignalStats Core::signal_stats() const noexcept {
    // On the hardware-modem path there is no IQ to take statistics from, so
    // report the radio's own per-packet RSSI instead. It is in dBm rather than
    // dBFS, which is what the app already treats this field as when attributing
    // a packet's signal strength.
    if (impl_->have_packet_rssi.load(std::memory_order_relaxed)) {
        const float rssi = impl_->packet_rssi_dbm.load(std::memory_order_relaxed);
        return CoreSignalStats{rssi, rssi, 0.0f, 0.0f, 0};
    }
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
