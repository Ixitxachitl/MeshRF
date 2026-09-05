// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Firmware holds a node talking on a default channel to a slower cadence than
/// it may have configured, because that channel carries the whole neighbourhood
/// rather than one group.
/// </summary>
public class DefaultChannelMinimumsTests
{
    private const LoraPreset Preset = LoraPreset.MediumFast;

    /// <summary>Well-known key, named the way the preset would name it.</summary>
    private static ChannelConfig Default(string? name = null) => new()
    {
        Index = 0,
        Name = name ?? ChannelPlan.PresetName(Preset),
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    private static ChannelConfig Private(byte precision = 13) => new()
    {
        Index = 1,
        Name = "Basement",
        Psk = Enumerable.Repeat((byte)0x7A, 16).ToArray(),
        Role = ChannelRole.Secondary,
        PositionPrecision = precision,
    };

    // ---- Recognising a default channel ----

    [Fact]
    public void WellKnownKeyUnderThePresetNameIsDefault() =>
        Assert.True(DefaultChannelMinimums.IsDefaultChannel(Default(), Preset));

    // An unnamed channel is displayed and hashed under the preset name — that
    // is exactly the out-of-the-box channel this rule exists for.
    [Fact]
    public void UnnamedChannelCountsAsDefault() =>
        Assert.True(DefaultChannelMinimums.IsDefaultChannel(Default(name: ""), Preset));

    [Fact]
    public void RenamedChannelIsNotDefault() =>
        Assert.False(DefaultChannelMinimums.IsDefaultChannel(Default("Basement"), Preset));

    [Fact]
    public void OwnKeyIsNotDefault() =>
        Assert.False(DefaultChannelMinimums.IsDefaultChannel(Private(), Preset));

    // The name has to match the preset actually in force: a channel called
    // "LongFast" on a MediumFast radio reaches nobody else's default.
    [Fact]
    public void NameMustMatchTheCurrentPreset() =>
        Assert.False(DefaultChannelMinimums.IsDefaultChannel(
            Default(ChannelPlan.PresetName(LoraPreset.LongFast)), Preset));

    // Firmware calls both the channel and the preset "Custom" once the modem is
    // hand-tuned, so the unnamed channel still matches and a preset-named one
    // stops matching — the preset name means nothing on a modem not running it.
    [Fact]
    public void CustomModemLeavesOnlyTheUnnamedChannelDefault()
    {
        Assert.True(DefaultChannelMinimums.IsDefaultChannel(Default(name: ""), Preset, usesPreset: false));
        Assert.False(DefaultChannelMinimums.IsDefaultChannel(Default(), Preset, usesPreset: false));
        Assert.True(DefaultChannelMinimums.IsDefaultChannel(Default("Custom"), Preset, usesPreset: false));
    }

    [Fact]
    public void DisabledChannelIsNotDefault()
    {
        var ch = Default();
        ch.Role = ChannelRole.Disabled;
        Assert.False(DefaultChannelMinimums.IsDefaultChannel(ch, Preset));
    }

    // ---- Telemetry gate ----

    [Fact]
    public void TelemetryFloorAppliesOnADefaultChannel() =>
        Assert.True(DefaultChannelMinimums.HasDefaultChannel(
            new[] { Default(), Private() }, Preset, usesPreset: true, onDefaultFrequencySlot: true));

    // A custom modem or an off-plan frequency means nobody else is listening
    // there, so a default-named channel on it reaches only us.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void OffPlanRadioHasNoDefaultChannel(bool usesPreset, bool onSlot) =>
        Assert.False(DefaultChannelMinimums.HasDefaultChannel(
            new[] { Default() }, Preset, usesPreset, onSlot));

    [Fact]
    public void NoDefaultChannelInTheListMeansNoFloor() =>
        Assert.False(DefaultChannelMinimums.HasDefaultChannel(
            new[] { Private() }, Preset, usesPreset: true, onDefaultFrequencySlot: true));

    // ---- Position gate ----

    // Only the channel the report is addressed to decides it. A default channel
    // sitting elsewhere in the list is not where this position is going.
    [Fact]
    public void PositionSentPrivatelyIsNotFloored() =>
        Assert.False(DefaultChannelMinimums.PositionUsesDefaultChannel(Private(), Preset));

    [Fact]
    public void PositionOnADefaultChannelIsFloored()
    {
        var shared = Default();
        shared.PositionPrecision = 13;
        Assert.True(DefaultChannelMinimums.PositionUsesDefaultChannel(shared, Preset));
    }

    // Sharing switched off on that channel means no position goes out at all,
    // so there is nothing to hold down.
    [Fact]
    public void ChannelThatSharesNoPositionIsNotFloored()
    {
        var shared = Default();
        shared.PositionPrecision = 0;
        Assert.False(DefaultChannelMinimums.PositionUsesDefaultChannel(shared, Preset));
    }

    [Fact]
    public void NoChannelAtAllIsNotFloored() =>
        Assert.False(DefaultChannelMinimums.PositionUsesDefaultChannel(null, Preset));

    // ---- The minimums themselves ----

    [Theory]
    [InlineData("Client", 30 * 60)]
    [InlineData("Tracker", 30 * 60)]
    [InlineData("Router", 12 * 60 * 60)]
    [InlineData("RouterLate", 12 * 60 * 60)]
    public void TelemetryMinimumIsRoleAware(string role, int expected) =>
        Assert.Equal(expected, DefaultChannelMinimums.TelemetrySeconds(role));

    [Theory]
    [InlineData("Client", 60 * 60)]
    [InlineData("Router", 12 * 60 * 60)]
    public void PositionMinimumIsRoleAware(string role, int expected) =>
        Assert.Equal(expected, DefaultChannelMinimums.PositionSeconds(role));

    [Fact]
    public void SmartPositionMinimumIsFiveMinutesForEveryone() =>
        Assert.Equal(300, DefaultChannelMinimums.SmartPositionSeconds);

    // The whole point of the smart floor: a TAK_TRACKER asks for 15 seconds,
    // and on the shared channel it does not get it.
    [Fact]
    public void TakTrackerSmartGapIsHeldToFiveMinutesOnDefaults()
    {
        int roleDefault = RoleDefaults.For("TakTracker").PositionSmartMinSeconds!.Value;
        Assert.Equal(15, roleDefault);
        Assert.Equal(300, DefaultChannelMinimums.ConfiguredOrMinimum(
            roleDefault, DefaultChannelMinimums.SmartPositionSeconds));
    }

    [Fact]
    public void AConfiguredValueAboveTheMinimumIsLeftAlone() =>
        Assert.Equal(7200, DefaultChannelMinimums.ConfiguredOrMinimum(7200, 1800));

    // Zero means "unset" and is resolved to a default later — raising it here
    // would turn it into a real interval.
    [Fact]
    public void UnsetStaysUnset() =>
        Assert.Equal(0, DefaultChannelMinimums.ConfiguredOrMinimum(0, 1800));
}
