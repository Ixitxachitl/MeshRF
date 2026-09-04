// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The path-loss panel is a fixed 250 px column, so a control added to it
/// either fits or is silently trimmed. Nothing throws when a label is clipped;
/// it just reads as "Impo…" on the one machine nobody tested on.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class PathLossWindowLayoutTests
{
    private readonly HeadlessAvalonia _avalonia;

    public PathLossWindowLayoutTests(HeadlessAvalonia avalonia) => _avalonia = avalonia;

    [Theory]
    [InlineData("ImportSurveyButton")]
    [InlineData("ExportSurveyButton")]
    [InlineData("ClearSurveyButton")]
    [InlineData("RemeasureButton")]
    [InlineData("ApplyButton")]
    [InlineData("ClearButton")]
    public void EveryButtonInTheSidePanelHasRoomForItsLabel(string name)
    {
        _avalonia.Run(() =>
        {
            var window = new PathLossWindow { Width = 1060, Height = 740 };
            window.Show();
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

            var button = window.FindControl<Button>(name);
            Assert.NotNull(button);

            // Measured again unconstrained. DesiredSize as laid out is clamped
            // to the slot the button was given, so comparing the two as they
            // stand would agree however badly the label overflowed.
            double got = button!.Bounds.Width;
            button.InvalidateMeasure();
            button.Measure(Size.Infinity);
            double wanted = button.DesiredSize.Width;

            window.Close();

            Assert.True(got >= wanted - 0.5,
                $"{name} wants {wanted:0.#} px and was given {got:0.#}");
            Assert.True(got > 0, $"{name} was not laid out at all");
        });
    }
}
