// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The Listeners window follows the receiver.
///
/// What it offers depends on whether the receiver is running: the set being
/// demodulated is fixed once it starts, so every tick greys out and a note
/// says why. That state was settled when the window was built and never
/// revisited, so starting or stopping the receiver left it offering choices
/// that would do nothing, or refusing ones that would now work, until it was
/// closed and reopened.
/// </summary>
public class MonitorsWindowRefreshTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private static RadioViewModel Station()
    {
        var vm = new RadioViewModel
        {
            SelectedDevice = RadioDeviceKind.HackRf,
            SelectedRegion = Region.US,
            SelectedPreset = LoraPreset.MediumFast,
        };
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        return vm;
    }

    /// <summary>Starting the receiver closes the window's controls where they
    /// stand, without it being reopened.</summary>
    [Fact]
    public void StartingTheReceiverGreysTheRowsOut() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();

        Assert.True(vm.CanEditMonitors);
        Assert.False(vm.HasMonitorsNote);
        Assert.Contains(vm.MonitorPresets, r => r.CanChoose);

        // What Poll does the moment the receiver is up.
        vm.IsRunning = true;

        Assert.False(vm.CanEditMonitors);
        Assert.True(vm.HasMonitorsNote);
        Assert.Contains("Stop the receiver", vm.MonitorsUnavailableNote);
        Assert.DoesNotContain(vm.MonitorPresets, r => r.CanChoose);
    }));

    /// <summary>And stopping it opens them again. This is the half that never
    /// refreshed at all: nothing asked the plan anything on the way down.
    /// </summary>
    /// <remarks>
    /// The rows are built greyed here rather than left to the start above, so
    /// the assertion turns on the stop having rebuilt them. Reading
    /// CanEditMonitors alone would prove nothing either way — it is computed
    /// on read, so it is right whenever it is asked and says nothing about
    /// whether anything was told to ask.
    /// </remarks>
    [Fact]
    public void StoppingTheReceiverOpensThemAgain() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.IsRunning = true;
        vm.RefreshMonitors();
        Assert.DoesNotContain(vm.MonitorPresets, r => r.CanChoose);

        vm.IsRunning = false;

        Assert.Contains(vm.MonitorPresets, r => r.CanChoose);
        Assert.False(vm.HasMonitorsNote);
        Assert.Equal(string.Empty, vm.MonitorsUnavailableNote);
    }));

    /// <summary>
    /// And the window is told to re-read, which is the part a value computed
    /// on demand cannot show. Without a notification the checkbox and the note
    /// keep whatever they were bound to when the window opened, however right
    /// the property is when something finally asks it.
    /// </summary>
    [Fact]
    public void TheBindingsAreToldToReRead() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.IsRunning = true;

        Assert.Contains(nameof(vm.CanEditMonitors), raised);
        Assert.Contains(nameof(vm.MonitorsUnavailableNote), raised);
        Assert.Contains(nameof(vm.HasMonitorsNote), raised);
    }));

    /// <summary>The rows themselves are rebuilt, not just the window's note —
    /// each one carries its own editability, so re-notifying around them would
    /// leave every tick live under a note saying they were not.</summary>
    [Fact]
    public void TheRowsCarryTheChangeNotJustTheNote() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var before = vm.MonitorPresets.Count;
        Assert.True(before > 1, "a multi-preset station should list several presets");

        vm.IsRunning = true;
        Assert.Equal(before, vm.MonitorPresets.Count);
        Assert.All(vm.MonitorPresets, r => Assert.False(r.CanChoose));

        // The primary is never choosable either way, so it is not what the
        // assertion above is picking up.
        vm.IsRunning = false;
        Assert.Contains(vm.MonitorPresets, r => r.CanChoose);
    }));
}
