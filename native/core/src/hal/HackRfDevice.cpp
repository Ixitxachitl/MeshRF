// SPDX-License-Identifier: GPL-3.0-or-later
//
// HackRF One implementation of IRadioDevice. Loads libhackrf at runtime via
// HackRfDynLoad — no build-time dependency on hackrf.h or hackrf.lib. When
// the DLL can't be found we fall back to a synthetic NullDevice so the rest
// of the stack works without hardware.
#include "mrf/hal/RadioDevice.h"
#include "HackRfDynLoad.h"

#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <numbers>
#include <random>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace mrf::hal {

// Defined in RtlSdrDevice.cpp. Declared here so open_device() can dispatch to
// the RTL-SDR backend without a header dependency on the RTL loader.
std::unique_ptr<IRadioDevice> open_rtlsdr_device(std::string& status);
bool rtlsdr_backend_available();

namespace {
std::string g_open_status = "not attempted";
} // namespace

const char* open_default_device_status() { return g_open_status.c_str(); }

namespace {

// ---------------------------------------------------------------------------
// Synthetic NullDevice fallback. Emits white noise + a pilot tone so the WPF
// spectrum view animates when no hardware is wired in.
// ---------------------------------------------------------------------------
class NullDevice final : public IRadioDevice {
public:
    DeviceInfo info() const override { return DeviceInfo{"", "null-synth", 0}; }

    void start_rx(const RxConfig& cfg, RxCallback cb) override {
        if (rx_running_) return;
        rx_cb_ = std::move(cb);
        sample_rate_ = cfg.sample_rate_hz == 0 ? 2'000'000u : cfg.sample_rate_hz;
        rx_running_ = true;
        rx_thread_ = std::thread(&NullDevice::rx_loop, this);
    }
    void stop_rx() override {
        rx_running_ = false;
        if (rx_thread_.joinable()) rx_thread_.join();
        rx_cb_ = {};
    }
    void start_tx(const TxConfig&, TxCallback) override { tx_running_ = true; }
    void stop_tx() override { tx_running_ = false; }
    bool is_rx_running() const override { return rx_running_; }
    bool is_tx_running() const override { return tx_running_; }

private:
    void rx_loop() {
        constexpr std::size_t kChunk = 16384;
        std::vector<SampleType> buf(kChunk);
        std::mt19937 rng{0xC0FFEEu};
        std::normal_distribution<float> nd(0.0f, 0.05f);
        const float two_pi = 2.0f * std::numbers::pi_v<float>;
        const float dphase = two_pi * 0.10f;
        const float tone_amp = 0.20f;
        float phase = 0.0f;

        const auto chunk_us = std::chrono::microseconds{
            static_cast<long long>(1e6 * static_cast<double>(kChunk) /
                                   static_cast<double>(sample_rate_))};

        while (rx_running_) {
            for (std::size_t i = 0; i < kChunk; ++i) {
                buf[i] = SampleType(nd(rng) + tone_amp * std::cos(phase),
                                    nd(rng) + tone_amp * std::sin(phase));
                phase += dphase;
                if (phase > two_pi) phase -= two_pi;
            }
            if (rx_cb_) rx_cb_(buf.data(), buf.size());
            std::this_thread::sleep_for(chunk_us);
        }
    }

    std::atomic<bool> rx_running_{false};
    std::atomic<bool> tx_running_{false};
    std::thread rx_thread_;
    RxCallback rx_cb_;
    std::uint32_t sample_rate_{2'000'000u};
};

// ---------------------------------------------------------------------------
// Real HackRF One via runtime-loaded libhackrf.
// ---------------------------------------------------------------------------
class HackRfDevice final : public IRadioDevice {
public:
    explicit HackRfDevice(const hackrf_dyn::Api& api) : api_(api) {
        const int rc_init = api_.hackrf_init();
        if (rc_init != hackrf_dyn::HACKRF_SUCCESS)
            throw std::runtime_error("hackrf_init rc=" + std::to_string(rc_init));
        const int rc_open = api_.hackrf_open(&dev_);
        if (rc_open != hackrf_dyn::HACKRF_SUCCESS) {
            api_.hackrf_exit();
            throw std::runtime_error("hackrf_open rc=" + std::to_string(rc_open) +
                                     " (device unplugged or WinUSB driver not bound \u2014 run Zadig?)");
        }        // Workaround: the first hackrf_start_rx after open frequently delivers
        // no samples on Windows even though it returns success. Prime the
        // device with a quick start/stop so the user's first real start
        // actually streams. SDRangel/gqrx avoid this by always pre-configuring
        // sample_rate + baseband_filter, but we've seen it bite even with that
        // ordering on some firmware revisions.
        prime_();    }
    ~HackRfDevice() override {
        stop_rx();
        if (tx_running_) api_.hackrf_stop_tx(dev_);
        if (dev_) api_.hackrf_close(dev_);
        api_.hackrf_exit();
    }

