// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include "mrf/mac/PacketHeader.h"

#include <cstdint>
#include <vector>

namespace mrf::mac {

// CSMA/CA helper. Phase 3 placeholder.
class Csma {
public:
    // True if the channel is currently considered idle and we may transmit.
    bool channel_is_idle() const noexcept { return idle_; }

    // Compute a random backoff in milliseconds based on SNR (dB) and channel
    // utilization (0..1). Mirrors the firmware's contention-window sizing.
    std::uint32_t backoff_ms(float snr_db, float channel_util) const noexcept;

    void set_idle(bool idle) noexcept { idle_ = idle; }

private:
    bool idle_{true};
};

} // namespace mrf::mac
