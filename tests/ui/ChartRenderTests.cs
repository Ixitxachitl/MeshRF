// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using MeshRF.AvaloniaApp;
using MeshRF.Map;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The RF charts draw themselves into a <see cref="Avalonia.Media.DrawingContext"/>
/// rather than composing controls, so nothing but the pixels can say whether a
/// layer was drawn at all.
///
/// Thresholds sit a long way under what a correct render produces. Each asks
/// only "was this drawn", and a tighter bound would break every time a padding
/// or a stroke width changed.
/// </summary>
public class ChartRenderTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private const int W = 900, H = 560;

    /// <summary>The plotting area, inside the axis gutters every chart keeps.
    /// </summary>
    private static readonly PixelRect Plot = new(60, 20, W - 90, H - 60);

    // -- Link profile -------------------------------------------------------

    /// <summary>Rolling ground with a ridge that punches through the sight
    /// line partway along.</summary>
    private static LinkProfile ObstructedProfile()
    {
        var ground = new List<(double, double)>();
        for (int i = 0; i <= 300; i++)
        {
            double baseM = 300 + 30 * Math.Sin(i / 22.0);
            double ridge = 180 * Math.Exp(-Math.Pow((i - 120) / 12.0, 2));
            ground.Add((i * 40.0, baseM + ridge));
        }
        return LinkProfile.Build(ground, 6, 3, 906.875);
    }

    [Fact]
    public void TheLinkProfileDrawsTerrainASightLineAndTheFresnelZone() => Ui(() =>
    {
        var chart = new LinkProfileChart();
        chart.Show(ObstructedProfile(), UnitSystem.Metric, "Here", "There");

        var image = Rendered.Draw(chart, W, H);

        int terrain = image.CountNear("#3C3A33", within: Plot);
        int sight = image.CountNear("#4FC3F7", tolerance: 40, within: Plot);
        int fresnel = image.CountNear("#FFB74D", tolerance: 40, within: Plot);

        Assert.True(terrain > 5000, $"no terrain fill ({terrain} px)");
        Assert.True(sight > 60, $"no sight line ({sight} px)");
        Assert.True(fresnel > 50, $"no 60% Fresnel guide ({fresnel} px)");
    });

    [Fact]
    public void GroundAboveTheSightLineIsFilledAsBlocked() => Ui(() =>
    {
        // The verdict at a glance, and the only cue that separates a grazing
        // path from an obstructed one without reading the figures.
        var chart = new LinkProfileChart();
        chart.Show(ObstructedProfile(), UnitSystem.Metric, "Here", "There");

        // A tight tolerance on purpose: the blocked brown and the terrain
        // brown differ by thirty in the red channel alone, so a loose match
        // counts the whole silhouette as blocked.
        int blocked = Rendered.Draw(chart, W, H).CountNear("#5A3230", tolerance: 15, within: Plot);
        Assert.True(blocked > 40, $"no blocked fill over the ridge ({blocked} px)");
    });

    [Fact]
    public void AClearPathDrawsNoBlockedFill() => Ui(() =>
    {
        var flat = new List<(double, double)>();
        for (int i = 0; i <= 300; i++) flat.Add((i * 20.0, 200));

        var chart = new LinkProfileChart();
        chart.Show(LinkProfile.Build(flat, 40, 40, 906.875), UnitSystem.Metric, "Here", "There");

        int blocked = Rendered.Draw(chart, W, H).CountNear("#5A3230", tolerance: 15, within: Plot);
        Assert.True(blocked < 20, $"a clear path should have no blocked fill, got {blocked} px");
    });

    // -- Path loss ----------------------------------------------------------

    /// <summary>Neighbours generated from a known model.</summary>
    private static List<(PathLossObservation Observation, bool Included)> Neighbours(
        double exponent, params double[] distances)
    {
        double reference = LinkBudget.FreeSpacePathLossAtOneMetreDb(906.875);
        return distances
            .Select((d, i) => (
                new PathLossObservation(
                    (uint)i, $"n{i}", d, -5, 0,
                    reference + 10 * exponent * Math.Log10(d), true),
                true))
            .ToList();
    }

    [Fact]
    public void ThePathLossChartDrawsItsPointsTheFitAndFreeSpace() => Ui(() =>
    {
        var observations = Neighbours(3.1, 400, 1200, 3500, 9000, 20000);
        var fit = PathLossFit.Fit(observations.Select(o => o.Observation.ToSample()).ToList(), 906.875);

        var chart = new PathLossChart();
        chart.Show(observations, fit, 906.875, UnitSystem.Metric);

        var image = Rendered.Draw(chart, W, H);

        int dots = image.CountNear("#4FC3F7", tolerance: 40, within: Plot);
        int fitted = image.CountNear("#FFB74D", tolerance: 40, within: Plot);
        int freeSpace = image.CountNear("#66BB6A", tolerance: 40, within: Plot);

        Assert.True(dots > 40, $"no neighbour dots ({dots} px)");
        Assert.True(fitted > 60, $"no fitted line ({fitted} px)");
        Assert.True(freeSpace > 40, $"no free-space reference line ({freeSpace} px)");
    });

    [Fact]
    public void NeighboursAllAtOneRangeAreStillDrawn() => Ui(() =>
    {
        // The shape of a real station whose neighbours are all on one mast: the
        // distance axis collapses onto a single value, which is where a chart
        // divides by a zero span and draws nothing at all.
        var observations = Neighbours(2.0, 2832, 2832, 2832, 2832);

        var chart = new PathLossChart();
        chart.Show(observations, null, 906.875, UnitSystem.Metric);

        int dots = Rendered.Draw(chart, W, H).CountNear("#4FC3F7", tolerance: 40, within: Plot);
        Assert.True(dots > 20, $"neighbours at one range were not drawn ({dots} px)");
    });

    [Fact]
    public void AnExcludedNeighbourIsDrawnDifferentlyFromAnIncludedOne() => Ui(() =>
    {
        // Unticking a row has to show on the chart, or pruning an outlier gives
        // no sign that it took.
        var observations = Neighbours(3.0, 500, 2000, 8000);

        int InkWith(bool lastIncluded)
        {
            var points = observations
                .Select((o, i) => (o.Observation, Included: i < observations.Count - 1 || lastIncluded))
                .ToList();

            var chart = new PathLossChart();
            chart.Show(points, null, 906.875, UnitSystem.Metric);
            return Rendered.Draw(chart, W, H).CountNear("#4FC3F7", tolerance: 40, within: Plot);
        }

        int allFilled = InkWith(true), oneHollow = InkWith(false);

        Assert.True(allFilled > 0, $"nothing was drawn ({allFilled} px)");
        Assert.True(allFilled > oneHollow,
            $"a hollow dot should carry less ink than a filled one, got {oneHollow} against {allFilled}");
    });

    // -- Horizon ------------------------------------------------------------

    /// <summary>Level ground with a ridge across the north-east.</summary>
    private sealed class Ridged(GeoPoint centre) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon)
        {
            var here = new GeoPoint(lat, lon);
            double range = Geodesy.DistanceM(centre, here);
            double bearing = HorizonPanorama.BearingDeg(centre, here);
            double off = Math.Abs(((bearing - 45 + 540) % 360) - 180);
            return 240 + (off > 130 ? 120 * Math.Exp(-Math.Pow((range - 1500) / 250.0, 2)) : 0);
        }
    }

    [Fact]
    public void TheHorizonDrawsASkylineAndPlacesNodesAgainstIt() => Ui(() =>
    {
        var centre = new GeoPoint(44.9778, -93.2650);
        var terrain = new Ridged(centre);
        var profile = HorizonPanorama.Build(
            terrain, new HorizonOptions(centre, 10, 8000, Bearings: 360, SamplesPerBearing: 200))!;

        var targets = HorizonPanorama.Place(profile, terrain,
        [
            ("Behind The Ridge", CoverageMap.Along(centre, 45, 4000)),
            ("In The Open", CoverageMap.Along(centre, 225, 3000)),
        ], targetAntennaM: 2);

        var chart = new HorizonChart();
        chart.Show(profile, targets, UnitSystem.Metric);

        var image = Rendered.Draw(chart, W, H);

        int nearGround = image.CountNear("#8D7B62", tolerance: 45, within: Plot);
        int visible = image.CountNear("#66BB6A", tolerance: 40, within: Plot);
        int hidden = image.CountNear("#EF5350", tolerance: 40, within: Plot);

        Assert.True(nearGround > 200, $"no near ground in the silhouette ({nearGround} px)");
        Assert.True(visible > 6, $"no visible node ({visible} px)");
        Assert.True(hidden > 6, $"no hidden node ({hidden} px)");
    });

    [Fact]
    public void TheHorizonDrawsTheHorizontalItIsAllMeasuredAgainst() => Ui(() =>
    {
        // Every angle on the chart is read against this line, and the panorama
        // is meaningless without it. Deliberately not a test that a taller mast
        // lowers the silhouette: the angle axis rescales to whatever it is
        // handed, so the drawing barely moves even when the numbers behind it
        // halve. That belongs to the model's own tests.
        var centre = new GeoPoint(44.9778, -93.2650);
        var profile = HorizonPanorama.Build(
            new Ridged(centre),
            new HorizonOptions(centre, 10, 8000, Bearings: 360, SamplesPerBearing: 200))!;

        var chart = new HorizonChart();
        chart.Show(profile, [], UnitSystem.Metric);

        int horizontal = Rendered.Draw(chart, W, H).CountNear("#4FC3F7", tolerance: 40, within: Plot);
        Assert.True(horizontal > 100, $"no horizontal reference line ({horizontal} px)");
    });

    // -- Horizon: depth, and turning the view -------------------------------

    /// <summary>A low bank a few hundred metres out with a taller ridge five
    /// kilometres behind it, both across the east: one direction at two
    /// distances, which is the whole of what depth has to show.</summary>
    private sealed class RidgeBehindRidge(GeoPoint centre) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon)
        {
            var here = new GeoPoint(lat, lon);
            double bearing = HorizonPanorama.BearingDeg(centre, here);
            if (Math.Abs(((bearing - 90 + 540) % 360) - 180) > 40) return 240;

            double range = Geodesy.DistanceM(centre, here);
            if (Math.Abs(range - 700) < 120) return 300;
            if (Math.Abs(range - 5000) < 400) return 700;
            return 240;
        }
    }

    private static HorizonProfile EastwardRidges()
    {
        var centre = new GeoPoint(44.9778, -93.2650);
        return HorizonPanorama.Build(
            new RidgeBehindRidge(centre),
            new HorizonOptions(centre, 10, 8000, Bearings: 360, SamplesPerBearing: 200))!;
    }

    private static int Ink((byte R, byte G, byte B) p) => p.R + p.G + p.B;

    [Fact]
    public void ANearBankAndTheRidgeBehindItAreShadedApart() => Ui(() =>
    {
        // Both are in the same direction, so a chart that kept only the skyline
        // draws one shape at one distance and says the eastern quadrant is five
        // kilometres off. The bank is what the station actually looks over.
        var chart = new HorizonChart();
        chart.Show(EastwardRidges(), [], UnitSystem.Metric);

        var image = Rendered.Draw(chart, W, H);

        // Due east: a quarter of the way across a whole turn drawn with north
        // at both edges.
        int x = 46 + (int)(0.25 * (W - 46 - 12));
        int floor = H - 26;

        int skyline = -1;
        for (int y = 40; y < floor && skyline < 0; y++)
            if (Ink(image.At(x, y)) > 140) skyline = y;
        Assert.True(skyline > 0, "nothing drawn in the eastern column");

        var far = image.At(x, skyline + 5);
        var near = image.At(x, floor - 8);

        Assert.True(far.B > far.R + 4,
            $"ground five kilometres out should be drawn cool, got {far}");
        Assert.True(near.R > near.B + 10,
            $"the bank down the road should be drawn warm, got {near}");
    });

    /// <summary>Where the mass of high ground sits across the picture. The
    /// eastern ridges are the only thing drawn this far up, so their mean
    /// column says which way the panorama is facing — and the corner captions
    /// sit above the band, out of the reading.</summary>
    private static double RidgeCentreX(Rendered image)
    {
        double sum = 0;
        int count = 0;

        for (int y = 60; y < 200; y++)
            for (int x = 47; x < W - 12; x++)
                if (Ink(image.At(x, y)) > 140) { sum += x; count++; }

        Assert.True(count > 200, $"no high ground to take a bearing from ({count} px)");
        return sum / count;
    }

    [Fact]
    public void DraggingTurnsThePanoramaWithThePointer() => Ui(() =>
    {
        var profile = EastwardRidges();

        var still = new HorizonChart();
        still.Show(profile, [], UnitSystem.Metric);
        double before = RidgeCentreX(Rendered.Draw(still, W, H));

        var turned = new HorizonChart();
        turned.Show(profile, [], UnitSystem.Metric);
        double after = RidgeCentreX(Rendered.Draw(turned, W, H, window =>
        {
            window.MouseMove(new Point(500, 300));
            window.MouseDown(new Point(500, 300), MouseButton.Left);
            window.MouseMove(new Point(450, 300), RawInputModifiers.LeftMouseButton);
            window.MouseMove(new Point(400, 300), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(400, 300), MouseButton.Left);
        }));

        // A hundred pixels of drag across a plot holding the whole turn is some
        // forty degrees of it, and the country goes with the pointer rather
        // than against it.
        Assert.InRange(turned.CentreBearing, 208, 238);
        Assert.InRange(before - after, 70, 130);
    });

    [Fact]
    public void TheWheelNarrowsTheViewAroundThePointer() => Ui(() =>
    {
        var chart = new HorizonChart();
        chart.Show(EastwardRidges(), [], UnitSystem.Metric);

        // On the middle of the plot, which is the one bearing a zoom must leave
        // where it is.
        Rendered.Draw(chart, W, H, window =>
            window.MouseWheel(new Point(46 + (W - 46 - 12) / 2.0, 300), new Vector(0, 3)));

        Assert.True(chart.SpanDegrees < 200, $"the wheel did not zoom in ({chart.SpanDegrees:F0}°)");
        Assert.Equal(180, chart.CentreBearing, 1);
    });

    [Fact]
    public void ADoubleClickPutsTheWholeTurnBack() => Ui(() =>
    {
        var chart = new HorizonChart();
        chart.Show(EastwardRidges(), [], UnitSystem.Metric);

        Rendered.Draw(chart, W, H, window =>
        {
            window.MouseWheel(new Point(300, 300), new Vector(0, 3));
            window.MouseDown(new Point(300, 300), MouseButton.Left);
            window.MouseUp(new Point(300, 300), MouseButton.Left);
            window.MouseDown(new Point(300, 300), MouseButton.Left);
            window.MouseUp(new Point(300, 300), MouseButton.Left);
        });

        Assert.Equal(360, chart.SpanDegrees, 1);
        Assert.Equal(180, chart.CentreBearing, 1);
    });
}
