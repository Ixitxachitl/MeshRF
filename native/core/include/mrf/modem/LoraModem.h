// SPDX-License-Identifier: GPL-3.0-or-later
//
// LoRa modem facade. The actual DSP will be ported from gr-lora_sdr in
// Phase 2; this header pins the public interface so dependent layers can be
// developed in parallel.

#pragma once

#include "mrf/modem/Preset.h"

#include <complex>
#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace mrf::modem {

using Sample = std::complex<float>;

struct DecodedFrame {
    std::vector<std::uint8_t> payload; // bytes after header CRC ok
    float snr_db{};
    float rssi_dbm{};
    std::uint64_t sample_index{};      // start sample of detected preamble
};

// Callback invoked when a frame is successfully demodulated.
using FrameCallback = std::function<void(const DecodedFrame&)>;

// Callback invoked for human-readable demodulator events (preamble detected,
// header parsed, CRC failure, etc). UTF-8.
using EventCallback = std::function<void(std::string)>;

class ILoraModem {
public:
    virtual ~ILoraModem() = default;

    // Streaming RX: feed IQ at the modem's working sample rate. Decoded
    // frames are delivered via set_frame_callback(); pre-decode events
    // (preamble lock etc.) are delivered via set_event_callback().
    virtual void process_rx(std::span<const Sample> samples) = 0;

    // Synchronous TX: encode payload bytes into IQ samples at the modem's
    // working sample rate. Caller is responsible for re-sampling to the
    // radio's sample rate.
    [[nodiscard]] virtual std::vector<Sample> encode(std::span<const std::uint8_t> payload) const = 0;

    virtual void set_frame_callback(FrameCallback cb) = 0;
    virtual void set_event_callback(EventCallback cb) = 0;
    [[nodiscard]] virtual const LoraParams& params() const = 0;
    [[nodiscard]] virtual std::uint32_t working_sample_rate_hz() const = 0;
};

// Construct a modem for the given parameters. The returned object is
// currently a stub that does not yet decode/encode frames.
std::unique_ptr<ILoraModem> make_modem(const LoraParams& params);

} // namespace mrf::modem
