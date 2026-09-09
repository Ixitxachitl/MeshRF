// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Listeners the operator described by hand rather than picked off the preset
/// list, and the naming that makes each of them a mesh of its own.
///
/// A mesh is known by its name everywhere else in the app — its channel list,
/// its tab, and what a node records as where it was heard all key off it — so
/// a hand-made listener without one would share a channel list with every
/// other, and none of them could be spoken to correctly.
/// </summary>
public class CustomListenerPlanTests
{
    private static readonly uint[] HackRfRates =
        [2_000_000, 2_400_000, 4_000_000, 8_000_000, 10_000_000, 12_500_000, 16_000_000];

    private static MonitorPlan.Primary MediumFast45(string name = "") =>
        new(LoraPreset.MediumFast, IsCustom: false, Sf: 9, BwHz: 250_000, Cr: 5,
            FreqMHz: ChannelPlan.FrequencyMHz(Region.US, LoraPreset.MediumFast, 45), Name: name);

    private static MonitorPlan.Result Build(MonitorPlan.Primary primary, uint rateHz,
                                            IReadOnlyList<MonitorPlan.CustomListener>? custom = null,
                                            IReadOnlyCollection<string>? excluded = null) =>
        MonitorPlan.Build(Region.US, primary, RadioDeviceKind.HackRf, rateHz, HackRfRates,
                          enabled: true, excluded ?? Array.Empty<string>(), null, custom);

    /// <summary>Everything else about the band excluded, so the assertions are
    /// about the hand-made listener and nothing else.</summary>
    private static string[] EveryPreset() => Enum.GetNames<LoraPreset>();

    [Fact]
    public void AHandMadeListenerIsListenedForUnderItsOwnName()
    {
        var mine = new MonitorPlan.CustomListener("Backyard net", 10, 125_000, 6, 913.875, Enabled: true);
        var plan = Build(MediumFast45(), 10_000_000, [mine], EveryPreset());

        var listener = Assert.Single(plan.Listeners, l => !l.IsPrimary);
        Assert.Equal("Backyard net", listener.Name);
        Assert.True(listener.IsCustom);
        Assert.Equal(10, listener.Sf);
        Assert.Equal(125_000u, listener.BwHz);
        Assert.Equal(6, listener.Cr);
        Assert.Equal(913.875, listener.FreqMHz, 6);
    }

    /// <summary>Out of the capture is out: it keeps its place in the window,
    /// with the reason and the rate that would bring it in.</summary>
    [Fact]
    public void OneTheCaptureCannotReachIsLeftOutRatherThanListenedFor()
    {
        // Settings that match no preset at all, so the left-out row also
        // shows what a mesh with no preset behind it reports.
        var far = new MonitorPlan.CustomListener("Far side", 12, 250_000, 6, 927.000, Enabled: true);
        var plan = Build(MediumFast45(), 2_400_000, [far], EveryPreset());

        Assert.DoesNotContain(plan.Listeners, l => l.Name == "Far side");
        var left = Assert.Single(plan.LeftOut, x => x.Name == "Far side");
        Assert.Equal(MonitorPlan.LeftOutReason.OutOfRange, left.Reason);
        Assert.Null(left.Preset);
    }

    /// <summary>And a wide enough capture brings it back, which is what the
    /// left-out row promises.</summary>
    [Fact]
    public void AWiderCaptureBringsItIn()
    {
        var far = new MonitorPlan.CustomListener("Far side", 9, 250_000, 5, 920.000, Enabled: true);

        Assert.DoesNotContain(Build(MediumFast45(), 2_400_000, [far], EveryPreset()).Listeners,
                              l => l.Name == "Far side");
        Assert.Contains(Build(MediumFast45(), 16_000_000, [far], EveryPreset()).Listeners,
                        l => l.Name == "Far side");
    }

    [Fact]
    public void OneTurnedOffIsNotListenedFor()
    {
        var off = new MonitorPlan.CustomListener("Resting", 9, 250_000, 5, 913.875, Enabled: false);
        var plan = Build(MediumFast45(), 10_000_000, [off], EveryPreset());

        Assert.DoesNotContain(plan.Listeners, l => l.Name == "Resting");
        Assert.Equal(MonitorPlan.LeftOutReason.Excluded,
                     Assert.Single(plan.LeftOut, x => x.Name == "Resting").Reason);
    }

    /// <summary>The primary's own channel is not a second mesh, however it was
    /// arrived at. Listening for it again would put a second demodulator on
    /// the identical channel and decode every packet there twice.</summary>
    [Fact]
    public void OneThatIsThePrimaryIsNotASecondMesh()
    {
        var same = new MonitorPlan.CustomListener("Twin", 9, 250_000, 5,
            ChannelPlan.FrequencyMHz(Region.US, LoraPreset.MediumFast, 45), Enabled: true);
        var plan = Build(MediumFast45(), 10_000_000, [same], EveryPreset());

        Assert.DoesNotContain(plan.Listeners, l => !l.IsPrimary);
        Assert.Equal(MonitorPlan.LeftOutReason.IsPrimary,
                     Assert.Single(plan.LeftOut, x => x.Name == "Twin").Reason);
    }

