// SPDX-License-Identifier: GPL-3.0-or-later
//
// RTL-SDR (RTL2832U) implementation of IRadioDevice. Loads librtlsdr at
// runtime via RtlSdrDynLoad — no build-time dependency on rtl-sdr.h. The
// dongle delivers interleaved unsigned-8-bit I/Q via a blocking async reader,
// so (like the HackRF backend) we run rtlsdr_read_async on a dedicated thread,
// convert + push into a decoupling ring in the USB callback, and drain the
// ring from a worker thread that runs the heavy DSP pipeline.
#include "mrf/hal/RadioDevice.h"
#include "RtlSdrDynLoad.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace mrf::hal {
namespace {

// Snap a requested gain (dB) to the nearest tuner gain the device supports.
// Returns the chosen value in tenths of a dB (librtlsdr's unit), or 0 if the
// supported-gains query is unavailable.
int nearest_supported_gain(const rtlsdr_dyn::Api& api,
                           rtlsdr_dyn::rtlsdr_dev* dev,
                           int requested_db) {
    if (!api.rtlsdr_get_tuner_gains) return requested_db * 10;
    const int count = api.rtlsdr_get_tuner_gains(dev, nullptr);
    if (count <= 0) return requested_db * 10;
    std::vector<int> gains(static_cast<std::size_t>(count));
    if (api.rtlsdr_get_tuner_gains(dev, gains.data()) <= 0)
        return requested_db * 10;
    const int target = requested_db * 10; // tenths of dB
    int best = gains[0];
    int best_err = std::abs(gains[0] - target);
    for (int g : gains) {
        const int err = std::abs(g - target);
        if (err < best_err) { best_err = err; best = g; }
    }
    return best;
}

class RtlSdrDevice final : public IRadioDevice {
public:
    RtlSdrDevice(const rtlsdr_dyn::Api& api, std::string name)
        : api_(api), board_name_(std::move(name)) {
        if (api_.rtlsdr_get_device_count() == 0)
            throw std::runtime_error("no RTL-SDR device connected");
        const int rc = api_.rtlsdr_open(&dev_, 0);
        if (rc != rtlsdr_dyn::RTLSDR_SUCCESS || !dev_)
            throw std::runtime_error("rtlsdr_open rc=" + std::to_string(rc) +
                                     " (device busy or WinUSB driver not bound \u2014 run Zadig?)");
        if (const char* ppm = std::getenv("MRF_RTLSDR_PPM");
            ppm && *ppm && api_.rtlsdr_set_freq_correction) {
            api_.rtlsdr_set_freq_correction(dev_, std::atoi(ppm));
        }
    }

    ~RtlSdrDevice() override {
        stop_rx();
        if (dev_) api_.rtlsdr_close(dev_);
    }

    DeviceInfo info() const override { return DeviceInfo{"", board_name_, 0}; }
    DeviceKind kind() const override { return DeviceKind::RtlSdr; }

    void start_rx(const RxConfig& cfg, RxCallback cb) override {
        if (rx_running_) return;
        rx_cb_ = std::move(cb);

        check(api_.rtlsdr_set_sample_rate(dev_, cfg.sample_rate_hz),
              "rtlsdr_set_sample_rate");
        check(api_.rtlsdr_set_center_freq(
                  dev_, static_cast<std::uint32_t>(cfg.center_freq_hz)),
              "rtlsdr_set_center_freq");
        apply_gains(cfg.lna_gain_db, cfg.vga_gain_db, cfg.amp_enable);
        apply_bias_tee(bias_tee_);
        api_.rtlsdr_reset_buffer(dev_);

        ring_.assign(kRingCapacity, SampleType{0.0f, 0.0f});
        ring_rpos_ = ring_wpos_ = ring_count_ = 0;
        ring_drops_ = 0;
        worker_run_ = true;
        rx_worker_ = std::thread(&RtlSdrDevice::rx_worker_loop, this);

        rx_running_ = true;
        // rtlsdr_read_async blocks until cancel_async; run it on its own thread.
        async_thread_ = std::thread([this] {
            // 0,0 => library default buffer count/length (15 x 256 KB).
            api_.rtlsdr_read_async(dev_, &RtlSdrDevice::rx_thunk, this, 0, 0);
        });
    }

    void stop_rx() override {
        if (rx_running_) {
            api_.rtlsdr_cancel_async(dev_);
            rx_running_ = false;
            if (async_thread_.joinable()) async_thread_.join();
        }
        if (worker_run_) {
            {
                std::lock_guard<std::mutex> lk(ring_mu_);
                worker_run_ = false;
            }
            ring_cv_.notify_all();
            if (rx_worker_.joinable()) rx_worker_.join();
        }
    }

    // RTL-SDR is receive-only.
    void start_tx(const TxConfig&, TxCallback) override {
        throw std::runtime_error("RTL-SDR does not support transmit");
    }
    void stop_tx() override {}

    bool is_rx_running() const override { return rx_running_; }
    bool is_tx_running() const override { return false; }

    std::uint64_t dropped_samples() const override { return ring_drops_; }

    void set_rx_gains(std::uint8_t lna, std::uint8_t vga, bool amp) override {
        if (!dev_) return;
        apply_gains(lna, vga, amp);
    }

