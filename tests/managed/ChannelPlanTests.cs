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

    [Fact]
    public void ContainsAcceptsAnInBandFrequencyAndRejectsAnotherBand()
    {
        Assert.True(ChannelPlan.Contains(Region.US, 913.125));
        Assert.False(ChannelPlan.Contains(Region.US, 433.5));
        Assert.True(ChannelPlan.Contains(Region.EU_433, 433.5));
        Assert.False(ChannelPlan.Contains(Region.EU_433, 913.125));
    }

    [Fact]
    public void ContainsIsInclusiveOfBothBandEdges()
    {
        var r = ChannelPlan.Range(Region.US);
        Assert.True(ChannelPlan.Contains(Region.US, r.FreqStartMHz));
        Assert.True(ChannelPlan.Contains(Region.US, r.FreqEndMHz));
    }

    /// <summary>
    /// The property that makes the transmit band gate safe to ship for regions
    /// nobody here can test: every frequency the app can produce from a region,
    /// preset and slot is inside that region's own band, so the gate never
    /// fires on a legitimate selection — in any region, not just the 915 ones.
    /// </summary>
    [Fact]
    public void EverySlotOfEveryRegionAndPresetIsInItsOwnBand()
    {
        foreach (var region in Enum.GetValues<Region>())
        {
            foreach (var preset in Enum.GetValues<LoraPreset>())
            {
                var slots = ChannelPlan.SlotCount(region, preset);
                for (var slot = 1; slot <= slots; slot++)
                {
                    var f = ChannelPlan.FrequencyMHz(region, preset, slot);
                    Assert.True(ChannelPlan.Contains(region, f),
                        $"{region}/{preset} slot {slot} = {f} MHz falls outside its own band");
                }
            }
        }
    }

    [Fact]
    public void OverlappingBandsAreARetuneAndDisjointOnesAreNot()
    {
        // US and ANZ share the top of the 900 band, so moving between them is
        // an ordinary retune that needs no confirmation.
        Assert.True(ChannelPlan.BandsOverlap(Region.US, Region.ANZ));
        Assert.True(ChannelPlan.BandsOverlap(Region.EU_433, Region.UA_433));
        // A region always overlaps itself, so re-selecting one cannot prompt.
        Assert.True(ChannelPlan.BandsOverlap(Region.US, Region.US));

        // These are the moves worth confirming: hundreds of MHz apart, where a
        // stick built for one band would drive its PA far outside it.
        Assert.False(ChannelPlan.BandsOverlap(Region.US, Region.EU_433));
        Assert.False(ChannelPlan.BandsOverlap(Region.EU_868, Region.CN));
        Assert.False(ChannelPlan.BandsOverlap(Region.US, Region.LORA_24));
    }

    [Fact]
    public void Lora24IsBeyondAnySx1262Stick()
    {
        // 2.4 GHz belongs to the SX1280. The region is selectable, so the
        // native side refuses it on the chip's 150-960 MHz range — this pins the
        // managed half of that story: the band really is out of reach.
        var r = ChannelPlan.Range(Region.LORA_24);
        Assert.True(r.FreqStartMHz > 960.0);
    }
}
