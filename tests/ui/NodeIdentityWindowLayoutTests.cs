// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Each auto-report row is a horizontal run of controls in a fixed-width
/// dialog, and the dialog scrolls vertically only. A row that outgrows the
/// window does not throw or wrap — its last control simply sits past the right
/// edge where nobody sees it, which is what adding the channel picker risked.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class NodeIdentityWindowLayoutTests
{
    private readonly HeadlessAvalonia _avalonia;

    public NodeIdentityWindowLayoutTests(HeadlessAvalonia avalonia) => _avalonia = avalonia;

    [Theory]
    [InlineData("AutoNodeInfoChannelCombo")]
    [InlineData("AutoPositionChannelCombo")]
    [InlineData("AutoDeviceMetricsChannelCombo")]
    [InlineData("AutoEnvironmentMetricsChannelCombo")]
    [InlineData("AutoAirQualityMetricsChannelCombo")]
    [InlineData("AutoNodeStatusChannelCombo")]
    public void EveryAutoReportChannelPickerFitsInsideTheDialog(string name) =>
        _avalonia.Run(() => TempDataDirectory.With(() =>
        {
            var vm = new RadioViewModel();
            vm.RefreshAutoReportChannelOptions();

            var window = new NodeIdentityWindow { DataContext = vm };
            window.Show();
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

            var combo = window.FindControl<ComboBox>(name);
            Assert.True(combo is not null, $"no {name} in the dialog");

            var topLeft = combo!.TranslatePoint(default, window);
            Assert.True(topLeft is not null, $"{name} was not laid out");
            double right = topLeft!.Value.X + combo.Bounds.Width;
            double available = window.ClientSize.Width;

            // Every channel this station can broadcast on is offered, and the
            // picker lands on one of them rather than on nothing.
            object? selected = combo.SelectedItem;
            int offered = vm.AutoReportChannelOptions.Count;

            window.Close();

            Assert.True(right <= available,
                $"{name} ends at {right:0.#} px in a {available:0.#} px dialog");
            Assert.True(offered > 0, "no channels were offered");
            Assert.True(selected is ChannelOffer { Label.Length: > 0 },
                $"{name} is on {selected ?? "nothing"}");
        }));

    /// <summary>
    /// And it still fits once the offer has to say which mesh each channel is
    /// on. That is the widest the picker ever gets: a mesh name, a channel
    /// name, and — for a channel already chosen on a mesh the operator has
    /// since dropped — the note saying it is not listened for.
    /// </summary>
    [Fact]
    public void TheChannelPickersFitWithTheMeshNamedOnEveryEntry() =>
        _avalonia.Run(() => TempDataDirectory.With(() =>
        {
            using var vm = new RadioViewModel
            {
                SelectedDevice = RadioDeviceKind.HackRf,
                SelectedRegion = Region.US,
                SelectedPreset = LoraPreset.MediumFast,
            };
            vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
            vm.MultiPresetEnabled = true;
            vm.RefreshMonitors();

            // Addressed to a channel on a mesh the operator has since dropped:
            // the picker keeps it, names its mesh, and says it is not listened
            // for — the longest an entry gets.
            var elsewhere = vm.Tabs.OfType<ChannelTabViewModel>()
                .First(t => t.Config.Preset == nameof(LoraPreset.LongFast)).Config;
            vm.AutoReportNodeStatusChannel.Restore(elsewhere.Preset, elsewhere.Name);
            vm.MonitorExcludedPresets.Add(nameof(LoraPreset.LongFast));
            vm.RefreshMonitors();

            var window = new NodeIdentityWindow { DataContext = vm };
            window.Show();
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

            var combo = window.FindControl<ComboBox>("AutoNodeStatusChannelCombo")!;
            var topLeft = combo.TranslatePoint(default, window);
            double right = topLeft!.Value.X + combo.Bounds.Width;
            double available = window.ClientSize.Width;
            var label = (combo.SelectedItem as ChannelOffer)?.Label ?? string.Empty;

            window.Close();

            Assert.Contains(nameof(LoraPreset.LongFast), label);
            Assert.True(right <= available,
                $"the picker ends at {right:0.#} px in a {available:0.#} px dialog, on \"{label}\"");
        }));

    /// <summary>The warning that the chosen channel shares no location is the
    /// only thing standing between a schedule that looks armed and one that
    /// sends nothing, so it has to be legible rather than merely present: it
    /// wraps onto a line of its own instead of running off the right edge the
    /// way the notes packed into a row do.</summary>
    [Fact]
    public void TheSharingOffWarningIsReadableInsideTheDialog() =>
        _avalonia.Run(() => TempDataDirectory.With(() =>
        {
            var vm = new RadioViewModel();
            vm.RefreshAutoReportChannelOptions();

            var channel = vm.Tabs.OfType<ChannelTabViewModel>().First().Config;
            channel.PositionPrecision = 0;
            vm.AutoReportPositionChannel.Selected =
                vm.AutoReportChannelOptions.First(o => o.Channel == channel);

            var window = new NodeIdentityWindow { DataContext = vm };
            window.Show();
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

            var note = window.GetVisualDescendants().OfType<TextBlock>()
                             .FirstOrDefault(t => t.Text is { } text && text.Contains("nothing sent"));
            Assert.True(note is not null, "the sharing-off warning was not shown");

            var topLeft = note!.TranslatePoint(default, window);
            Assert.True(topLeft is not null, "the warning was not laid out");
            double right = topLeft!.Value.X + note.Bounds.Width;
            double available = window.ClientSize.Width;
            double height = note.Bounds.Height;

            window.Close();

            Assert.True(right <= available,
                $"the warning ends at {right:0.#} px in a {available:0.#} px dialog");
            Assert.True(height > 0, "the warning was given no height");
        }));
}
