// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Auto moves the capture whenever something it fits around changes, and the
/// waterfall has to move with it.
/// </summary>
/// <remarks>
/// While the receiver is stopped the waterfall is drawn around the capture it
/// would start on. Only a change of rate, region, device or frequency used to
/// recentre it; ticking a preset, making a listener, turning multi-preset on or
/// typing an offset refitted the plan and moved the bands and the status line,
/// and left the waterfall where it was until one of those other things came
/// along.
/// </remarks>
public class MonitorsRefitTests(HeadlessAvalonia ui) : RenderTest(ui)
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

    private static void AssertCentredOnThePlan(RadioViewModel vm) =>
        Assert.Equal(vm.BuildMonitorPlan().DeviceCenterMHz * 1e6, vm.SpectrumCenterHz, 0);

    /// <summary>Every preset but LongFast and LongTurbo left out, so the
    /// capture has somewhere definite to go when one of them changes.</summary>
    private static void ListenForTheLongPresetsOnly(RadioViewModel vm)
    {
        vm.MonitorExcludedPresets.Clear();
        vm.MonitorExcludedPresets.AddRange(Enum.GetNames<LoraPreset>()
            .Where(n => n is not (nameof(LoraPreset.LongFast) or nameof(LoraPreset.LongTurbo))));
        vm.RefreshMonitors();
    }

    [Fact]
    public void UntickingAPresetMovesTheWaterfallWithTheCapture() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        ListenForTheLongPresetsOnly(vm);
        double before = vm.SpectrumCenterHz;
        AssertCentredOnThePlan(vm);

        // LongFast is the one far below the primary, so dropping it lets the
        // capture come back up toward the rest.
        var longFast = vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast));
        longFast.Included = false;

        Assert.NotEqual(before, vm.SpectrumCenterHz);
        AssertCentredOnThePlan(vm);
    }));

    [Fact]
    public void MakingAListenerMovesTheWaterfallWithTheCapture() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MonitorExcludedPresets.AddRange(Enum.GetNames<LoraPreset>());
        vm.RefreshMonitors();
        double before = vm.SpectrumCenterHz;

        vm.SaveCustomListener(new CustomListenerEdit
        {
            Name = "Below", Sf = 11, BwKhz = 250, Cr = 5, FreqMHz = 910.000,
        }, replacing: null);

        Assert.Contains(vm.BuildMonitorPlan().Listeners, l => l.Name == "Below");
        Assert.NotEqual(before, vm.SpectrumCenterHz);
        AssertCentredOnThePlan(vm);
    }));

    [Fact]
    public void TurningMultiPresetOffPutsTheWaterfallBackOnThePrimary() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        ListenForTheLongPresetsOnly(vm);
        Assert.NotEqual(vm.CenterFreqMHz * 1e6, vm.SpectrumCenterHz, 0);

        vm.MultiPresetEnabled = false;

        Assert.Equal(vm.CenterFreqMHz * 1e6, vm.SpectrumCenterHz, 0);
    }));

    [Fact]
    public void ATypedOffsetMovesTheWaterfall() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MonitorCenterAuto = false;

        vm.MonitorCenterOffsetText = "-1500";

        Assert.Equal((vm.CenterFreqMHz - 1.5) * 1e6, vm.SpectrumCenterHz, 0);
        AssertCentredOnThePlan(vm);
    }));

    /// <summary>
    /// While the receiver runs, what it demodulates was fixed when it started,
    /// so a listener's Edit and remove buttons and the offset box close like
    /// every other control in the window. Left open, an edit rebuilt the rows
    /// from the plan, and the window listed listeners the receiver was not
    /// running.
    /// </summary>
    [Fact]
    public void WhileRunningNothingThatChangesThePlanStaysOpen() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.SaveCustomListener(new CustomListenerEdit
        {
            Name = "Mine", Sf = 11, BwKhz = 250, Cr = 5, FreqMHz = 911.000,
        }, replacing: null);
        vm.MonitorCenterAuto = false;
        Assert.True(vm.CanEditMonitorCenterOffset);
        Assert.True(vm.MonitorPresets.Single(r => r.Name == "Mine").CanEdit);

        vm.IsRunning = true;

        Assert.False(vm.CanEditMonitorCenterOffset);
        Assert.False(vm.MonitorPresets.Single(r => r.Name == "Mine").CanEdit);
        // And a typed offset is refused rather than moving a plan the receiver
        // is not running.
        var offset = vm.MonitorCenterOffsetKHz;
        vm.MonitorCenterOffsetText = "1234";
        Assert.Equal(offset, vm.MonitorCenterOffsetKHz);

        vm.IsRunning = false;

        Assert.True(vm.CanEditMonitorCenterOffset);
        Assert.True(vm.MonitorPresets.Single(r => r.Name == "Mine").CanEdit);
    }));
}