    /// <summary>Settings that land exactly on a preset are reported as being
    /// on that mesh, so the app can seal for it and name it the way it names
    /// every other.</summary>
    [Fact]
    public void OneThatLandsOnAPresetSaysWhichPresetItIs()
    {
        var longFastElsewhere = new MonitorPlan.CustomListener("Club net", 11, 250_000, 5, 914.500, Enabled: true);
        var plan = Build(MediumFast45(), 10_000_000, [longFastElsewhere], EveryPreset());

        var listener = Assert.Single(plan.Listeners, l => !l.IsPrimary);
        Assert.Equal(LoraPreset.LongFast, listener.Preset);
        // Named by its operator, not by what it amounts to: the name is what
        // its channels belong to.
        Assert.Equal("Club net", listener.Name);
    }

    /// <summary>The primary is a mesh like any other and can be named. Left
    /// unnamed it keeps what it has always been called.</summary>
    [Fact]
    public void ThePrimaryTakesTheNameItIsGiven()
    {
        Assert.Equal(nameof(LoraPreset.MediumFast),
                     Build(MediumFast45(), 2_400_000).Listeners[0].Name);

        Assert.Equal("Home net",
                     Build(MediumFast45("Home net"), 2_400_000).Listeners[0].Name);
    }

    /// <summary>
    /// Auto puts the capture in the middle of what it takes in.
    /// </summary>
    /// <remarks>
    /// Noah's own setup: MediumFast on 913.125 with LongFast and LongTurbo
    /// beside it, at 10 MS/s. Every position from 909.200 to 910.800 receives
    /// all three, and the search used to stop at the one nearest the primary —
    /// 910.800, which puts LongFast's lower edge exactly on the capture's own
    /// edge, where the front end is already rolling off. The midpoint of the
    /// three leaves the same room on both sides.
    /// </remarks>
    [Fact]
    public void AutoPutsTheCaptureInTheMiddleOfWhatItTakesIn()
    {
        string[] excluded = Enum.GetNames<LoraPreset>()
            .Where(n => n is not (nameof(LoraPreset.LongFast) or nameof(LoraPreset.LongTurbo)))
            .ToArray();
        var plan = Build(MediumFast45(), 10_000_000, custom: null, excluded: excluded);

        Assert.Equal(3, plan.Listeners.Count);

        double lowest = plan.Listeners.Min(l => l.LowEdgeMHz);
        double highest = plan.Listeners.Max(l => l.HighEdgeMHz);
        Assert.Equal((lowest + highest) / 2.0, plan.DeviceCenterMHz, 3);

        // Which is to say: the same room below the lowest as above the highest.
        double below = lowest - (plan.DeviceCenterMHz - plan.UsableHalfSpanMHz);
        double above = (plan.DeviceCenterMHz + plan.UsableHalfSpanMHz) - highest;
        Assert.Equal(below, above, 3);
        Assert.True(below > 0, "centring should leave room on both sides, not sit on an edge");
    }

    /// <summary>Centring never costs a listener: the capture is held inside
    /// the range over which the whole set still fits.</summary>
    [Fact]
    public void CentringDoesNotDropAnyone()
    {
        string[] excluded = Enum.GetNames<LoraPreset>()
            .Where(n => n is not (nameof(LoraPreset.LongFast) or nameof(LoraPreset.LongTurbo)))
            .ToArray();

        foreach (var rate in new uint[] { 4_000_000, 8_000_000, 10_000_000, 16_000_000 })
        {
            var plan = Build(MediumFast45(), rate, custom: null, excluded: excluded);
            foreach (var l in plan.Listeners)
            {
                Assert.True(l.LowEdgeMHz >= plan.DeviceCenterMHz - plan.UsableHalfSpanMHz - 1e-6,
                            $"{l.Name} fell off the bottom at {rate}");
                Assert.True(l.HighEdgeMHz <= plan.DeviceCenterMHz + plan.UsableHalfSpanMHz + 1e-6,
                            $"{l.Name} fell off the top at {rate}");
            }
        }
    }

    /// <summary>And a hand-made listener is centred with the rest — the
    /// capture is placed around everything it receives.</summary>
    [Fact]
    public void AHandMadeListenerIsCentredWithTheRest()
    {
        var mine = new MonitorPlan.CustomListener("Wide net", 11, 500_000, 8, 911.500, Enabled: true);
        var plan = Build(MediumFast45(), 10_000_000, [mine], EveryPreset());

        Assert.Equal(2, plan.Listeners.Count);
        double lowest = plan.Listeners.Min(l => l.LowEdgeMHz);
        double highest = plan.Listeners.Max(l => l.HighEdgeMHz);
        Assert.Equal((lowest + highest) / 2.0, plan.DeviceCenterMHz, 3);
    }

    /// <summary>Several at once, each its own mesh. This is the case a single
    /// "Custom" name could never have served.</summary>
    [Fact]
    public void SeveralAreSeveralMeshes()
    {
        MonitorPlan.CustomListener[] mine =
        [
            new("North", 10, 125_000, 5, 912.000, Enabled: true),
            new("South", 10, 125_000, 5, 914.000, Enabled: true),
        ];
        var plan = Build(MediumFast45(), 10_000_000, mine, EveryPreset());

        var names = plan.Listeners.Where(l => !l.IsPrimary).Select(l => l.Name).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("North", names);
        Assert.Contains("South", names);
    }
}
