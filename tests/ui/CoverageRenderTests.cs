// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using MeshRF.AvaloniaApp;
using MeshRF.Map;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The coverage layers over the basemap: the shaded field, the reach outline,
/// and the circle showing what the radio has actually managed.
/// </summary>
public class CoverageRenderTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private const int W = 700, H = 700;

    private static readonly GeoPoint Centre = new(44.9778, -93.2650);

    /// <summary>Level ground with a ridge across the east, close enough in to
    /// bite.</summary>
    private sealed class Ridged : IElevationSource
    {
        public double? ElevationAt(double lat, double lon)
        {
            var here = new GeoPoint(lat, lon);
            double range = Geodesy.DistanceM(Centre, here);
            double bearing = CoverageMap.Along(Centre, 0, 1).Lat >= 0
                ? HorizonPanorama.BearingDeg(Centre, here)
                : 0;
            double off = Math.Abs(((bearing - 90 + 540) % 360) - 180);
            return 200 + (off > 140 ? 140 * Math.Exp(-Math.Pow((range - 1400) / 300.0, 2)) : 0);
        }
    }

    /// <summary>A station in ordinary clutter, so the ring lands within a few
    /// kilometres and terrain has something to say about it.</summary>
    private static CoverageOptions Options() =>
        new(Centre,
            MyAntennaM: 10, PeerAntennaM: 2,
            MyGainDbi: 2.15, PeerGainDbi: 2.15,
            TxPowerDbm: 22,
            FrequencyMhz: 906.875,
            BandwidthKhz: 250,
            SpreadingFactor: 9,
            Calibration: new PathLossFit(3.2, 0, 3, 8, ExponentFitted: true, OffsetFitted: true),
            Bearings: 180);

    private static MapCanvas CanvasShowing(CoverageRing ring, double measuredReachM = 0)
    {
        var canvas = new MapCanvas();

        // No basemap: these tests are about what the coverage layers draw, and
        // a map underneath would both colour the samples and put the suite on
        // the network.
        canvas.SetTileTheme("None");
        canvas.CenterOn(Centre.Lat, Centre.Lon, zoom: 13);
        canvas.ShowCoverage(ring, "test", measuredReachM, UnitSystem.Metric);
        return canvas;
    }

    [Fact]
    public void TheFieldIsShadedGreenWhereTheLinkIsStrongAndRedWhereItIsNot() => Ui(() =>
    {
        var ring = CoverageMap.Build(new Ridged(), Options())!;
        var image = Rendered.Draw(CanvasShowing(ring), W, H);

        // Near the station the odds are certain; out at the fringe they are
        // not, and the shading has to show both or it is showing nothing.
        int strong = image.Count(p => p.G > p.R + 12 && p.G > p.B + 8);
        int weak = image.Count(p => p.R > p.G + 12 && p.R > p.B + 8);

        Assert.True(strong > 2000, $"no strong coverage shaded ({strong} px)");
        Assert.True(weak > 200, $"no weak coverage shaded ({weak} px)");
    });

    [Fact]
    public void TheShadingStopsWhereTheSweepDid() => Ui(() =>
    {
        // A corner of the window is far outside a few-kilometre ring at this
        // zoom, and has to be left as basemap.
        var ring = CoverageMap.Build(new Ridged(), Options())!;
        var image = Rendered.Draw(CanvasShowing(ring), W, H);

        int corner = image.Count(
            p => p.G > p.R + 12 || p.R > p.G + 12, new PixelRect(0, 0, 40, 40));

        Assert.True(corner < 40, $"the far corner should be unshaded, got {corner} px");
    });

    [Fact]
    public void TheMeasuredReachIsDrawnBesideThePrediction() => Ui(() =>
    {
        // The one circle on the map that was measured rather than modelled.
        var ring = CoverageMap.Build(new Ridged(), Options())!;

        int withCircle = Rendered.Draw(CanvasShowing(ring, 900), W, H)
            .CountNear("#4FC3F7", tolerance: 45);
        int without = Rendered.Draw(CanvasShowing(ring), W, H)
            .CountNear("#4FC3F7", tolerance: 45);

        Assert.True(withCircle > without + 100,
            $"the measured circle added {withCircle - without} px, which is not a circle");
    });

    [Fact]
    public void WhatASweepIsWaitingOnIsShownOnTheMap() => Ui(() =>
    {
        // A sweep on a cold cache pulls a hundred-odd terrain tiles and can sit
        // for a minute. The status bar alone is the wrong place to say so — the
        // user is looking at the map.
        var canvas = new MapCanvas();
        canvas.SetTileTheme("None");
        canvas.CenterOn(Centre.Lat, Centre.Lon, zoom: 13);

        var idle = Rendered.Draw(canvas, W, H);

        canvas.ShowCoverageBusy("Coverage: reading terrain… 12 of 40 tiles");
        var busy = Rendered.Draw(canvas, W, H);

        // The chip sits bottom-left, where the legend goes once there is one.
        var corner = new PixelRect(0, H - 60, 340, 60);
        Assert.True(busy.CountInk(corner) > idle.CountInk(corner) + 200,
            "no busy chip was drawn");
    });

    [Fact]
    public void TheBusyChipGoesAwayWhenTheSweepDoes() => Ui(() =>
    {
        var canvas = new MapCanvas();
        canvas.SetTileTheme("None");
        canvas.CenterOn(Centre.Lat, Centre.Lon, zoom: 13);

        canvas.ShowCoverageBusy("Coverage: reading buildings…");
        int busy = Rendered.Draw(canvas, W, H).CountInk(new PixelRect(0, H - 60, 340, 60));

        canvas.ShowCoverageBusy(null);
        int cleared = Rendered.Draw(canvas, W, H).CountInk(new PixelRect(0, H - 60, 340, 60));

        Assert.True(busy > 200, $"nothing was drawn to begin with ({busy} px)");
        Assert.True(cleared < 40, $"the chip outlived the sweep ({cleared} px)");
    });

    [Fact]
    public void ClearingCoverageLeavesTheMapAlone() => Ui(() =>
    {
        var ring = CoverageMap.Build(new Ridged(), Options())!;

        var canvas = CanvasShowing(ring);
        int shaded = Rendered.Draw(canvas, W, H).Count(p => p.G > p.R + 12 || p.R > p.G + 12);

        canvas.ShowCoverage(null);
        int cleared = Rendered.Draw(canvas, W, H).Count(p => p.G > p.R + 12 || p.R > p.G + 12);

        Assert.True(shaded > 1000, $"nothing was shaded to begin with ({shaded} px)");
        Assert.True(cleared < 40, $"clearing left {cleared} px behind");
    });
}
