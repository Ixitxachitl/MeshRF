// SPDX-License-Identifier: GPL-3.0-or-later
//
// One channel of a wide capture, brought down to baseband and demodulated.
//
// A listener is a LoRa configuration on a frequency. Several listeners can
// share a channel, the same frequency and bandwidth with different
// spreading factors, and then they share the mixing and decimation that
// dominate the cost, with only the demodulators differing. A chain is that
// shared front end plus its demodulators:
//
//   device-rate IQ -> mix by (channel - device centre) -> resample to
//   4 samples per chip -> one MeshtasticRx per listener
//
// The resampler's low-pass, cut at the output Nyquist of twice the
// bandwidth, is the channel filter. A neighbouring channel closer than that
// is passed to the demodulators, which is the same adjacent-channel
// behaviour a single-channel receiver tuned straight to the channel has.

#pragma once

#include "mrf/dsp/Resampler.h"
#include "mrf/dsp/SignalStats.h"
#include "mrf/modem/LoraModem.h"

#include <complex>
#include <cstdint>
#include <functional>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace mrf::modem {

// What the caller wants demodulated: a configuration on a frequency.
struct RxListener {
    LoraParams    params;
    std::uint64_t center_freq_hz;
};

class RxListenerChain {
public:
    // A listener as this chain knows it: its index in the caller's table,
    // which every event and frame reports, and its parameters.
    struct Member {
        int        index;
        LoraParams params;
    };

    using FrameCallback = std::function<void(int listener, const DecodedFrame&)>;
    using EventCallback = std::function<void(int listener, std::string)>;

    // Every member must share `bandwidth_hz`; that is what makes them one
    // chain. `offset_hz` is the channel centre relative to the device centre.
    // Throws std::invalid_argument for parameters the modem cannot take.
    RxListenerChain(std::uint32_t device_rate_hz,
                    std::int64_t offset_hz,
                    std::uint32_t bandwidth_hz,
                    std::span<const Member> members);

    void set_frame_callback(FrameCallback cb) { frame_cb_ = std::move(cb); }
    void set_event_callback(EventCallback cb);

    // Feed one block of device-rate IQ. Returns the channel at the modem
    // rate, after mixing and decimation, valid until the next call: the
    // caller keeps it for the packet spectrogram when this is the primary.
    // Thread-affine.
    std::span<const Sample> process(std::span<const Sample> in);

    [[nodiscard]] std::int64_t  offset_hz()        const noexcept { return offset_hz_; }
    [[nodiscard]] std::uint32_t bandwidth_hz()     const noexcept { return bandwidth_hz_; }
    [[nodiscard]] std::uint32_t working_rate_hz()  const noexcept { return working_rate_hz_; }
    [[nodiscard]] const std::vector<Member>& members() const noexcept { return members_; }
    [[nodiscard]] bool has_listener(int index) const noexcept;

    // Signal level of the channel itself, after the channel filter, so a
    // listener's RSSI is its own channel's and not the whole capture's.
    [[nodiscard]] dsp::SignalStats::Snapshot stats() const noexcept { return stats_.snapshot(); }

private:
    void mix_(std::span<const Sample> in);

    std::uint32_t device_rate_hz_;
    std::int64_t  offset_hz_;
    std::uint32_t bandwidth_hz_;
    std::uint32_t working_rate_hz_;

    // Mixer phase in turns, as a 32-bit integer so it cannot drift: the
    // increment is exact and the accumulator wraps. Each block starts a
    // float rotator from this phase and advances it by complex multiply,
    // so the per-sample cost is two multiplies and the error is bounded
    // by one block.
    std::uint32_t phase_{0};
    std::uint32_t phase_inc_{0};
    std::vector<Sample> mixed_;

    std::unique_ptr<dsp::Resampler> resampler_;
    dsp::SignalStats stats_;

    std::vector<Member> members_;
    std::vector<std::unique_ptr<ILoraModem>> modems_;

    FrameCallback frame_cb_;
    EventCallback event_cb_;
};

} // namespace mrf::modem
