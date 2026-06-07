// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// Lightweight DTO used to carry SF/BW/CR defaults for a given <see cref="LoraPreset"/>
/// without pulling in the native layer.
/// </summary>
public readonly record struct PresetLoraParams(byte Sf, double BwKhz, byte Cr);

/// <summary>
/// Maps every <see cref="LoraPreset"/> to its firmware-defined default SF/BW/CR values.
/// These mirror the values in <c>native/core/include/mrf/modem/Preset.h</c> and the
/// Meshtastic firmware's <c>modemPresetToParams()</c>.
/// </summary>
public static class LoraParamsHelper
{
    public static PresetLoraParams FromPreset(LoraPreset preset) => preset switch
    {
        LoraPreset.ShortTurbo   => new(7,  500.0,  5),
        LoraPreset.ShortFast    => new(7,  250.0,  5),
        LoraPreset.ShortSlow    => new(8,  250.0,  5),
        LoraPreset.MediumFast   => new(9,  250.0,  5),
        LoraPreset.MediumSlow   => new(10, 250.0,  5),
        LoraPreset.LongTurbo    => new(11, 500.0,  8),
        LoraPreset.LongFast     => new(11, 250.0,  5),
        LoraPreset.LongModerate => new(11, 125.0,  8),
        LoraPreset.LongSlow     => new(12, 125.0,  8),
        LoraPreset.LiteFast     => new(9,  125.0,  5),
        LoraPreset.LiteSlow     => new(10, 125.0,  5),
        LoraPreset.NarrowFast   => new(7,  62.5,   6),
        LoraPreset.NarrowSlow   => new(8,  62.5,   6),
        LoraPreset.TinyFast     => new(7,  15.6,   5),
        LoraPreset.TinySlow     => new(8,  15.6,   6),
        _                       => new(11, 250.0,  5),  // LongFast default
    };
}
