// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/modem/RxListenerChain.h"

#include <algorithm>
#include <cmath>
#include <numbers>
#include <stdexcept>

namespace mrf::modem {

namespace {
constexpr double kTwoPi = 2.0 * std::numbers::pi;
constexpr double kPhaseScale = 4294967296.0; // 2^32 phase steps per turn

// Samples per chip the demodulator wants; LoraModem's kOversampling.
constexpr std::uint32_t kOversampling = 4;

// Rederive the float rotator from the integer phase this often. Float error
// grows by about 1e-7 per multiply, so a thousand steps stays far below
// anything the demodulator can see.
constexpr std::size_t kRotatorRenormEvery = 1024;
} // namespace

RxListenerChain::RxListenerChain(std::uint32_t device_rate_hz,
                                 std::int64_t offset_hz,
                                 std::uint32_t bandwidth_hz,
                                 std::span<const Member> members)
    : device_rate_hz_(device_rate_hz),
      offset_hz_(offset_hz),
      bandwidth_hz_(bandwidth_hz),
      working_rate_hz_(bandwidth_hz * kOversampling) {
    if (device_rate_hz == 0) throw std::invalid_argument("RxListenerChain: zero device rate");
    if (bandwidth_hz == 0)   throw std::invalid_argument("RxListenerChain: zero bandwidth");
    if (members.empty())     throw std::invalid_argument("RxListenerChain: no listeners");
    if (working_rate_hz_ > device_rate_hz)
        throw std::invalid_argument("RxListenerChain: device rate below the modem rate");
    // The channel has to lie inside the capture, or the mixer folds it in
    // from the far edge and the demodulator sees an alias.
    const double half_span = device_rate_hz / 2.0;
    if (std::abs(static_cast<double>(offset_hz)) + bandwidth_hz / 2.0 > half_span)
        throw std::invalid_argument("RxListenerChain: channel outside the capture");

    for (const auto& m : members) {
        if (m.params.bandwidth_hz != bandwidth_hz)
            throw std::invalid_argument("RxListenerChain: listeners on one chain share a bandwidth");
        members_.push_back(m);
        modems_.push_back(make_modem(m.params)); // validates SF and CR
    }

    // Phase increment in turns per sample, rounded to the nearest 2^-32 of
    // a turn: at 16 MS/s that is under 4 mHz of frequency error.
    const double turns_per_sample = static_cast<double>(offset_hz) / device_rate_hz;
    const auto inc = static_cast<std::int64_t>(std::llround(turns_per_sample * kPhaseScale));
    phase_inc_ = static_cast<std::uint32_t>(static_cast<std::uint64_t>(inc) & 0xFFFFFFFFull);

    resampler_ = std::make_unique<dsp::Resampler>(device_rate_hz, working_rate_hz_);

    for (std::size_t i = 0; i < modems_.size(); ++i) {
        const int index = members_[i].index;
        modems_[i]->set_frame_callback([this, index](const DecodedFrame& f) {
            if (frame_cb_) frame_cb_(index, f);
        });
    }
}

void RxListenerChain::set_event_callback(EventCallback cb) {
    event_cb_ = std::move(cb);
    for (std::size_t i = 0; i < modems_.size(); ++i) {
        const int index = members_[i].index;
        modems_[i]->set_event_callback([this, index](std::string msg) {
            if (event_cb_) event_cb_(index, std::move(msg));
        });
    }
}

bool RxListenerChain::has_listener(int index) const noexcept {
    return std::any_of(members_.begin(), members_.end(),
                       [index](const Member& m) { return m.index == index; });
}

void RxListenerChain::mix_(std::span<const Sample> in) {
    mixed_.resize(in.size());
    // Multiplying by exp(-j*2*pi*offset*t) brings the channel at +offset
    // down to DC, so the phase runs backwards.
    const std::uint32_t inc = static_cast<std::uint32_t>(0u - phase_inc_);
    const double inc_rad = -kTwoPi * static_cast<double>(phase_inc_) / kPhaseScale;
    const Sample step(static_cast<float>(std::cos(inc_rad)),
                      static_cast<float>(std::sin(inc_rad)));

    std::size_t i = 0;
    while (i < in.size()) {
        // Start the rotator from the exact integer phase.
        const double rad = kTwoPi * static_cast<double>(phase_) / kPhaseScale;
        Sample rot(static_cast<float>(std::cos(rad)), static_cast<float>(std::sin(rad)));
        const std::size_t end = std::min(in.size(), i + kRotatorRenormEvery);
        for (; i < end; ++i) {
            mixed_[i] = in[i] * rot;
            rot *= step;
            phase_ += inc;
        }
    }
}

std::span<const Sample> RxListenerChain::process(std::span<const Sample> in) {
    std::span<const Sample> baseband = in;
    if (phase_inc_ != 0) {
        mix_(in);
        baseband = std::span<const Sample>(mixed_.data(), mixed_.size());
    }
    auto channel = resampler_->process(baseband);
    stats_.process(channel);
    for (auto& modem : modems_) modem->process_rx(channel);
    return channel;
}

} // namespace mrf::modem
