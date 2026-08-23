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
    /// <remarks>
    /// EU_866 with either Tiny preset is excluded because firmware itself
    /// overshoots there: PROFILE_LITE's 0.4 MHz spacing makes the band an
    /// awkward multiple of a 15.6 kHz slot, and applyModemConfig()'s round()
    /// yields one slot more than fits, putting the top slot's centre 7.7 kHz
    /// past 867.6. Mirroring firmware is the goal, so MeshRF reproduces the
    /// overshoot and the band gate correctly refuses to transmit on it.
    /// </remarks>
    [Fact]
    public void EverySlotOfEveryRegionAndPresetIsInItsOwnBand()
    {
        foreach (var region in Enum.GetValues<Region>())
        {
            foreach (var preset in Enum.GetValues<LoraPreset>())
            {
                if (region == Region.EU_866 &&
                    preset is LoraPreset.TinyFast or LoraPreset.TinySlow) continue;

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
    public void JpBandMatchesFirmware()
    {
        // RadioInterface.cpp: RDEF(JP, 920.5f, 923.5f, ...). An earlier table
        // had 920.8-927.8, which put every JP slot on the wrong frequency and
        // ran 4.3 MHz past the top of the Japanese band.
        var r = ChannelPlan.Range(Region.JP);
        Assert.Equal(920.5, r.FreqStartMHz, precision: 4);
        Assert.Equal(923.5, r.FreqEndMHz, precision: 4);
        Assert.Equal(12, ChannelPlan.SlotCount(Region.JP, LoraPreset.LongFast));
        Assert.Equal(923.375, ChannelPlan.FrequencyMHz(Region.JP, LoraPreset.LongFast, 12), precision: 4);
    }

    [Theory]
    // The power_limit column of RDEF() in RadioInterface.cpp, spot-checked
    // across the spread: the ISM maximum, the two 433 regions that sit far
    // below it, JP's 13, and NZ_865's 36 — the only limit above 30, and so the
    // only one no stick here can reach.
    [InlineData(Region.US, 30)]
    [InlineData(Region.JP, 13)]
    [InlineData(Region.EU_433, 10)]
    [InlineData(Region.EU_868, 27)]
    [InlineData(Region.CN, 19)]
    [InlineData(Region.ANZ_433, 14)]
    [InlineData(Region.NZ_865, 36)]
    [InlineData(Region.LORA_24, 10)]
    public void PowerLimitMatchesFirmware(Region region, int dbm)
    {
        Assert.Equal(dbm, ChannelPlan.PowerLimitDbm(region));
    }

    [Fact]
    public void EveryRegionDeclaresAPowerLimit()
    {
        // Firmware reads 0 as "no limit declared" and falls back to 17 dBm.
        // No row in regions[] is 0 today, so a 0 here means a region was added
        // to the table without its limit rather than a region that has none.
        foreach (var region in Enum.GetValues<Region>())
            Assert.True(ChannelPlan.PowerLimitDbm(region) > 0, $"{region} has no power limit");
    }

    [Fact]
    public void SlotCountRoundsRatherThanTruncating()
    {
        // 26 MHz / 15.6 kHz = 1666.67. Firmware rounds to 1667; truncating to
        // 1666 changes the modulo and so moves every hashed default slot.
        Assert.Equal(1667, ChannelPlan.SlotCount(Region.US, LoraPreset.TinyFast));
        Assert.Equal(1577, ChannelPlan.DefaultSlot(Region.US, LoraPreset.TinyFast));

        // Presets that divide their band exactly are unaffected either way.
        Assert.Equal(104, ChannelPlan.SlotCount(Region.US, LoraPreset.LongFast));
    }

    [Fact]
    public void WideLoraRegionsScaleThePresetBandwidth()
    {
        Assert.True(ChannelPlan.IsWideLora(Region.LORA_24));
        Assert.False(ChannelPlan.IsWideLora(Region.US));

        // MeshRadio.h: LONG_FAST is 812.5 kHz on wideLora hardware, not 250.
        Assert.Equal(0.8125, ChannelPlan.BandwidthMHz(LoraPreset.LongFast, wideLora: true), precision: 6);
        Assert.Equal(0.250, ChannelPlan.BandwidthMHz(LoraPreset.LongFast, wideLora: false), precision: 6);
        // Lite/Narrow/Tiny have no scaled variant.
        Assert.Equal(0.0625, ChannelPlan.BandwidthMHz(LoraPreset.NarrowFast, wideLora: true), precision: 6);

        // 83.5 MHz / 812.5 kHz = 103 slots, not the 334 a 250 kHz slot implies.
        Assert.Equal(103, ChannelPlan.SlotCount(Region.LORA_24, LoraPreset.LongFast));
    }

    [Fact]
    public void ABandTooNarrowForThePresetFallsBackToTheRegionDefault()
    {
        // EU_868 spans 250 kHz, so a 500 kHz Turbo preset cannot fit. Firmware
        // records INVALID_RADIO_SETTING and clamps to the region's default
        // preset rather than transmitting off-band.
        Assert.False(ChannelPlan.Supports(Region.EU_868, LoraPreset.ShortTurbo));
        Assert.False(ChannelPlan.Supports(Region.EU_868, LoraPreset.LongTurbo));
        Assert.True(ChannelPlan.Supports(Region.EU_868, LoraPreset.LongFast));

        Assert.Equal(LoraPreset.LongFast, ChannelPlan.DefaultPreset(Region.EU_868));
        Assert.Equal(
            ChannelPlan.FrequencyMHz(Region.EU_868, LoraPreset.LongFast, 1),
            ChannelPlan.FrequencyMHz(Region.EU_868, LoraPreset.ShortTurbo, 1),
            precision: 6);
        Assert.True(ChannelPlan.Contains(Region.EU_868,
            ChannelPlan.FrequencyMHz(Region.EU_868, LoraPreset.ShortTurbo, 1)));
    }

    [Fact]
    public void RegionsWithSpacingAndPaddingUseTheirProfile()
    {
        // EU_866 is PROFILE_LITE: 0.4 MHz spacing, 37.5 kHz padding either
        // side, so a 125 kHz LiteFast slot is 600 kHz wide and 2.0 MHz of band
        // (plus one spacing) holds four of them.
        Assert.Equal(4, ChannelPlan.SlotCount(Region.EU_866, LoraPreset.LiteFast));
        Assert.Equal(865.7, ChannelPlan.FrequencyMHz(Region.EU_866, LoraPreset.LiteFast, 1), precision: 4);
        Assert.Equal(867.5, ChannelPlan.FrequencyMHz(Region.EU_866, LoraPreset.LiteFast, 4), precision: 4);
    }

    [Fact]
    public void RegionsThatPinASlotIgnoreTheHash()
    {
        // The ham bands and EU_N_868 carry an explicit overrideSlot in
        // firmware's region table instead of hashing the channel name.
        Assert.Equal(1, ChannelPlan.DefaultSlot(Region.EU_N_868, LoraPreset.NarrowSlow));
        Assert.Equal(26, ChannelPlan.DefaultSlot(Region.ITU1_2M, LoraPreset.TinyFast));
        Assert.Equal(51, ChannelPlan.DefaultSlot(Region.ITU2_2M, LoraPreset.TinyFast));
        Assert.Equal(137, ChannelPlan.DefaultSlot(Region.ITU2_70CM, LoraPreset.NarrowSlow));
        // A pinned slot holds whatever preset is selected, hash or no hash.
        Assert.Equal(26, ChannelPlan.DefaultSlot(Region.ITU1_2M, LoraPreset.NarrowSlow));

        // Each region pins a slot that exists at its own default preset...
        foreach (var region in Enum.GetValues<Region>())
        {
            var preset = ChannelPlan.DefaultPreset(region);
            Assert.InRange(ChannelPlan.DefaultSlot(region, preset), 1, ChannelPlan.SlotCount(region, preset));
            Assert.True(ChannelPlan.Supports(region, preset),
                $"{region} does not support its own default preset {preset}");
        }

        // ...but a wider preset leaves the pinned slot past the end of the
        // band. Firmware returns it anyway and flags the config; Supports()
        // is how a caller sees that coming.
        Assert.Equal(8, ChannelPlan.SlotCount(Region.ITU1_2M, LoraPreset.LongFast));
        Assert.Equal(26, ChannelPlan.DefaultSlot(Region.ITU1_2M, LoraPreset.LongFast));
        Assert.False(ChannelPlan.Supports(Region.ITU1_2M, LoraPreset.LongFast));
    }

    [Fact]
    public void DefaultSlotHashesTheChannelNameWhenTheChannelIsNamed()
    {
        // Firmware hashes the primary channel's name and only falls back to the
        // preset display name when it is unset.
        var unnamed = ChannelPlan.DefaultSlot(Region.US, LoraPreset.LongFast);
        Assert.Equal(20, unnamed);
        Assert.Equal(unnamed, ChannelPlan.DefaultSlot(Region.US, LoraPreset.LongFast, "LongFast"));

        var named = ChannelPlan.DefaultSlot(Region.US, LoraPreset.LongFast, "MeshRF");
        Assert.Equal((int)(ChannelPlan.Djb2("MeshRF") % 104) + 1, named);
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
