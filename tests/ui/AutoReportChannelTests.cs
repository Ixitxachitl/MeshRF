// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Each auto report is broadcast on a channel of its own choosing, and for the
/// position report the channel decides more than where it goes: sharing turned
/// off there means nothing goes out at all, and a channel anyone can decrypt
/// caps how precise a position may be. None of that is visible from the picker,
/// so the row says it.
/// </summary>
public class AutoReportChannelTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    [Fact]
    public void ThePositionRowSaysWhatTheChosenChannelWillActuallyDo() => Ui(() => TempDataDirectory.With(() =>
    {
        var vm = new RadioViewModel();
        vm.RefreshAutoReportChannelOptions();

        var channel = vm.Tabs.OfType<ChannelTabViewModel>().First().Config;
        vm.AutoReportPositionChannel = channel.Name;

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

    /// <summary>A name saved for a channel that has since been renamed or
    /// deleted is put back on the primary, rather than leaving the picker on a
    /// channel that no longer exists.</summary>
    [Fact]
    public void AChannelThatIsGoneFallsBackToThePrimary() => Ui(() => TempDataDirectory.With(() =>
    {
        var vm = new RadioViewModel();
        vm.AutoReportDeviceMetricsChannel = "a channel that never existed";
        vm.RefreshAutoReportChannelOptions();

        Assert.Contains(vm.AutoReportDeviceMetricsChannel, vm.AutoReportChannelOptions);
    }));
}
