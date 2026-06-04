// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/mac/Csma.h"

#include <algorithm>
#include <random>

namespace mrf::mac {

std::uint32_t Csma::backoff_ms(float snr_db, float channel_util) const noexcept {
    // TODO(phase-3): implement the firmware's SNR-scaled contention window.
    // Sketch: smaller CW for low SNR (so far-away nodes flood first), larger
    // CW when channel_util is high.
    const float cw_min = 8.0f;
    const float cw_max = 256.0f;
    const float u = std::clamp(channel_util, 0.0f, 1.0f);
    const float s = std::clamp((snr_db + 20.0f) / 30.0f, 0.0f, 1.0f);
    const float cw = cw_min + (cw_max - cw_min) * u * (1.0f - 0.5f * s);

    static thread_local std::mt19937 rng{std::random_device{}()};
    std::uniform_real_distribution<float> dist(0.0f, cw);
    return static_cast<std::uint32_t>(dist(rng));
}

} // namespace mrf::mac
