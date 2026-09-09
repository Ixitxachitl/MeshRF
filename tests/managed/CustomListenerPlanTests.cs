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
