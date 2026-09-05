// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A coverage sweep is drawn from one origin and is only true about that
/// origin, but nothing on the field says where it was swept from. So when the
/// chosen point moves — for a horizon, for the near end of a link profile, for
/// any of the tools that share it — the field that is still on the map has
/// quietly become a picture of somewhere else, and it comes down.
/// </summary>
public class MapOriginTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    /// <summary>A panel with no view model behind it: the coverage toggle then
    /// records what was asked for without running a sweep, which needs terrain
    /// this test has no business fetching.</summary>
    private static (MapPanel Panel, MapCanvas Canvas, ToggleButton Coverage) Panel()
    {
        var panel = new MapPanel();
        var canvas = panel.FindControl<MapCanvas>("Canvas");
        var coverage = panel.FindControl<ToggleButton>("CoverageButton");
        Assert.True(canvas is not null && coverage is not null, "map panel is missing its parts");
        return (panel, canvas!, coverage!);
    }

    [Fact]
    public void MovingTheChosenPointTakesCoverageDown() => Ui(() =>
    {
        var (_, canvas, coverage) = Panel();
        coverage.IsChecked = true;

        canvas.SetChosenPoint(51.5, -0.12);

        Assert.False(coverage.IsChecked);
    });

    [Fact]
    public void ChoosingAPointWhileCoverageIsOffLeavesItOff() => Ui(() =>
    {
        var (_, canvas, coverage) = Panel();

        canvas.SetChosenPoint(51.5, -0.12);

        Assert.False(coverage.IsChecked);
    });

    /// <summary>Asking about the point coverage was already swept from is not a
    /// move, so the field it is showing is still the right one.</summary>
    [Fact]
    public void ChoosingThePointAlreadyInUseLeavesCoverageAlone() => Ui(() =>
    {
        var (_, canvas, coverage) = Panel();
        canvas.SetChosenPoint(51.5, -0.12);
        coverage.IsChecked = true;

        canvas.SetChosenPoint(51.5, -0.12);

        Assert.True(coverage.IsChecked);
    });
}
