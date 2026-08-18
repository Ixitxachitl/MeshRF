// SPDX-License-Identifier: GPL-3.0-or-later
//
// Mirrors firmware's channel plan: the region table in
// meshtastic/firmware src/mesh/RadioInterface.cpp (`const RegionInfo regions[]`,
// the `PROFILE_*` constants and `applyModemConfig()`) and the preset table in
// src/mesh/MeshRadio.h (`modemPresetToParams()`).
namespace MeshRF;

/// <summary>
/// Meshtastic <c>Config.LoRaConfig.RegionCode</c>. Values are the protobuf's
/// own, so they can be compared and persisted without a translation table —
/// but the wire mapping still goes through an explicit switch rather than a
/// cast, because the protobuf carries region codes firmware has no radio
/// settings for.
/// </summary>
/// <remarks>
/// Only regions with a row in firmware's <c>regions[]</c> table appear here.
/// <c>UA_868</c> (15), <c>EU_874</c> (30) and <c>EU_917</c> (31) exist in the
/// protobuf but have no firmware row — UA_868 is marked deprecated upstream and
/// its row was removed — so there is no band, preset or slot plan to offer for
/// them.
/// </remarks>
public enum Region
{
    UNSET      = 0,
    US         = 1,
    EU_433     = 2,
    EU_868     = 3,
    CN         = 4,
    JP         = 5,
    ANZ        = 6,
    KR         = 7,
    TW         = 8,
    RU         = 9,
    IN         = 10,
    NZ_865     = 11,
    TH         = 12,
    LORA_24    = 13,
    UA_433     = 14,
    MY_433     = 16,
    MY_919     = 17,
    SG_923     = 18,
    PH_433     = 19,
    PH_868     = 20,
    PH_915     = 21,
    ANZ_433    = 22,
    KZ_433     = 23,
    KZ_863     = 24,
    NP_865     = 25,
    BR_902     = 26,
    ITU1_2M    = 27,
    ITU2_2M    = 28,
    EU_866     = 29,
    EU_N_868   = 32,
    ITU3_2M    = 33,
    ITU1_70CM  = 34,
    ITU2_70CM  = 35,
    ITU3_70CM  = 36,
    ITU2_125CM = 37,
}

/// <summary>
/// Maps (Region, Preset, slot) → center frequency, mirroring firmware's
/// <c>applyModemConfig()</c>. The default slot is the djb2 hash of the channel
/// name modulo the slot count, unless the region pins an explicit slot.
/// </summary>
public static class ChannelPlan
{
    public readonly record struct RegionRange(double FreqStartMHz, double FreqEndMHz);

    /// <summary>Per-region slot geometry — <c>PROFILE_*</c> in
    /// RadioInterface.cpp. <paramref name="SpacingMHz"/> is the gap between
    /// slots (and before the first); <paramref name="PaddingMHz"/> is the guard
    /// at each end of every slot.</summary>
    private readonly record struct RegionProfile(double SpacingMHz, double PaddingMHz);

    private static readonly RegionProfile ProfileStd        = new(0.0, 0.0);
    private static readonly RegionProfile ProfileEu868      = new(0.0, 0.0);
    private static readonly RegionProfile ProfileUndef      = new(0.0, 0.0);
    private static readonly RegionProfile ProfileLite       = new(0.4, 0.0375);
    private static readonly RegionProfile ProfileNarrow     = new(0.0, 0.0104);
    private static readonly RegionProfile ProfileHam20KHz   = new(0.0, 0.0022);
    private static readonly RegionProfile ProfileHam100KHz  = new(0.0, 0.01875);

    /// <summary>One row of firmware's <c>regions[]</c>.</summary>
    /// <param name="OverrideSlot">Firmware's <c>overrideSlot</c>: 0 selects the
    /// channel-name hash, a positive value pins that 1-based slot.</param>
    private readonly record struct RegionInfo(
        double FreqStartMHz,
        double FreqEndMHz,
        RegionProfile Profile,
        bool WideLora,
        LoraPreset DefaultPreset,
        int OverrideSlot);

