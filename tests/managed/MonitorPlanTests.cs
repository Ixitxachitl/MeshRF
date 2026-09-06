// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// <see cref="MonitorPlan"/> decides what the receiver listens to and where
/// the capture is centred. The cases here are the ones the Monitors window
/// shows the user, so a wrong answer is a wrong promise on screen.
/// </summary>
public class MonitorPlanTests
{
    private static readonly uint[] HackRfRates = [2_000_000, 2_400_000, 4_000_000, 8_000_000, 10_000_000, 12_500_000, 16_000_000];

    /// <summary>Noah's setup: MediumFast on slot 45 of the US band.</summary>
    private static MonitorPlan.Primary MediumFast45() =>
        new(LoraPreset.MediumFast, IsCustom: false, Sf: 9, BwHz: 250_000, Cr: 5,
            FreqMHz: ChannelPlan.FrequencyMHz(Region.US, LoraPreset.MediumFast, 45));

    private static MonitorPlan.Result Build(MonitorPlan.Primary primary, uint rateHz, bool enabled = true,
                                            IReadOnlyCollection<string>? excluded = null, double? offsetKHz = null,
                                            RadioDeviceKind kind = RadioDeviceKind.HackRf) =>
        MonitorPlan.Build(Region.US, primary, kind, rateHz, HackRfRates, enabled, excluded ?? Array.Empty<string>(), offsetKHz);

    [Fact]
    public void FeatureOffIsThePrimaryAloneAtZeroOffset()
    {
        var plan = Build(MediumFast45(), 2_400_000, enabled: false);
        Assert.Single(plan.Listeners);
        Assert.True(plan.Listeners[0].IsPrimary);
        Assert.Equal(913.125, plan.DeviceCenterMHz, 6);
        Assert.Equal(0, plan.CenterOffsetKHz);
        Assert.Empty(plan.LeftOut);
    }

    [Fact]
    public void HardwareModemIsThePrimaryAlone()
    {
        var plan = Build(MediumFast45(), 2_400_000, kind: RadioDeviceKind.Sx1262);
        Assert.Single(plan.Listeners);
    }

    [Fact]
    public void HackRfBasebandFilterIsTheWidestBelowTheRate()
    {
        Assert.Equal(1_750_000u, MonitorPlan.HackRfBasebandFilterHz(2_400_000));
        Assert.Equal(3_500_000u, MonitorPlan.HackRfBasebandFilterHz(4_000_000));
        Assert.Equal(7_000_000u, MonitorPlan.HackRfBasebandFilterHz(8_000_000));
        Assert.Equal(9_000_000u, MonitorPlan.HackRfBasebandFilterHz(10_000_000));
        Assert.Equal(15_000_000u, MonitorPlan.HackRfBasebandFilterHz(16_000_000));
    }

    [Fact]
    public void AtTwoPointFourMegasamplesOnlyTheNearestPresetsFit()
    {
        var plan = Build(MediumFast45(), 2_400_000);
        double half = plan.UsableHalfSpanMHz;
        Assert.InRange(half, 0.78, 0.79); // 0.9 of half of 1.75 MHz
        foreach (var l in plan.Listeners)
            Assert.True(Math.Abs(l.FreqMHz - plan.DeviceCenterMHz) + l.BandwidthMHz / 2 <= half + 1e-9,
                        $"{l.Preset} at {l.FreqMHz} sits outside the window");
        // LongFast's default slot is 906.875, over 6 MHz away.
        var longFast = Assert.Single(plan.LeftOut, x => x.Preset == LoraPreset.LongFast);
        Assert.Equal(MonitorPlan.LeftOutReason.OutOfRange, longFast.Reason);
        Assert.Equal(906.875, longFast.FreqMHz, 6);
        Assert.NotNull(longFast.FitsAtRateHz);
    }

    [Fact]
    public void ALongFastWindowNeedsSixteenMegasamplesCentredButTenWhenSlid()
    {
        var primary = MediumFast45();
        // Held on the primary, LongFast at 906.875 needs the whole 16 MS/s window.
        var centred = Build(primary, 10_000_000, offsetKHz: 0);
        Assert.DoesNotContain(centred.Listeners, l => l.Preset == LoraPreset.LongFast);
        // Let the window slide and 10 MS/s (usable ±4.05 MHz) covers both.
        var slid = Build(primary, 10_000_000);
        Assert.Contains(slid.Listeners, l => l.Preset == LoraPreset.LongFast);
        Assert.True(slid.DeviceCenterMHz < primary.FreqMHz, "the centre should move down toward LongFast");
        Assert.True(primary.FreqMHz - slid.DeviceCenterMHz + 0.125 <= slid.UsableHalfSpanMHz + 1e-9,
                    "the primary must stay inside the window");
        // And the OutOfRange note at 2.4 MS/s names a rate that holds both.
        var narrow = Build(primary, 2_400_000);
        var note = Assert.Single(narrow.LeftOut, x => x.Preset == LoraPreset.LongFast);
        Assert.Equal(10_000_000u, note.FitsAtRateHz);
    }

