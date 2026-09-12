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
    /// The pickers offer what can actually be broadcast on: the primary mesh's
    /// channels, and those of any mesh a listener is up for. A channel on a
    /// mesh nothing is listening for has no settings of its own to go out on,
    /// so a report addressed to it would be sealed with one mesh's key and put
    /// on the air with another's — noise to everyone who hears it.
    /// </summary>
    [Fact]
    public void OnlyTheMeshesInReachAreOffered() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        // Channel lists for every preset in the capture, but no receiver: the
        // primary's mesh is the only one this station is on.
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        vm.RefreshAutoReportChannelOptions();

        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(),
                        t => t.Config.Preset == nameof(LoraPreset.LongFast));
        Assert.All(vm.AutoReportChannelOptions,
                   o => Assert.Equal(vm.PrimaryListName, o.Preset));
        Assert.All(vm.AutoReportChannelOptions, o => Assert.False(o.Channel.IsDisabled));
    }));

    /// <summary>
    /// A choice on a mesh that has gone quiet stays on the list and stays
    /// picked, saying it is out of reach. Stopping the receiver is not the
    /// operator changing where their reports go, and silently moving them onto
    /// the primary would rewrite the schedule behind their back.
    /// </summary>
    [Fact]
    public void AChosenChannelSurvivesItsMeshGoingQuiet() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();

        var elsewhere = vm.Tabs.OfType<ChannelTabViewModel>()
            .First(t => t.Config.Preset == nameof(LoraPreset.LongFast)).Config;
        vm.AutoReportNodeStatusChannel.Restore(elsewhere.Preset, elsewhere.Name);
        vm.RefreshAutoReportChannelOptions();

        var picked = vm.AutoReportNodeStatusChannel.Selected;
        Assert.NotNull(picked);
        Assert.Equal(elsewhere, picked!.Channel);
        Assert.Contains(picked, vm.AutoReportChannelOptions);

        // Said on the option itself: the mesh it is on, and that nothing is
        // listening for it.
        Assert.Contains(nameof(LoraPreset.LongFast), picked.Label);
        Assert.Contains("not listening", picked.Label);

        // The others are unmoved by it and stay on the primary's mesh.
        Assert.Equal(vm.PrimaryListName, vm.AutoReportPositionChannel.Selected!.Preset);
    }));
}
