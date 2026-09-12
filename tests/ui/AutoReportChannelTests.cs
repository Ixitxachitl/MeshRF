// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Each auto report is broadcast on a channel of its own choosing, picked the
/// way the quick-send picker picks one: the channels this station can actually
/// broadcast on, each said to be on its mesh where more than one is in reach.
/// For the position report the channel decides more than where it goes:
/// sharing turned off there means nothing goes out at all, and a channel
/// anyone can decrypt caps how precise a position may be. None of that is
/// visible from the picker, so the row says it.
/// </summary>
public class AutoReportChannelTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private static RadioViewModel Station(uint rateHz = 10_000_000u)
    {
        var vm = new RadioViewModel();
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == rateHz);
        return vm;
    }

    [Fact]
    public void ThePositionRowSaysWhatTheChosenChannelWillActuallyDo() => Ui(() => TempDataDirectory.With(() =>
    {
        var vm = new RadioViewModel();
        vm.RefreshAutoReportChannelOptions();

        var channel = vm.Tabs.OfType<ChannelTabViewModel>().First().Config;
        vm.AutoReportPositionChannel.Selected =
            vm.AutoReportChannelOptions.First(o => o.Channel == channel);

        // Sharing on, at a precision the channel is allowed to use: the report
        // goes out as asked and there is nothing to say.
        channel.PositionPrecision = 13;
        Assert.Equal(string.Empty, vm.PositionChannelNote);
        Assert.False(vm.HasPositionChannelNote);

        // Sharing off: the schedule would run and nothing would ever leave.
        channel.PositionPrecision = 0;
        Assert.True(vm.HasPositionChannelNote);
        Assert.Contains("nothing sent", vm.PositionChannelNote);
        Assert.Contains(channel.Name, vm.PositionChannelNote);

        // Asking for an exact position on a channel with a published key: the
        // cap applies whatever the setting says, so the note names what will
        // really go out rather than what was asked for.
        channel.PositionPrecision = 32;
        if (channel.EffectivePositionPrecision < 32)
        {
            Assert.True(vm.HasPositionChannelNote);
            Assert.Contains("public key", vm.PositionChannelNote);
        }
    }));

    /// <summary>A channel saved before the mesh was recorded alongside it —
    /// and one that has since been renamed or deleted — is resolved against
    /// the primary's list, which is the only place a report could go then.
    /// A name nothing answers to is put back on the primary rather than
    /// leaving the picker on a channel that no longer exists.</summary>
    [Fact]
    public void AChannelThatIsGoneFallsBackToThePrimary() => Ui(() => TempDataDirectory.With(() =>
    {
        var vm = new RadioViewModel();
        vm.AutoReportDeviceMetricsChannel.Restore(string.Empty, "a channel that never existed");
        vm.RefreshAutoReportChannelOptions();

        Assert.Contains(vm.AutoReportDeviceMetricsChannel.Selected, vm.AutoReportChannelOptions);
    }));

    /// <summary>
    /// Every mesh the operator has chosen is offered, whether or not the
    /// receiver is running. A stopped receiver changes nothing about which
    /// channels exist or which one a report belongs on, and a picker that
    /// emptied itself when the receiver stopped would make a saved setting
    /// look lost.
    /// </summary>
    [Fact]
    public void EveryMeshThisStationIsOnIsOffered() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        // Channel lists for every preset in the capture, and no receiver.
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        Assert.Contains(vm.AutoReportChannelOptions, o => o.Preset == vm.PrimaryListName);
        Assert.Contains(vm.AutoReportChannelOptions, o => o.Preset == nameof(LoraPreset.LongFast));
        Assert.All(vm.AutoReportChannelOptions, o => Assert.False(o.Channel.IsDisabled));
        // More than one mesh on offer, so each entry says which it is on.
        Assert.All(vm.AutoReportChannelOptions, o => Assert.Contains(o.Preset, o.Label));
    }));

    /// <summary>
    /// A report nobody has chosen a channel for shows where it would go and
    /// records nothing, so it keeps following the primary mesh. Filling the
    /// picker in counted as a choice once, which pinned every report to
    /// whatever the primary was the first time the dialog was built — a
    /// station that later moved preset went on reporting to the mesh it left.
    /// </summary>
    [Fact]
    public void AnUnchosenReportFollowsThePrimaryRatherThanPinningToIt() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();

        Assert.False(vm.AutoReportPositionChannel.IsSet);
        Assert.Equal(vm.PrimaryListName, vm.AutoReportPositionChannel.Selected!.Preset);

        // The station moves mesh; the unchosen reports move with it.
        vm.SelectedPreset = LoraPreset.LongFast;
        Assert.Equal(nameof(LoraPreset.LongFast), vm.PrimaryListName);
        Assert.False(vm.AutoReportPositionChannel.IsSet);
        Assert.Equal(vm.PrimaryListName, vm.AutoReportPositionChannel.Selected!.Preset);

        // Choosing one is different: that is pinned, and stays put.
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        var chosen = vm.AutoReportChannelOptions.First(o => o.Preset != vm.PrimaryListName);
        vm.AutoReportPositionChannel.Selected = chosen;
        Assert.True(vm.AutoReportPositionChannel.IsSet);

        vm.RefreshAutoReportChannelOptions();
        Assert.Equal(chosen.Channel, vm.AutoReportPositionChannel.Selected!.Channel);
    }));

    /// <summary>
    /// A mesh the operator is done with takes its channels off the offer — but
    /// one already chosen stays, and says it is no longer listened for.
    /// Silently moving a report onto the primary would rewrite the schedule
    /// behind their back.
    /// </summary>
    [Fact]
    public void AChosenChannelSurvivesItsMeshBeingDroppedFromTheOffer() =>
        Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var elsewhere = vm.Tabs.OfType<ChannelTabViewModel>()
            .First(t => t.Config.Preset == nameof(LoraPreset.LongFast)).Config;
        vm.AutoReportNodeStatusChannel.Restore(elsewhere.Preset, elsewhere.Name);
        vm.RefreshAutoReportChannelOptions();

        // Done with that mesh.
        vm.MonitorExcludedPresets.Add(nameof(LoraPreset.LongFast));
        vm.RefreshMonitors();

        var picked = vm.AutoReportNodeStatusChannel.Selected;
        Assert.NotNull(picked);
        Assert.Equal(elsewhere, picked!.Channel);
        Assert.Contains(picked, vm.AutoReportChannelOptions);

        // Said on the option itself: the mesh it is on, and that the station
        // is not on that mesh any more.
        Assert.Contains(nameof(LoraPreset.LongFast), picked.Label);
        Assert.Contains("not listened for", picked.Label);

        // Nobody else is offered it, and the other reports are unmoved.
        Assert.Single(vm.AutoReportChannelOptions, o => o.Preset == nameof(LoraPreset.LongFast));
        Assert.Equal(vm.PrimaryListName, vm.AutoReportPositionChannel.Selected!.Preset);
    }));
}