    [Fact]
    public void AManualOffsetIsClampedSoThePrimaryStaysInside()
    {
        var plan = Build(MediumFast45(), 2_400_000, offsetKHz: -5_000);
        // As far down as the primary's own channel allows, to the kilohertz
        // the centre is rounded to.
        double reach = plan.UsableHalfSpanMHz - 0.125;
        Assert.InRange(plan.CenterOffsetKHz / 1000.0, -reach - 0.001, -reach + 0.001);
        Assert.True(plan.Listeners[0].IsPrimary);
        Assert.True(Math.Abs(MediumFast45().FreqMHz - plan.DeviceCenterMHz) + 0.125 <= plan.UsableHalfSpanMHz + 1e-9);
    }

    [Fact]
    public void APrimaryAtTheBandEdgeGetsAOneSidedWindow()
    {
        // Slot 1 of the US band, 902.125 MHz: nothing lies below it, so the
        // window should sit above.
        var edge = new MonitorPlan.Primary(LoraPreset.MediumFast, false, 9, 250_000, 5,
                                           ChannelPlan.FrequencyMHz(Region.US, LoraPreset.MediumFast, 1));
        var plan = Build(edge, 8_000_000);
        Assert.True(plan.DeviceCenterMHz > edge.FreqMHz);
        Assert.True(plan.Listeners.Count > 1);
    }

    /// <summary>
    /// Presets hash their names onto slots independently, so in a narrow band
    /// several land on one channel. EU_433 is four 250 kHz slots wide, and
    /// ShortFast, MediumSlow and LongFast all hash to the last of them.
    /// </summary>
    [Fact]
    public void PresetsSharingADefaultSlotAndBandwidthShareAChannel()
    {
        var primary = new MonitorPlan.Primary(LoraPreset.MediumFast, false, 9, 250_000, 5,
                                              MonitorPlan.DefaultSlotFrequencyMHz(Region.EU_433, LoraPreset.MediumFast));
        var plan = MonitorPlan.Build(Region.EU_433, primary, RadioDeviceKind.HackRf, 4_000_000,
                                     HackRfRates, true, Array.Empty<string>(), null);
        var shared = plan.Listeners
            .GroupBy(l => (Math.Round(l.FreqMHz, 6), l.BwHz))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.NotEmpty(shared);
        Assert.Contains(shared, g => g.Select(l => l.Preset).Contains(LoraPreset.LongFast) &&
                                     g.Select(l => l.Preset).Contains(LoraPreset.ShortFast));
    }

    [Fact]
    public void ExcludedAndUnsupportedPresetsAreReportedNotListenedFor()
    {
        var plan = Build(MediumFast45(), 16_000_000, excluded: new[] { "LongFast" });
        Assert.DoesNotContain(plan.Listeners, l => l.Preset == LoraPreset.LongFast);
        var excluded = Assert.Single(plan.LeftOut, x => x.Preset == LoraPreset.LongFast);
        Assert.Equal(MonitorPlan.LeftOutReason.Excluded, excluded.Reason);

        // EU_868 is 250 kHz wide, so the 500 kHz Turbo presets cannot be held.
        var eu = new MonitorPlan.Primary(LoraPreset.LongFast, false, 11, 250_000, 5,
                                         MonitorPlan.DefaultSlotFrequencyMHz(Region.EU_868, LoraPreset.LongFast));
        var euPlan = MonitorPlan.Build(Region.EU_868, eu, RadioDeviceKind.HackRf, 16_000_000, HackRfRates,
                                       true, Array.Empty<string>(), null);
        var turbo = Assert.Single(euPlan.LeftOut, x => x.Preset == LoraPreset.ShortTurbo);
        Assert.Equal(MonitorPlan.LeftOutReason.Unsupported, turbo.Reason);
    }

    /// <summary>
    /// A preset is left out only when the primary is on that preset's own
    /// default slot, which is the channel that would be listened for twice.
    /// Slot 45 is where MediumFast's name hashes in the US band, so Noah's
    /// primary is exactly that case.
    /// </summary>
    [Fact]
    public void ThePrimaryOwnChannelIsNotListenedForTwice()
    {
        double defaultSlot = MonitorPlan.DefaultSlotFrequencyMHz(Region.US, LoraPreset.MediumFast);
        Assert.Equal(defaultSlot, MediumFast45().FreqMHz, 6);

        var plan = Build(MediumFast45(), 16_000_000);
        Assert.DoesNotContain(plan.Listeners, l => l.Preset == LoraPreset.MediumFast && !l.IsPrimary);
        var self = Assert.Single(plan.LeftOut, x => x.Preset == LoraPreset.MediumFast);
        Assert.Equal(MonitorPlan.LeftOutReason.IsPrimary, self.Reason);
    }

    /// <summary>The same preset on another slot is a different mesh, so it
    /// is still listened for.</summary>
    [Fact]
    public void ThePrimaryPresetOnAnotherSlotIsStillListenedFor()
    {
        var offSlot = new MonitorPlan.Primary(LoraPreset.MediumFast, false, 9, 250_000, 5,
                                              ChannelPlan.FrequencyMHz(Region.US, LoraPreset.MediumFast, 46));
        var plan = Build(offSlot, 16_000_000);
        Assert.Contains(plan.Listeners, l => l.Preset == LoraPreset.MediumFast && !l.IsPrimary);
        Assert.DoesNotContain(plan.LeftOut, x => x.Preset == LoraPreset.MediumFast);
    }

    [Fact]
    public void RtlSdrUsesAFractionOfItsRate()
    {
        Assert.Equal(0.4 * 2.4, MonitorPlan.UsableHalfSpanMHz(RadioDeviceKind.RtlSdr, 2_400_000), 6);
    }
}
