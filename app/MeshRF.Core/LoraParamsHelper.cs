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
    /// <summary>
    /// SF/BW/CR for a preset. <paramref name="wideLora"/> selects the scaled
    /// bandwidths the 2.4 GHz SX128x regions use — spreading factor and coding
    /// rate are the same either way, and the Lite/Narrow/Tiny presets are
    /// unscaled, exactly as <c>modemPresetToParams()</c> has it.
    /// </summary>
    /// <summary>
    /// The preset a spreading factor and bandwidth amount to, if any. Settings
    /// typed in by hand are not a different mesh for being typed: SF11 at
    /// 250 kHz is LongFast whichever way it was arrived at, so it is named for
    /// what it is.
    /// </summary>
    /// <remarks>
    /// Spreading factor and bandwidth alone, because they are what decides
    /// whether two stations can hear each other at all: the coding rate
    /// travels in each packet's own header, so it varies within a mesh rather
    /// than defining one. No two presets share a spreading factor and
    /// bandwidth, so at most one can match.
    /// </remarks>
    public static bool TryPresetFor(byte sf, double bwKhz, bool wideLora, out LoraPreset preset)
    {
        foreach (var candidate in Enum.GetValues<LoraPreset>())
        {
            var p = FromPreset(candidate, wideLora);
            if (p.Sf != sf || Math.Abs(p.BwKhz - bwKhz) > 0.001) continue;
            preset = candidate;
            return true;
        }
        preset = default;
        return false;
    }

    public static PresetLoraParams FromPreset(LoraPreset preset, bool wideLora = false) => preset switch
    {
        LoraPreset.ShortTurbo   => new(7,  wideLora ? 1625.0  : 500.0, 5),
        LoraPreset.ShortFast    => new(7,  wideLora ? 812.5   : 250.0, 5),
        LoraPreset.ShortSlow    => new(8,  wideLora ? 812.5   : 250.0, 5),
        LoraPreset.MediumFast   => new(9,  wideLora ? 812.5   : 250.0, 5),
        LoraPreset.MediumSlow   => new(10, wideLora ? 812.5   : 250.0, 5),
        LoraPreset.LongTurbo    => new(11, wideLora ? 1625.0  : 500.0, 8),
        LoraPreset.LongFast     => new(11, wideLora ? 812.5   : 250.0, 5),
        LoraPreset.LongModerate => new(11, wideLora ? 406.25  : 125.0, 8),
        LoraPreset.LongSlow     => new(12, wideLora ? 406.25  : 125.0, 8),
        LoraPreset.LiteFast     => new(9,  125.0, 5),
        LoraPreset.LiteSlow     => new(10, 125.0, 5),
        LoraPreset.NarrowFast   => new(7,  62.5,  6),
        LoraPreset.NarrowSlow   => new(8,  62.5,  6),
        LoraPreset.TinyFast     => new(7,  15.6,  5),
        LoraPreset.TinySlow     => new(8,  15.6,  6),
        LoraPreset.MediumTurbo  => new(9,  wideLora ? 1625.0  : 500.0, 5),
        _                       => new(11, wideLora ? 812.5   : 250.0, 5),  // LongFast default
    };
}
