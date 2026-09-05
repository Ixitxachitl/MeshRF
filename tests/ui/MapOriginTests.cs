// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
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

    /// <summary>The crosshair answers the pointer the way a node marker does.
    /// Without that there is no way to tell you are over the point you already
    /// chose — and no way to click it, since two clicks by hand never land on
    /// the same coordinates.</summary>
    [Fact]
    public void TheChosenPointAnswersTheHoverLikeAMarker() => Ui(() => TempDataDirectory.With(() =>
    {
        const double Lat = 51.5, Lon = -0.12;
        const int W = 400, H = 300;

        var canvas = new MapCanvas();
        canvas.Attach(new RadioViewModel());
        canvas.CenterOn(Lat, Lon);
        canvas.SetChosenPoint(Lat, Lon);

        // Centred on the point, so the crosshair is drawn in the middle.
        Assert.Contains("Chosen point", HoverAt(canvas, W, H, new Point(W / 2.0, H / 2.0)));

        // And bare map a little way off says nothing, so the tooltip is the
        // crosshair answering rather than the map answering everywhere.
        Assert.Null(HoverAt(canvas, W, H, new Point(W / 2.0 + 60, H / 2.0 + 60)));
    }));

    /// <summary>Shows the map, draws it once so its hit targets exist, then
    /// moves the real pointer over it and reports the tooltip that came up —
    /// which is how a marker says it is under the pointer.</summary>
    private static string? HoverAt(MapCanvas canvas, int w, int h, Point at)
    {
        var window = new Window
        {
            Width = w,
            Height = h,
            WindowDecorations = WindowDecorations.None,
            Content = canvas,
        };
        window.Show();
        try
        {
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
            window.MouseMove(at);
            Dispatcher.UIThread.RunJobs();
            return ToolTip.GetTip(canvas) as string;
        }
        finally
        {
            window.Content = null;
            window.Close();
        }
    }
}