    // RTL-SDR specific toggle: "bias_tee" (5 V bias-T on the antenna port).
    // Unknown keys are ignored.
    void set_rx_option(std::string_view key, int value) override {
        if (key == "bias_tee") {
            bias_tee_ = (value != 0);
            apply_bias_tee(bias_tee_);
        }
    }

private:
    static void check(int rc, const char* what) {
        if (rc != rtlsdr_dyn::RTLSDR_SUCCESS)
            throw std::runtime_error(std::string(what) + " rc=" + std::to_string(rc));
    }

    // Map the shared gain controls onto the RTL-SDR's single tuner gain.
    // `amp` selects the tuner's automatic-gain mode (the "AGC" toggle in the
    // UI); otherwise the tuner gain is set manually to the nearest supported
    // value to the requested gain (lna + a fraction of vga).
    void apply_gains(std::uint8_t lna, std::uint8_t vga, bool amp) {
        if (!dev_) return;
        if (amp) {
            api_.rtlsdr_set_tuner_gain_mode(dev_, 0); // 0 = automatic tuner gain
            return;
        }
        api_.rtlsdr_set_tuner_gain_mode(dev_, 1); // 1 = manual
        const int requested_db = static_cast<int>(lna) + static_cast<int>(vga) / 4;
        const int tenths = nearest_supported_gain(api_, dev_, requested_db);
        api_.rtlsdr_set_tuner_gain(dev_, tenths);
    }

    // 5 V bias-T on the antenna port (for powering LNAs/active antennas).
    void apply_bias_tee(bool on) {
        if (dev_ && api_.rtlsdr_set_bias_tee)
            api_.rtlsdr_set_bias_tee(dev_, on ? 1 : 0);
    }

    static void rx_thunk(unsigned char* buf, std::uint32_t len, void* ctx) {
        static_cast<RtlSdrDevice*>(ctx)->on_rx(buf, len);
    }

    void on_rx(const unsigned char* buf, std::uint32_t len) {
        // Unsigned-8 samples are centered at 127.5. Convert to [-1, 1) float.
        const std::size_t n = len / 2;
        if (scratch_.size() < n) scratch_.resize(n);
        constexpr float kScale = 1.0f / 127.5f;
        for (std::size_t i = 0; i < n; ++i) {
            scratch_[i] = SampleType((buf[2 * i]     - 127.5f) * kScale,
                                     (buf[2 * i + 1] - 127.5f) * kScale);
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
            if (take < n) ring_drops_ += (n - take);
        }
        ring_cv_.notify_one();
    }

    void rx_worker_loop() {
        constexpr std::size_t kWorkerChunk = 32768;
        std::vector<SampleType> batch(kWorkerChunk);
        while (true) {
            std::size_t got;
            {
                std::unique_lock<std::mutex> lk(ring_mu_);
                ring_cv_.wait(lk, [&] {
                    return ring_count_ >= kWorkerChunk || !worker_run_;
                });
                if (!worker_run_ && ring_count_ < kWorkerChunk) {
                    got = ring_count_;
                } else {
                    got = std::min(ring_count_, kWorkerChunk);
                }
                if (got == 0) return;
                for (std::size_t i = 0; i < got; ++i) {
                    batch[i] = ring_[ring_rpos_];
                    ring_rpos_ = (ring_rpos_ + 1) % kRingCapacity;
                }
                ring_count_ -= got;
            }
            if (rx_cb_) rx_cb_(batch.data(), got);
        }
    }

    rtlsdr_dyn::Api api_;
    rtlsdr_dyn::rtlsdr_dev* dev_{nullptr};
    std::string board_name_;
    RxCallback rx_cb_;
    std::vector<SampleType> scratch_;
    std::atomic<bool> rx_running_{false};
    std::thread async_thread_;
    bool bias_tee_{false};

    // Producer (USB callback) -> consumer (rx_worker_) decoupling ring.
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

// Exposed to HackRfDevice.cpp's open_device() dispatcher. Returns a working
// RtlSdrDevice or nullptr (with `status` describing why).
std::unique_ptr<IRadioDevice> open_rtlsdr_device(std::string& status) {
    rtlsdr_dyn::Api api{};
    if (!rtlsdr_dyn::load(api)) {
        status = std::string("librtlsdr load failed: ") + rtlsdr_dyn::last_status();
        return nullptr;
    }
    std::string name = "RTL-SDR";
    if (api.rtlsdr_get_device_name && api.rtlsdr_get_device_count() > 0) {
        if (const char* dn = api.rtlsdr_get_device_name(0); dn && *dn)
            name = std::string("RTL-SDR: ") + dn;
    }
    try {
        auto dev = std::make_unique<RtlSdrDevice>(api, name);
        status = std::string("RTL-SDR open OK \u2014 ") + rtlsdr_dyn::last_status();
        return dev;
    } catch (const std::exception& e) {
        status = std::string("RTL-SDR detected but open failed: ") + e.what() +
                 " (loader: " + rtlsdr_dyn::last_status() + ")";
        return nullptr;
    }
}

// True if librtlsdr can be loaded (driver present). Does not require a device.
bool rtlsdr_backend_available() {
    rtlsdr_dyn::Api api{};
    return rtlsdr_dyn::load(api);
}

} // namespace mrf::hal
