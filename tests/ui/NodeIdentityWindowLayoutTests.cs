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
            Assert.True(selected is string { Length: > 0 },
                $"{name} is on {selected ?? "nothing"}");
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
            vm.AutoReportPositionChannel = channel.Name;

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
