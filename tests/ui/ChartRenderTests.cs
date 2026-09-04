// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
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
}
