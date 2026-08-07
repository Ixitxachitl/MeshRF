// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

public class ChannelPlanTests
{
    [Fact]
    public void UsLongFastSlot20Is906_875MHz()
    {
        var f = ChannelPlan.FrequencyMHz(Region.US, LoraPreset.LongFast, 20);
        Assert.Equal(906.875, f, precision: 3);
    }

    [Fact]
    public void Slot1IsBwHalfAboveStart()
    {
        var f = ChannelPlan.FrequencyMHz(Region.US, LoraPreset.LongFast, 1);
        Assert.Equal(902.125, f, precision: 3);
    }

    [Fact]
    public void UsLongFastHas104Slots()
    {
        Assert.Equal(104, ChannelPlan.SlotCount(Region.US, LoraPreset.LongFast));
    }

    [Fact]
    public void UsLongTurboHas52Slots()
    {
        Assert.Equal(52, ChannelPlan.SlotCount(Region.US, LoraPreset.LongTurbo));
    }

    [Fact]
    public void UsLongModerateHas208Slots()
    {
        Assert.Equal(208, ChannelPlan.SlotCount(Region.US, LoraPreset.LongModerate));
    }

    [Fact]
    public void Djb2OfLongFastMatchesReferenceImplementation()
    {
        // Reference (computed by hand and confirmed against the upstream JS):
        // djb2("LongFast") = 130_429_955.
        Assert.Equal(130_429_955u, ChannelPlan.Djb2("LongFast"));
    }

    [Fact]
    public void UsLongFastDefaultSlotIs20()
    {
        // 130_429_955 % 104 = 19 (0-based); displayed as slot 20.
        Assert.Equal(20, ChannelPlan.DefaultSlot(Region.US, LoraPreset.LongFast));
    }

    [Theory]
    [InlineData(LoraPreset.ShortTurbo)]
    [InlineData(LoraPreset.ShortFast)]
    [InlineData(LoraPreset.ShortSlow)]
    [InlineData(LoraPreset.MediumFast)]
    [InlineData(LoraPreset.MediumSlow)]
    [InlineData(LoraPreset.LongFast)]
    [InlineData(LoraPreset.LongTurbo)]
    [InlineData(LoraPreset.LongModerate)]
    [InlineData(LoraPreset.LongSlow)]
    [InlineData(LoraPreset.LiteFast)]
    [InlineData(LoraPreset.LiteSlow)]
    [InlineData(LoraPreset.NarrowFast)]
    [InlineData(LoraPreset.NarrowSlow)]
    [InlineData(LoraPreset.TinyFast)]
    [InlineData(LoraPreset.TinySlow)]
    [InlineData(LoraPreset.MediumTurbo)]
    public void DefaultSlotIsAlwaysInRange(LoraPreset preset)
    {
        var n = ChannelPlan.SlotCount(Region.US, preset);
        var s = ChannelPlan.DefaultSlot(Region.US, preset);
        Assert.InRange(s, 1, n);
    }

    [Fact]
    public void OutOfRangeSlotsClamp()
    {
        var max = ChannelPlan.SlotCount(Region.US, LoraPreset.LongFast);
        var top  = ChannelPlan.FrequencyMHz(Region.US, LoraPreset.LongFast, max);
        var over = ChannelPlan.FrequencyMHz(Region.US, LoraPreset.LongFast, max + 50);
        Assert.Equal(top, over, precision: 6);

        var bot  = ChannelPlan.FrequencyMHz(Region.US, LoraPreset.LongFast, 1);
        var zero = ChannelPlan.FrequencyMHz(Region.US, LoraPreset.LongFast, 0);
        Assert.Equal(bot, zero, precision: 6);
    }

    [Fact]
    public void LongModUsesShortNameForHashing()
    {
        Assert.Equal("LongMod", ChannelPlan.PresetName(LoraPreset.LongModerate));
    }
}
