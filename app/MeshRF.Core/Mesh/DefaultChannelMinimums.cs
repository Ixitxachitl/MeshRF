// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;

namespace MeshRF.Mesh;

/// <summary>
/// Firmware's floor on how often a node may broadcast when it is talking on a
/// default channel (<c>NodeDB::installDefaultConfig</c>'s two coercion blocks).
///
/// A default channel is the one every unconfigured radio in the region joins, so
/// it carries the whole neighbourhood rather than one group. Firmware holds
/// anyone on it to a slower cadence than they may have configured for a private
/// channel: a 30-second beacon that is merely noisy on your own channel is
/// noise for every node in earshot on the shared one.
/// </summary>
/// <remarks>
/// Applied as a floor when a report is scheduled rather than by rewriting the
/// configured interval, so renaming a channel or giving it a key restores the
/// user's own setting instead of leaving it silently overwritten. The effect on
/// air is the same.
/// </remarks>
public static class DefaultChannelMinimums
{
    private const int OneDay = 24 * 60 * 60;

    /// <summary>Firmware <c>min_default_telemetry_interval_secs</c>.</summary>
    public static int TelemetrySeconds(string? role) => IsRouter(role) ? OneDay / 2 : 30 * 60;

    /// <summary>Firmware <c>min_default_broadcast_interval_secs</c>, which the
    /// position schedule is held to.</summary>
    public static int PositionSeconds(string? role) => IsRouter(role) ? OneDay / 2 : 60 * 60;

    /// <summary>Firmware <c>min_default_broadcast_smart_minimum_interval_secs</c>.
    /// Not role-aware — five minutes for everyone, including the TAK_TRACKER
    /// whose role default asks for fifteen seconds.</summary>
    public const int SmartPositionSeconds = 5 * 60;

    /// <summary>
    /// Firmware <c>Default::getConfiguredOrMinimumValue</c>. Zero passes
    /// through untouched: it means "unset", and the caller resolves it to a
    /// default later — raising it here would turn "unset" into a real interval.
    /// </summary>
    public static int ConfiguredOrMinimum(int configured, int minimum) =>
        configured == 0 ? 0 : Math.Max(configured, minimum);

    /// <summary>
    /// Firmware <c>DisplayFormatters::getModemPresetDisplayName</c> with
    /// <c>use_preset</c> false. Firmware relies on it being this exact literal,
    /// because it is what both a channel name and a preset name collapse to
    /// once the modem is hand-tuned.
    /// </summary>
    private const string CustomPresetName = "Custom";

    /// <summary>
    /// Firmware <c>Channels::isDefaultChannel</c>: the well-known key, under the
    /// name the current preset would have given it.
    /// </summary>
    /// <param name="usesPreset">Firmware <c>config.lora.use_preset</c>. False
    /// renames both sides to "Custom", so an unnamed channel still matches while
    /// one named after a preset no longer does — the preset name means nothing
    /// on a modem that isn't running that preset.</param>
    /// <remarks>
    /// An unnamed channel counts, because firmware displays such a channel under
    /// the preset name and hashes it that way — that is precisely the
    /// out-of-the-box channel this rule exists for.
    ///
    /// Firmware tests the stored key as the single byte 1, which is the only
    /// form it ever writes. MeshRF also stores the key that byte stands for, so
    /// the test is what the key resolves to; the two agree about every channel
    /// firmware could have configured, and ours additionally recognises the
    /// same channel written out in full.
    ///
    /// A disabled channel is excluded, where firmware's own loop counts it. It
    /// carries nothing, so a floor held for its sake would be one with no
    /// channel behind it.
    /// </remarks>
    public static bool IsDefaultChannel(ChannelConfig channel, LoraPreset preset, bool usesPreset = true)
    {
        if (channel.IsDisabled || !channel.UsesDefaultKey) return false;
        string presetName = usesPreset ? ChannelPlan.PresetName(preset) : CustomPresetName;
        string name = string.IsNullOrEmpty(channel.Name) ? presetName : channel.Name;
        return string.Equals(name, presetName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Firmware <c>Channels::hasDefaultChannel</c>, which gates the telemetry
    /// floor. The extra conditions matter: a custom modem or an off-plan
    /// frequency means nobody else is listening there, so a default-named
    /// channel on it reaches no one but us.
    /// </summary>
    public static bool HasDefaultChannel(IEnumerable<ChannelConfig> channels, LoraPreset preset,
                                         bool usesPreset, bool onDefaultFrequencySlot)
    {
        if (!usesPreset || !onDefaultFrequencySlot) return false;
        foreach (var channel in channels)
            if (IsDefaultChannel(channel, preset, usesPreset)) return true;
        return false;
    }

    /// <summary>
    /// Whether our positions would go out over a default channel, which gates
    /// the position floor. One channel decides it — the one the report is
    /// addressed to — so a default channel elsewhere in the list does not hold
    /// down a position sent privately.
    /// </summary>
    /// <remarks>
    /// Firmware has no say in where a position goes: <c>sendOurPosition</c>
    /// takes the first channel with a precision set, and
    /// <c>NodeDB::NodeDB</c> tests that one. MeshRF gives each auto report a
    /// channel of its own, so the channel to test is the one chosen rather
    /// than the first one found — otherwise a report addressed to a private
    /// channel is held to the shared channel's cadence.
    /// </remarks>
    public static bool PositionUsesDefaultChannel(ChannelConfig? positionChannel, LoraPreset preset,
                                                  bool usesPreset = true) =>
        positionChannel is not null
        && positionChannel.EffectivePositionPrecision != 0
        && IsDefaultChannel(positionChannel, preset, usesPreset);

    private static bool IsRouter(string? role) =>
        (role ?? string.Empty).Trim().Replace("_", string.Empty).ToUpperInvariant()
            is "ROUTER" or "ROUTERLATE";
}