    private static RegionInfo Info(Region region) => region switch
    {
        Region.US         => new(902.0,   928.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.EU_433     => new(433.0,   434.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.EU_868     => new(869.4,   869.65,  ProfileEu868,     false, LoraPreset.LongFast,   0),
        Region.EU_866     => new(865.6,   867.6,   ProfileLite,      false, LoraPreset.LiteFast,   0),
        Region.EU_N_868   => new(869.4,   869.65,  ProfileNarrow,    false, LoraPreset.NarrowSlow, 1),
        Region.CN         => new(470.0,   510.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.JP         => new(920.5,   923.5,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.ANZ        => new(915.0,   928.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.ANZ_433    => new(433.05,  434.79,  ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.RU         => new(868.7,   869.2,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.KR         => new(920.0,   923.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.TW         => new(920.0,   925.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.IN         => new(865.0,   867.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.NZ_865     => new(864.0,   868.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.TH         => new(920.0,   925.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.UA_433     => new(433.0,   434.7,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.MY_433     => new(433.0,   435.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.MY_919     => new(919.0,   924.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.SG_923     => new(917.0,   925.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.PH_433     => new(433.0,   434.7,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.PH_868     => new(868.0,   869.4,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.PH_915     => new(915.0,   918.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.KZ_433     => new(433.075, 434.775, ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.KZ_863     => new(863.0,   868.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.NP_865     => new(865.0,   868.0,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.BR_902     => new(902.0,   907.5,   ProfileStd,       false, LoraPreset.LongFast,   0),
        Region.ITU1_2M    => new(144.0,   146.0,   ProfileHam20KHz,  false, LoraPreset.TinyFast,   26),
        Region.ITU2_2M    => new(144.0,   148.0,   ProfileHam20KHz,  false, LoraPreset.TinyFast,   51),
        Region.ITU3_2M    => new(144.0,   148.0,   ProfileHam20KHz,  false, LoraPreset.TinyFast,   33),
        Region.ITU2_125CM => new(220.0,   225.0,   ProfileHam100KHz, false, LoraPreset.NarrowSlow, 37),
        Region.ITU1_70CM  => new(430.0,   440.0,   ProfileHam100KHz, false, LoraPreset.NarrowSlow, 37),
        Region.ITU2_70CM  => new(420.0,   450.0,   ProfileHam100KHz, false, LoraPreset.NarrowSlow, 137),
        Region.ITU3_70CM  => new(430.0,   450.0,   ProfileHam100KHz, false, LoraPreset.NarrowSlow, 37),
        Region.LORA_24    => new(2400.0,  2483.5,  ProfileStd,       true,  LoraPreset.LongFast,   0),
        Region.UNSET      => new(902.0,   928.0,   ProfileUndef,     false, LoraPreset.LongFast,   0),
        _                 => new(902.0,   928.0,   ProfileUndef,     false, LoraPreset.LongFast,   0),
    };

    public static RegionRange Range(Region region)
    {
        var info = Info(region);
        return new(info.FreqStartMHz, info.FreqEndMHz);
    }

    /// <summary>True for the 2.4 GHz SX128x regions, whose presets use scaled
    /// bandwidths.</summary>
    public static bool IsWideLora(Region region) => Info(region).WideLora;

    /// <summary>The preset a region falls back to when the configured one's
    /// bandwidth will not fit in its band — firmware's clamp branch in
    /// <c>applyModemConfig()</c>.</summary>
    public static LoraPreset DefaultPreset(Region region) => Info(region).DefaultPreset;

    /// <summary>
    /// Preset bandwidth in MHz. <paramref name="wideLora"/> selects the scaled
    /// bandwidths the SX128x regions use; the Lite, Narrow and Tiny presets are
    /// unscaled and ignore it, exactly as <c>modemPresetToParams()</c> does.
    /// </summary>
    public static double BandwidthMHz(LoraPreset preset, bool wideLora = false) => preset switch
    {
        LoraPreset.ShortTurbo or LoraPreset.LongTurbo or LoraPreset.MediumTurbo => wideLora ? 1.625   : 0.500,
        LoraPreset.LongModerate or LoraPreset.LongSlow                          => wideLora ? 0.40625 : 0.125,
        LoraPreset.LiteFast or LoraPreset.LiteSlow                              => 0.125,
        LoraPreset.NarrowFast or LoraPreset.NarrowSlow                          => 0.0625,
        LoraPreset.TinyFast or LoraPreset.TinySlow                              => 0.0156,
        _                                                                      => wideLora ? 0.8125  : 0.250,
    };

    /// <summary>Channel name used for djb2 default-slot hashing. Matches
    /// upstream <c>getModemPresetDisplayName()</c> exactly (note: "LongMod",
    /// not "LongModerate").</summary>
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
        LoraPreset.LongSlow     => "LongSlow",
        LoraPreset.LiteFast     => "LiteFast",
        LoraPreset.LiteSlow     => "LiteSlow",
        LoraPreset.NarrowFast   => "NarrowFast",
        LoraPreset.NarrowSlow   => "NarrowSlow",
        LoraPreset.TinyFast     => "TinyFast",
        LoraPreset.TinySlow     => "TinySlow",
        LoraPreset.MediumTurbo  => "MediumTurbo",
        _                       => "Invalid",
    };

    /// <summary>
    /// True when <paramref name="mhz"/> falls inside the region's band. Used to
    /// gate SX1262 transmits: a stick's front end is built for one band and
    /// reports nothing about which, so the selected region is the only
    /// statement of it available.
    /// </summary>
    public static bool Contains(Region region, double mhz)
    {
        var range = Range(region);
        // A hair of tolerance so a frequency sitting exactly on a band edge
        // isn't rejected by double rounding.
        const double eps = 1e-6;
        return mhz >= range.FreqStartMHz - eps && mhz <= range.FreqEndMHz + eps;
    }

    /// <summary>
    /// True when two regions' bands touch at all. A move between overlapping
    /// bands (say US to ANZ) is a retune the hardware handles; a move between
    /// disjoint ones (US to EU_433) means the stick is either the wrong one for
    /// the new region or about to drive its PA hundreds of MHz off-band.
    /// </summary>
    public static bool BandsOverlap(Region a, Region b)
    {
        var ra = Range(a);
        var rb = Range(b);
        return ra.FreqStartMHz <= rb.FreqEndMHz && rb.FreqStartMHz <= ra.FreqEndMHz;
    }

    /// <summary>Width of one slot: the preset's bandwidth plus whatever gap and
    /// guard band the region's profile requires.</summary>
    private static double SlotWidthMHz(in RegionInfo info, double bandwidthMHz) =>
        info.Profile.SpacingMHz + (info.Profile.PaddingMHz * 2.0) + bandwidthMHz;

    /// <summary>
    /// False for a pairing firmware treats as an invalid radio setting: a
    /// preset whose bandwidth is wider than the region's entire band, or a
    /// region whose pinned slot doesn't exist at that preset's slot width.
    /// Both are recoverable — this class clamps the way firmware does — but a
    /// caller about to transmit should warn rather than rely on the clamp.
    /// </summary>
    public static bool Supports(Region region, LoraPreset preset)
    {
        var info = Info(region);
        var bw = BandwidthMHz(preset, info.WideLora);
        if (info.FreqEndMHz - info.FreqStartMHz < SlotWidthMHz(info, bw)) return false;
        return info.OverrideSlot <= SlotCount(region, preset);
    }

    /// <summary>The bandwidth actually used, after firmware's clamp to the
    /// region's default preset for a band too narrow to hold the requested
    /// one.</summary>
    private static double EffectiveBandwidthMHz(in RegionInfo info, LoraPreset preset)
    {
        var bw = BandwidthMHz(preset, info.WideLora);
        if (info.FreqEndMHz - info.FreqStartMHz >= SlotWidthMHz(info, bw)) return bw;
        return BandwidthMHz(info.DefaultPreset, info.WideLora);
    }

    public static int SlotCount(Region region, LoraPreset preset)
    {
        var info = Info(region);
        var width = SlotWidthMHz(info, EffectiveBandwidthMHz(info, preset));
        // Firmware rounds (RadioInterface.cpp) — it does not truncate. A band
        // that isn't an exact multiple of the slot width gets the nearest whole
        // number of slots, so flooring here would shift every hashed default
        // slot in the region.
        var n = (int)Math.Round(
            (info.FreqEndMHz - info.FreqStartMHz + info.Profile.SpacingMHz) / width,
            MidpointRounding.AwayFromZero);
        return Math.Max(1, n);
    }

    /// <summary>1-indexed slot → center MHz. Firmware's
    /// <c>freqStart + (bw / 2000) + padding + (channel_num * freqSlotWidth)</c>
    /// with <c>channel_num</c> 0-based.</summary>
    public static double FrequencyMHz(Region region, LoraPreset preset, int slot)
    {
        var max = SlotCount(region, preset);
        if (slot < 1)   slot = 1;
        if (slot > max) slot = max;
        var info = Info(region);
        var bw = EffectiveBandwidthMHz(info, preset);
        return info.FreqStartMHz
             + bw / 2.0
             + info.Profile.PaddingMHz
             + (slot - 1) * SlotWidthMHz(info, bw);
    }

    /// <summary>djb2 hash; firmware's <c>hash()</c> in RadioInterface.cpp.</summary>
    public static uint Djb2(string s)
    {
        uint h = 5381;
        foreach (var c in s)
            h = unchecked((h << 5) + h + c); // h * 33 + c
        return h;
    }

    /// <summary>
    /// 1-indexed default slot. Regions that pin a slot (the ham bands, EU_N_868)
    /// use it directly; everywhere else it is
    /// <c>(djb2(channelName) % numSlots) + 1</c>.
    /// </summary>
    /// <param name="channelName">The primary channel's name, which is what
    /// firmware hashes. Empty selects the preset's display name, matching a
    /// device whose primary channel was never renamed.</param>
    /// <remarks>
    /// A pinned slot is returned verbatim, even where it exceeds the slot count
    /// — firmware assigns <c>channel_num = overrideSlot - 1</c> unconditionally
    /// and leaves <c>checkOrClampConfigLora()</c> to raise INVALID_RADIO_SETTING
    /// rather than quietly picking a different slot. That only happens for a
    /// preset the region did not pin its slot for, which <see cref="Supports"/>
    /// reports; <see cref="FrequencyMHz"/> still clamps into band so the
    /// transmit gate has something sane to refuse.
    /// </remarks>
    public static int DefaultSlot(Region region, LoraPreset preset, string channelName = "")
    {
        var n = SlotCount(region, preset);
        var overrideSlot = Info(region).OverrideSlot;
        if (overrideSlot > 0) return overrideSlot;
        var name = string.IsNullOrEmpty(channelName) ? PresetName(preset) : channelName;
        return (int)(Djb2(name) % (uint)n) + 1;
    }
}
