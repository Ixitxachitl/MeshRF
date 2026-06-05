// SPDX-License-Identifier: GPL-3.0-or-later
//
// Mirrors the canonical Meshtastic frequency calculator in
// https://github.com/meshtastic/meshtastic/blob/master/src/components/tools/FrequencyCalculator.tsx
namespace MeshRF;

/// <summary>Meshtastic <c>Config_LoRaConfig_RegionCode</c>.</summary>
public enum Region
{
    UNSET,
    US,
    EU_433,
    EU_868,
    CN,
    JP,
    ANZ,
    KR,
    TW,
    RU,
    IN,
    NZ_865,
    TH,
    LORA_24,
    UA_433,
    UA_868,
    MY_433,
    MY_919,
    SG_923,
    ANZ_433,
}

/// <summary>
/// Maps (Region, Preset, slot) → center frequency, matching the upstream
/// Meshtastic web app's <c>FrequencyCalculator.tsx</c>. The default slot is
/// the djb2 hash of the preset name modulo the slot count.
/// </summary>
public static class ChannelPlan
{
    public readonly record struct RegionRange(double FreqStartMHz, double FreqEndMHz);

    public static RegionRange Range(Region region) => region switch
    {
        Region.US      => new(902.0,  928.0),
        Region.EU_433  => new(433.0,  434.0),
        Region.EU_868  => new(869.4,  869.65),
        Region.CN      => new(470.0,  510.0),
        Region.JP      => new(920.8,  927.8),
        Region.ANZ     => new(915.0,  928.0),
        Region.ANZ_433 => new(433.05, 434.79),
        Region.RU      => new(868.7,  869.2),
        Region.KR      => new(920.0,  923.0),
        Region.TW      => new(920.0,  925.0),
        Region.IN      => new(865.0,  867.0),
        Region.NZ_865  => new(864.0,  868.0),
        Region.TH      => new(920.0,  925.0),
        Region.UA_433  => new(433.0,  434.7),
        Region.UA_868  => new(868.0,  868.6),
        Region.MY_433  => new(433.0,  435.0),
        Region.MY_919  => new(919.0,  924.0),
        Region.SG_923  => new(917.0,  925.0),
        Region.LORA_24 => new(2400.0, 2483.5),
        Region.UNSET   => new(902.0,  928.0),
        _              => new(902.0,  928.0),
    };

    public static double BandwidthMHz(LoraPreset preset) => preset switch
    {
        LoraPreset.ShortTurbo or LoraPreset.LongTurbo => 0.500,
        LoraPreset.LongModerate                       => 0.125,
        _                                             => 0.250,
    };

    /// <summary>Channel name used for djb2 default-slot hashing. Matches
    /// upstream <c>getChannelName()</c> exactly (note: "LongMod", not
    /// "LongModerate").</summary>
    public static string PresetName(LoraPreset p) => p switch
    {
        LoraPreset.ShortTurbo   => "ShortTurbo",
        LoraPreset.ShortFast    => "ShortFast",
        LoraPreset.ShortSlow    => "ShortSlow",
        LoraPreset.MediumFast   => "MediumFast",
        LoraPreset.MediumSlow   => "MediumSlow",
        LoraPreset.LongFast     => "LongFast",
        LoraPreset.LongTurbo    => "LongTurbo",
        LoraPreset.LongModerate => "LongMod",
        _                       => "Invalid",
    };

    public static int SlotCount(Region region, LoraPreset preset)
    {
        var range = Range(region);
        var bw    = BandwidthMHz(preset);
        var n = (int)Math.Floor((range.FreqEndMHz - range.FreqStartMHz) / bw);
        return Math.Max(1, n);
    }

    /// <summary>1-indexed slot → center MHz. Identical math to the upstream
    /// 0-indexed formula: freq = start + bw/2 + channel*bw.</summary>
    public static double FrequencyMHz(Region region, LoraPreset preset, int slot)
    {
        var max = SlotCount(region, preset);
        if (slot < 1)   slot = 1;
        if (slot > max) slot = max;
        var range = Range(region);
        var bw    = BandwidthMHz(preset);
        return range.FreqStartMHz + bw / 2.0 + (slot - 1) * bw;
    }

    /// <summary>djb2 hash; matches upstream <c>calculateHash</c>.</summary>
    public static uint Djb2(string s)
    {
        uint h = 5381;
        foreach (var c in s)
            h = unchecked((h << 5) + h + c); // h * 33 + c
        return h;
    }

    /// <summary>1-indexed default slot — <c>(djb2(presetName) % numSlots) + 1</c>.
    /// Matches the upstream "Default Frequency Slot" field exactly.</summary>
    public static int DefaultSlot(Region region, LoraPreset preset)
    {
        var n = SlotCount(region, preset);
        var h = Djb2(PresetName(preset));
        return (int)(h % (uint)n) + 1;
    }
}