    DeviceInfo info() const override { return DeviceInfo{"", "HackRF One", 0}; }
    DeviceKind kind() const override { return DeviceKind::HackRf; }

    void start_rx(const RxConfig& cfg, RxCallback cb) override {
        rx_cb_ = std::move(cb);
        // Decouple the USB transfer callback from the heavy DSP+modem
        // pipeline: the libusb callback (on_rx) only converts samples and
        // pushes them into ring_, then returns immediately. A worker thread
        // (rx_worker_loop) drains the ring and runs rx_cb_. Running the whole
        // pipeline inline in the callback at 2.4 MS/s stalls libusb and drops
        // sample blocks, corrupting frames mid-payload. This matches how
        // SDRangel/gqrx buffer the HackRF stream before processing.
        ring_.assign(kRingCapacity, SampleType{0.0f, 0.0f});
        ring_rpos_ = ring_wpos_ = ring_count_ = 0;
        ring_drops_ = 0;
        worker_run_ = true;
        rx_worker_ = std::thread(&HackRfDevice::rx_worker_loop, this);
        // Order matches SDRangel/gqrx: sample_rate -> baseband_bw -> freq ->
        // gains -> start_rx. The baseband filter call is REQUIRED — without
        // it the first hackrf_start_rx after open often delivers no samples.
        check(api_.hackrf_set_sample_rate(dev_, static_cast<double>(cfg.sample_rate_hz)),
              "hackrf_set_sample_rate");
        const std::uint32_t bw =
            api_.hackrf_compute_baseband_filter_bw_round_down_lt(cfg.sample_rate_hz);
        check(api_.hackrf_set_baseband_filter_bandwidth(dev_, bw),
              "hackrf_set_baseband_filter_bandwidth");
        check(api_.hackrf_set_freq(dev_, cfg.center_freq_hz), "hackrf_set_freq");
        check(api_.hackrf_set_lna_gain(dev_, cfg.lna_gain_db), "hackrf_set_lna_gain");
        check(api_.hackrf_set_vga_gain(dev_, cfg.vga_gain_db), "hackrf_set_vga_gain");
        check(api_.hackrf_set_amp_enable(dev_, cfg.amp_enable ? 1 : 0),
              "hackrf_set_amp_enable");
        check(api_.hackrf_start_rx(dev_, &HackRfDevice::rx_thunk, this),
              "hackrf_start_rx");
        rx_running_ = true;
    }
    void stop_rx() override {
        if (rx_running_) {
            api_.hackrf_stop_rx(dev_);
            rx_running_ = false;
        }
        // Tear down the worker thread (no-op if never started).
        if (worker_run_) {
            {
                std::lock_guard<std::mutex> lk(ring_mu_);
                worker_run_ = false;
            }
            ring_cv_.notify_all();
            if (rx_worker_.joinable()) rx_worker_.join();
        }
    }

    void start_tx(const TxConfig& cfg, TxCallback cb) override {
        tx_cb_ = std::move(cb);
        check(api_.hackrf_set_sample_rate(dev_, static_cast<double>(cfg.sample_rate_hz)),
              "hackrf_set_sample_rate");
        const std::uint32_t bw =
            api_.hackrf_compute_baseband_filter_bw_round_down_lt(cfg.sample_rate_hz);
        check(api_.hackrf_set_baseband_filter_bandwidth(dev_, bw),
              "hackrf_set_baseband_filter_bandwidth");
        check(api_.hackrf_set_freq(dev_, cfg.center_freq_hz), "hackrf_set_freq");
        check(api_.hackrf_set_txvga_gain(dev_, cfg.txvga_gain_db),
              "hackrf_set_txvga_gain");
        check(api_.hackrf_set_amp_enable(dev_, cfg.amp_enable ? 1 : 0),
              "hackrf_set_amp_enable");
        check(api_.hackrf_start_tx(dev_, &HackRfDevice::tx_thunk, this),
              "hackrf_start_tx");
        tx_running_ = true;
    }
    void stop_tx() override {
        if (tx_running_) {
            api_.hackrf_stop_tx(dev_);
            tx_running_ = false;
        }
    }

    bool is_rx_running() const override { return rx_running_; }
    bool is_tx_running() const override { return tx_running_; }

    std::uint64_t dropped_samples() const override { return ring_drops_; }

