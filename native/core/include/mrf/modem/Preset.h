// SPDX-License-Identifier: GPL-3.0-or-later
#pragma once

#include <cstdint>

namespace mrf::modem {

// Standard Meshtastic LoRa presets. Source:
// https://meshtastic.org/docs/overview/radio-settings/#presets
// Parameters verified against firmware develop branch modemPresetToParams().
enum class Preset : std::uint8_t {
    ShortTurbo,    // SF7  / 500 kHz / 4-5
    ShortFast,     // SF7  / 250 kHz / 4-5
    ShortSlow,     // SF8  / 250 kHz / 4-5
    MediumFast,    // SF9  / 250 kHz / 4-5
    MediumSlow,    // SF10 / 250 kHz / 4-5
    LongTurbo,     // SF11 / 500 kHz / 4-8
    LongFast,      // SF11 / 250 kHz / 4-5  (default)
    LongModerate,  // SF11 / 125 kHz / 4-8  (LDRO)
    LongSlow,      // SF12 / 125 kHz / 4-8  (LDRO)
    LiteFast,      // SF9  / 125 kHz / 4-5
    LiteSlow,      // SF10 / 125 kHz / 4-5
    NarrowFast,    // SF7  /  62.5 kHz / 4-6
    NarrowSlow,    // SF8  /  62.5 kHz / 4-6
    TinyFast,      // SF7  /  15.6 kHz / 4-5
    TinySlow,      // SF8  /  15.6 kHz / 4-6  (LDRO)
};

struct LoraParams {
    std::uint8_t  spreading_factor; // 5..12
    std::uint32_t bandwidth_hz;     // 7800..500000
    std::uint8_t  coding_rate;      // 5..8 -> 4/N
    std::uint8_t  sync_word{0x2B};  // Meshtastic public network
    std::uint16_t preamble_symbols{16};
    bool          explicit_header{true};
    bool          crc_enabled{true};
    bool          low_data_rate_optimize{false};
};

constexpr LoraParams params_for(Preset p) noexcept {
    switch (p) {
        case Preset::ShortTurbo:    return {7,  500'000, 5};
        case Preset::ShortFast:     return {7,  250'000, 5};
        case Preset::ShortSlow:     return {8,  250'000, 5};
        case Preset::MediumFast:    return {9,  250'000, 5};
        case Preset::MediumSlow:    return {10, 250'000, 5};
        case Preset::LongTurbo:     return {11, 500'000, 8};
        case Preset::LongFast:      return {11, 250'000, 5};
        case Preset::LongModerate:  return {11, 125'000, 8, 0x2B, 16, true, true, true};
        case Preset::LongSlow:      return {12, 125'000, 8, 0x2B, 16, true, true, true};
        case Preset::LiteFast:      return {9,  125'000, 5};
        case Preset::LiteSlow:      return {10, 125'000, 5};
        case Preset::NarrowFast:    return {7,   62'500, 6};
        case Preset::NarrowSlow:    return {8,   62'500, 6};
        case Preset::TinyFast:      return {7,   15'600, 5};
        case Preset::TinySlow:      return {8,   15'600, 6, 0x2B, 16, true, true, true};
    }
    return params_for(Preset::LongFast);
}

} // namespace mrf::modem
