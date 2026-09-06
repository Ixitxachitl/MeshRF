// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The Presets window says what the receiver will listen for and what is
/// stopping the rest, so the rows have to be the plan's own answer rather
/// than a stored list that can drift from it.
/// </summary>
public class MonitorsWindowTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    /// <summary>A HackRF on Noah's setup: MediumFast in the US band, which is
    /// that preset's own default slot at 913.125 MHz.</summary>
    private static RadioViewModel Station(uint rateHz = 2_400_000u)
    {
        var vm = new RadioViewModel();
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == rateHz);
        return vm;
    }

    [Fact]
    public void OffItIsThePrimaryAloneAndTheWaterfallShowsOneChannel() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();

        var band = Assert.Single(vm.ChannelBands);
        Assert.True(band.IsPrimary);
        Assert.Equal(913.125e6, band.CenterHz, 0);
        Assert.Equal(250_000.0, band.BandwidthHz);

        Assert.Contains("primary alone", vm.MonitorStatusText);
        var primaryRow = Assert.Single(vm.MonitorPresets, r => r.StatusText == "primary");
        Assert.Equal("MediumFast", primaryRow.Name);
        Assert.Equal("slot 45", primaryRow.SlotText);
        Assert.False(primaryRow.CanChoose);
    }));

    [Fact]
    public void ARateTooNarrowForAPresetSaysWhichRateWouldReachIt() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;

        var longFast = Assert.Single(vm.MonitorPresets, r => r.Name == nameof(LoraPreset.LongFast));
        Assert.Equal("906.875 MHz", longFast.FreqText);
        Assert.Contains("out of range", longFast.StatusText);
        Assert.Contains("MS/s", longFast.StatusText);
        Assert.DoesNotContain(vm.ChannelBands, b => b.Label == nameof(LoraPreset.LongFast));
    }));

    [Fact]
    public void AWideEnoughCaptureReachesLongFastAndMarksItOnTheWaterfall() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(10_000_000u);
        vm.MultiPresetEnabled = true;

        var longFast = Assert.Single(vm.MonitorPresets, r => r.Name == nameof(LoraPreset.LongFast));
        Assert.Equal("listening", longFast.StatusText);
        Assert.Contains(vm.ChannelBands, b => b.Label == nameof(LoraPreset.LongFast));
        Assert.Contains(vm.ChannelBands, b => b.IsPrimary && Math.Abs(b.CenterHz - 913.125e6) < 1);
        Assert.Contains("listening", vm.MonitorStatusText);
        Assert.Contains("centre", vm.MonitorStatusText);
    }));

    [Fact]
    public void UntickingAPresetDropsItAndIsRemembered() => Ui(() => TempDataDirectory.With(() =>
    {
        using (var vm = Station(10_000_000u))
        {
            vm.MultiPresetEnabled = true;
            var longFast = vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast));
            Assert.True(longFast.CanChoose);
            Assert.True(longFast.Included);

            longFast.Included = false;

            var after = vm.MonitorPresets.Single(r => r.Name == nameof(LoraPreset.LongFast));
            Assert.False(after.Included);
            Assert.Equal("not listened for", after.StatusText);
            Assert.DoesNotContain(vm.ChannelBands, b => b.Label == nameof(LoraPreset.LongFast));
        }
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));

        using var reopened = Station(10_000_000u);
        Assert.Contains(nameof(LoraPreset.LongFast), reopened.MonitorExcludedPresets);
        Assert.True(reopened.MultiPresetEnabled);
        reopened.RefreshMonitors();
        Assert.DoesNotContain(reopened.ChannelBands, b => b.Label == nameof(LoraPreset.LongFast));
    }));

    /// <summary>
    /// The window itself loads and lays out. A binding to a property that is
    /// not there, or a template over the wrong type, is a warning in a log
    /// nobody reads and an empty row on screen, so the rows are counted.
    /// </summary>
    [Fact]
    public void TheWindowDrawsARowPerPreset() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station(10_000_000u);
        vm.MultiPresetEnabled = true;

        var window = new MonitorsWindow { DataContext = vm };
        window.Show();
        for (int i = 0; i < 8; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants().OfType<CheckBox>().ToList();
        // One tick per preset, plus the feature switch and the Auto centre box.
        Assert.Equal(vm.MonitorPresets.Count + 2, rows.Count);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.Text == vm.MonitorStatusText);

        window.Close();
    }));

    [Fact]
    public void AHardwareModemHearsOneChannelAndTheWindowSaysSo() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.MultiPresetEnabled = true;
        vm.SelectedDevice = RadioDeviceKind.Sx1262;
        vm.RefreshMonitors();

        Assert.False(vm.CanEditMonitors);
        Assert.True(vm.HasMonitorsNote);
        Assert.Contains("one channel at a time", vm.MonitorsUnavailableNote);
        Assert.Single(vm.ChannelBands);
    }));
}