    void set_rx_gains(std::uint8_t lna, std::uint8_t vga, bool amp) override {
        if (!dev_) return;
        api_.hackrf_set_lna_gain(dev_, lna);
        api_.hackrf_set_vga_gain(dev_, vga);
        api_.hackrf_set_amp_enable(dev_, amp ? 1 : 0);
    }

private:
    static void check(int rc, const char* what) {
        if (rc != hackrf_dyn::HACKRF_SUCCESS)
            throw std::runtime_error(std::string(what) + " rc=" + std::to_string(rc));
    }

    // Prime the USB pipe by briefly starting + stopping RX with throwaway
    // settings. See ctor comment.
    void prime_() {
        constexpr std::uint32_t kPrimeRate = 8'000'000u; // any legal rate works
        api_.hackrf_set_sample_rate(dev_, static_cast<double>(kPrimeRate));
        const std::uint32_t bw =
            api_.hackrf_compute_baseband_filter_bw_round_down_lt(kPrimeRate);
        api_.hackrf_set_baseband_filter_bandwidth(dev_, bw);
        api_.hackrf_set_freq(dev_, 915'000'000ull);
        api_.hackrf_set_lna_gain(dev_, 0);
        api_.hackrf_set_vga_gain(dev_, 0);
        api_.hackrf_set_amp_enable(dev_, 0);
        if (api_.hackrf_start_rx(dev_, &HackRfDevice::prime_thunk, this) ==
            hackrf_dyn::HACKRF_SUCCESS) {
            std::this_thread::sleep_for(std::chrono::milliseconds(50));
            api_.hackrf_stop_rx(dev_);
            // Give the firmware a moment to fully settle the endpoints.
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
    }
    static int prime_thunk(hackrf_dyn::hackrf_transfer*) { return 0; }

    static int rx_thunk(hackrf_dyn::hackrf_transfer* t) {
        auto* self = static_cast<HackRfDevice*>(t->rx_ctx);
        return self->on_rx(t);
    }
    static int tx_thunk(hackrf_dyn::hackrf_transfer* t) {
        auto* self = static_cast<HackRfDevice*>(t->tx_ctx);
        return self->on_tx(t);
    }

    int on_rx(hackrf_dyn::hackrf_transfer* t) {
        // Runs in the libusb transfer-callback thread: do the minimum work
        // (int8 -> float conversion + a memcpy into the ring) and return
        // immediately so libusb can resubmit transfers without dropping
        // sample blocks. The heavy DSP runs in rx_worker_loop.
        const std::size_t n = static_cast<std::size_t>(t->valid_length) / 2;
        if (scratch_.size() < n) scratch_.resize(n);
        const auto* src = reinterpret_cast<const std::int8_t*>(t->buffer);
        for (std::size_t i = 0; i < n; ++i) {
            scratch_[i] = SampleType(src[2 * i]     / 128.0f,
                                     src[2 * i + 1] / 128.0f);
        }
        {
            std::lock_guard<std::mutex> lk(ring_mu_);
            const std::size_t freespace = kRingCapacity - ring_count_;
            const std::size_t take = std::min(n, freespace);
            for (std::size_t i = 0; i < take; ++i) {
                ring_[ring_wpos_] = scratch_[i];
                ring_wpos_ = (ring_wpos_ + 1) % kRingCapacity;
            }
            ring_count_ += take;
            if (take < n) ring_drops_ += (n - take); // ring overflow (consumer too slow)
        }
        ring_cv_.notify_one();
        return 0;
    }

    void rx_worker_loop() {
        // Process the ring in fixed-size chunks so the downstream DSP (and the
        // spectrum/waterfall it feeds) advances at a steady cadence. Draining
        // the whole ring in one giant rx_cb_ call made the UI update in bursts
        // (freeze, then jump) — the periodic stutter we saw. kWorkerChunk is
        // close to a typical HackRF transfer so the cadence matches the stream.
        constexpr std::size_t kWorkerChunk = 32768;
        std::vector<SampleType> batch(kWorkerChunk);
        while (true) {
            std::size_t got;
            {
                std::unique_lock<std::mutex> lk(ring_mu_);
                ring_cv_.wait(lk, [&] {
                    return ring_count_ >= kWorkerChunk || !worker_run_;
                });
                // On shutdown, flush whatever remains (smaller than a chunk).
                if (!worker_run_ && ring_count_ < kWorkerChunk) {
                    got = ring_count_;
                } else {
                    got = std::min(ring_count_, kWorkerChunk);
                }
                if (got == 0) return; // shutdown with empty ring
                for (std::size_t i = 0; i < got; ++i) {
                    batch[i] = ring_[ring_rpos_];
                    ring_rpos_ = (ring_rpos_ + 1) % kRingCapacity;
                }
                ring_count_ -= got;
            }
            if (rx_cb_) rx_cb_(batch.data(), got);
        }
    }

    int on_tx(hackrf_dyn::hackrf_transfer* t) {
        const std::size_t cap = static_cast<std::size_t>(t->valid_length) / 2;
        if (scratch_.size() < cap) scratch_.resize(cap);
        const std::size_t produced = tx_cb_ ? tx_cb_(scratch_.data(), cap) : 0;
        auto* dst = reinterpret_cast<std::int8_t*>(t->buffer);
        for (std::size_t i = 0; i < produced; ++i) {
            dst[2 * i]     = static_cast<std::int8_t>(scratch_[i].real() * 127.0f);
            dst[2 * i + 1] = static_cast<std::int8_t>(scratch_[i].imag() * 127.0f);
        }
        for (std::size_t i = produced; i < cap; ++i) {
            dst[2 * i] = 0;
            dst[2 * i + 1] = 0;
        }
        return produced == 0 ? -1 : 0;
    }

    hackrf_dyn::Api api_;
    hackrf_dyn::hackrf_device* dev_{nullptr};
    RxCallback rx_cb_;
    TxCallback tx_cb_;
    std::vector<SampleType> scratch_;
    std::atomic<bool> rx_running_{false};
    std::atomic<bool> tx_running_{false};

    // Producer (USB callback) -> consumer (rx_worker_) decoupling ring.
    // ~1.7 s of slack at 2.4 MS/s absorbs scheduling jitter without dropping.
    static constexpr std::size_t kRingCapacity = 4u * 1024u * 1024u;
    std::vector<SampleType> ring_;
    std::size_t ring_rpos_{0};
    std::size_t ring_wpos_{0};
    std::size_t ring_count_{0};
    std::atomic<std::uint64_t> ring_drops_{0};
    std::mutex ring_mu_;
    std::condition_variable ring_cv_;
    std::thread rx_worker_;
    std::atomic<bool> worker_run_{false};
};

} // namespace

namespace {

// Try to open the HackRF backend. Returns nullptr (and sets g_open_status) if
// libhackrf is missing or no device opened.
std::unique_ptr<IRadioDevice> try_open_hackrf() {
    hackrf_dyn::Api api{};
    if (hackrf_dyn::load(api)) {
        try {
            auto dev = std::make_unique<HackRfDevice>(api);
            g_open_status = std::string("HackRF open OK \u2014 ") +
                            hackrf_dyn::last_status();
            return dev;
        } catch (const std::exception& e) {
            g_open_status = std::string("HackRF detected but open failed: ") +
                            e.what() + " (loader: " + hackrf_dyn::last_status() + ")";
        }
    } else {
        g_open_status = std::string("libhackrf load failed: ") +
                        hackrf_dyn::last_status();
    }
    return nullptr;
}

std::unique_ptr<IRadioDevice> try_open_rtlsdr() {
    std::string status;
    auto dev = open_rtlsdr_device(status);
    g_open_status = status;
    return dev; // may be nullptr
}

} // namespace

std::unique_ptr<IRadioDevice> open_device(DeviceKind kind) {
    switch (kind) {
        case DeviceKind::HackRf:
            if (auto d = try_open_hackrf()) return d;
            return std::make_unique<NullDevice>();
        case DeviceKind::RtlSdr:
            if (auto d = try_open_rtlsdr()) return d;
            return std::make_unique<NullDevice>();
        case DeviceKind::Null:
            g_open_status = "Synthetic NullDevice selected (no hardware)";
            return std::make_unique<NullDevice>();
        case DeviceKind::Auto:
        default: {
            if (auto d = try_open_hackrf()) return d;
            const std::string hackrf_why = g_open_status;
            if (auto d = try_open_rtlsdr()) return d;
            g_open_status = "No SDR found \u2014 " + hackrf_why +
                            "; " + g_open_status + "; using synthetic NullDevice";
            return std::make_unique<NullDevice>();
        }
    }
}

std::unique_ptr<IRadioDevice> open_default_device() {
    return open_device(DeviceKind::Auto);
}

bool device_available(DeviceKind kind) {
    switch (kind) {
        case DeviceKind::HackRf: {
            hackrf_dyn::Api api{};
            return hackrf_dyn::load(api);
        }
        case DeviceKind::RtlSdr:
            return rtlsdr_backend_available();
        case DeviceKind::Null:
        case DeviceKind::Auto:
        default:
            return true;
    }
}

} // namespace mrf::hal
