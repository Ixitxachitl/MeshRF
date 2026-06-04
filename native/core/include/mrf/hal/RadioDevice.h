// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <cstdint>
#include <cstddef>
#include <complex>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace mrf::hal {

using SampleType = std::complex<float>;

struct DeviceInfo {
    std::string serial;
    std::string board_name;
    std::uint32_t firmware_version{};
};

struct RxConfig {
    std::uint64_t center_freq_hz{915'000'000};
    std::uint32_t sample_rate_hz{2'000'000};
    std::uint8_t  lna_gain_db{24}; // 0..40 step 8
    std::uint8_t  vga_gain_db{20}; // 0..62 step 2
    bool          amp_enable{false};
};

struct TxConfig {
    std::uint64_t center_freq_hz{915'000'000};
    std::uint32_t sample_rate_hz{2'000'000};
    std::uint8_t  txvga_gain_db{0}; // 0..47 step 1
    bool          amp_enable{false};
};

// Callback delivered with a contiguous block of complex<float> IQ samples.
// The buffer is only valid for the duration of the callback.
using RxCallback = std::function<void(const SampleType* samples, std::size_t count)>;

// Callback asked to fill the buffer for transmission. Return number of
// samples actually written; returning 0 ends the TX stream.
using TxCallback = std::function<std::size_t(SampleType* out, std::size_t capacity)>;

// Abstract radio device. Implementations: HackRfDevice (real), NullDevice (test).
class IRadioDevice {
public:
    virtual ~IRadioDevice() = default;

    virtual DeviceInfo info() const = 0;

    virtual void start_rx(const RxConfig& cfg, RxCallback cb) = 0;
    virtual void stop_rx() = 0;

    virtual void start_tx(const TxConfig& cfg, TxCallback cb) = 0;
    virtual void stop_tx() = 0;

    virtual bool is_rx_running() const = 0;
    virtual bool is_tx_running() const = 0;

    // Live gain update. Default implementation no-ops; HackRF override applies
    // to the running stream. Values are clamped to HackRF ranges by callers.
    virtual void set_rx_gains(std::uint8_t /*lna_db*/,
                              std::uint8_t /*vga_db*/,
                              bool         /*amp*/) {}

    // Total RX samples dropped since start_rx because the processing consumer
    // could not keep up (ring-buffer overflow). 0 on backends that never drop.
    virtual std::uint64_t dropped_samples() const { return 0; }
};

// Factory: returns a HackRfDevice if libhackrf is available and a device is
// connected; otherwise returns a NullDevice that produces zero samples (so
// higher layers can still be exercised in tests / dev without hardware).
std::unique_ptr<IRadioDevice> open_default_device();

// Human-readable diagnostic from the most recent open_default_device() call,
// e.g. "HackRF open OK" or "libhackrf load failed: hackrf.dll not found".
const char* open_default_device_status();

} // namespace mrf::hal
