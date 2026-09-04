// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;

namespace MeshRF.Map;

/// <summary>How a direction fared against the range it would have had over
/// open ground.</summary>
public enum CoverageQuality
{
    /// <summary>Terrain took nothing worth counting: the reach here is the
    /// range the link budget alone allows.</summary>
    Clear,

    /// <summary>Terrain cut the reach back, but the direction still carries.
    /// </summary>
    Weakened,

    /// <summary>Something close in stops this direction almost at once.
    /// </summary>
    Blocked,
}

/// <summary>
/// The link margin everywhere the sweep looked, as a polar grid of bearings by
/// range.
///
/// The ring reports where contiguous coverage ends, which is the honest answer
/// to "how far do I reach". This is the other half of the picture: the hilltop
/// past a blocked valley that the ring deliberately will not claim, and the
/// gradient either side of the boundary that a single edge cannot show.
/// </summary>
public sealed record CoverageField(
    GeoPoint Centre, double SpacingM, int Bearings, int Samples, float[] MarginDb)
{
    /// <summary>How far out the grid was filled.</summary>
    public double RadiusM => Samples * SpacingM;

    /// <summary>
    /// The margin at a point, interpolated between the four surrounding grid
    /// cells, or null past the edge of the sweep.
    /// </summary>
    public double? MarginAt(double bearingDegrees, double distanceM)
    {
        if (distanceM <= 0 || distanceM > RadiusM) return null;

        // Bearing wraps; range does not. The grid holds sample k at range
        // (k + 1) × spacing, so the first ring of cells sits one step out.
        double b = ((bearingDegrees % 360) + 360) % 360 / 360.0 * Bearings;
        double s = distanceM / SpacingM - 1;
        if (s < 0) s = 0;

        int b0 = (int)Math.Floor(b), s0 = (int)Math.Floor(s);
        double bf = b - b0, sf = s - s0;

        int b1 = (b0 + 1) % Bearings;
        b0 %= Bearings;
        int s1 = Math.Min(s0 + 1, Samples - 1);
        s0 = Math.Min(s0, Samples - 1);

        double near = MarginDb[b0 * Samples + s0] + (MarginDb[b1 * Samples + s0] - MarginDb[b0 * Samples + s0]) * bf;
        double far = MarginDb[b0 * Samples + s1] + (MarginDb[b1 * Samples + s1] - MarginDb[b0 * Samples + s1]) * bf;
        return near + (far - near) * sf;
    }
}

/// <summary>One compass direction's result.</summary>
public readonly record struct CoverageSpoke(
    double BearingDegrees, double ReachM, CoverageQuality Quality);

/// <summary>
/// How far this station reaches in every direction, and where the ground stops
/// it. <paramref name="UnobstructedRangeM"/> is what the same radio does over
/// open ground from the same spot — curvature included — which is what each
/// spoke is judged against.
/// </summary>
public sealed record CoverageRing(
    GeoPoint Centre,
    IReadOnlyList<CoverageSpoke> Spokes,
    double UnobstructedRangeM,
    bool RangeWasCapped = false,
    CoverageField? Field = null)
{
    public double FurthestReachM => Spokes.Count == 0 ? 0 : Spokes.Max(s => s.ReachM);

    public int CountOf(CoverageQuality quality) => Spokes.Count(s => s.Quality == quality);
}

/// <summary>The radio and station facts a coverage sweep needs.</summary>
/// <param name="PeerAntennaM">Height assumed for whatever is being reached.
/// Coverage is a question about hypothetical receivers, so this is the one
/// number that describes them.</param>
/// <param name="RequiredMarginDb">Headroom over the demodulator's floor that
/// still counts as reach. Zero is the cliff edge, where fading alone drops the
/// link; a few decibels describes somewhere a packet actually gets through.
/// </param>
/// <param name="Calibration">A fitted local path-loss model, when there is
/// one. Without it the sweep spends free-space loss, which over anywhere with
/// trees or buildings draws a ring far larger than the station really has.
/// </param>
/// <param name="MaxCredibleRangeM">How far the caller is willing to have the
/// model asked about, or zero for no limit. Beyond the ranges a model was
/// fitted over it is extrapolating, and a log-distance model extrapolated two
/// orders of magnitude past its data will happily draw a ring across a
/// continent. Stopping at a stated distance is honest; drawing that ring is
/// not.</param>
public sealed record CoverageOptions(
    GeoPoint Centre,
    double MyAntennaM,
    double PeerAntennaM,
    double MyGainDbi,
    double PeerGainDbi,
    double TxPowerDbm,
    double FrequencyMhz,
    double BandwidthKhz,
    int SpreadingFactor,
    double NoiseFigureDb = LinkBudget.DefaultNoiseFigureDb,
    double RequiredMarginDb = 6.0,
    PathLossFit? Calibration = null,
    double MaxCredibleRangeM = 0,
    BuildingIndex? Buildings = null,
    BuildingLossModel? BuildingLoss = null,
    int Bearings = 180,
    double EarthRadiusFactor = 4.0 / 3.0);

/// <summary>
/// Sweeps the compass from one station, walking outward along each bearing
/// until the link stops closing, and reports how far it got and whether the
/// ground was what stopped it.
///
/// The reach along a bearing is where contiguous coverage ends — the first
/// range that fails, not the furthest that happens to work. A hilltop eight
/// kilometres out with a blocked valley in front of it is a real place a packet
/// can reach, but drawing it as part of one ring would claim everything nearer
/// works too, which is the opposite of what the map is for.
/// </summary>
/// <remarks>
/// The idea, and the clear/weakened/blocked reading of it, follow MeshLab RF's
/// beacon profile (https://github.com/HarukiToreda/MeshLab-RF, MIT,
/// Copyright (c) 2026 HarukiToreda). The propagation underneath is this app's
/// own, so where a calibration has been fitted the ring is drawn against
/// measured local path loss rather than against free space.
/// </remarks>
public static class CoverageMap
{
    /// <summary>At or above this fraction of the open-ground range, terrain
    /// has not meaningfully cost the direction anything.</summary>
    public const double ClearFraction = 0.9;

    /// <summary>Below this fraction, the direction is stopped rather than
    /// shortened.</summary>
    public const double BlockedFraction = 0.25;

    /// <summary>Steps taken outward along each bearing. Enough that the
    /// boundary lands within a fraction of a percent of the range, and few
    /// enough that the whole sweep stays interactive.</summary>
    private const int RadialSamples = 220;

    /// <summary>Steps in the open-ground reference walk. It runs once rather
    /// than once per bearing, so it can afford to land the reference range
    /// precisely — every spoke is then compared against a number that is not
    /// itself quantised.</summary>
    private const int ReferenceSamples = 512;

    /// <summary>How far the link budget alone reaches, with no earth in the
    /// way at all. Not a range anyone gets: at LoRa's sensitivity this runs to
    /// hundreds of kilometres, and the horizon arrives long first. It is the
    /// ceiling the open-ground search starts from.</summary>
    public static double BudgetRangeM(CoverageOptions options)
    {
        double budget = options.TxPowerDbm + options.MyGainDbi + options.PeerGainDbi
                      - LinkBudget.SensitivityDbm(
                            options.SpreadingFactor, options.BandwidthKhz, options.NoiseFigureDb)
                      - options.RequiredMarginDb;

        return options.Calibration is { } fit
            ? LinkBudget.RangeForLossDb(budget, options.FrequencyMhz, fit.Exponent, fit.OffsetDb)
            : LinkBudget.RangeForLossDb(budget, options.FrequencyMhz);
    }

    /// <summary>
    /// How far this radio reaches over level open ground — the number every
    /// direction is judged against, and the radius of terrain a sweep has to
    /// read.
    ///
    /// Callable without any terrain at all, and independent of the station's
    /// own elevation: over level ground the elevation appears in both the sight
    /// line and the ground under it, and cancels. Only the two antenna heights
    /// above that ground matter.
    /// </summary>
    public static double OpenGroundRangeM(CoverageOptions options)
    {
        double gains = options.TxPowerDbm + options.MyGainDbi + options.PeerGainDbi;
        double allowedLoss = gains
            - LinkBudget.SensitivityDbm(
                options.SpreadingFactor, options.BandwidthKhz, options.NoiseFigureDb)
            - options.RequiredMarginDb;

        double wavelength = 299.792458 / options.FrequencyMhz;
        double effectiveRadius = Geodesy.EarthRadiusM * options.EarthRadiusFactor;

        return OpenGroundReach(
            options, centreGround: 0, txM: options.MyAntennaM, allowedLoss,
            wavelength, effectiveRadius, BudgetRangeM(options), ReferenceSamples);
    }

    /// <summary>
    /// Runs the sweep. Returns null when the station's own ground cannot be
    /// read, since every sight line starts there.
    /// </summary>
    public static CoverageRing? Build(IElevationSource terrain, CoverageOptions options)
    {
        if (options.Bearings < 3)
            throw new ArgumentOutOfRangeException(nameof(options), "a ring needs at least three bearings");

        if (terrain.ElevationAt(options.Centre.Lat, options.Centre.Lon) is not double centreGround)
            return null;

        double txM = centreGround + options.MyAntennaM;
        double gains = options.TxPowerDbm + options.MyGainDbi + options.PeerGainDbi;
        double sensitivity = LinkBudget.SensitivityDbm(
            options.SpreadingFactor, options.BandwidthKhz, options.NoiseFigureDb);
        double allowedLoss = gains - sensitivity - options.RequiredMarginDb;

        double wavelength = 299.792458 / options.FrequencyMhz;
        double effectiveRadius = Geodesy.EarthRadiusM * options.EarthRadiusFactor;

        // What this radio does over open ground from here, found by the same
        // walk across level terrain. That, rather than the link budget's own
        // reach, is what a direction is judged against: the budget ignores the
        // earth, and past the horizon the ground is what ends the link.
        //
        // Measured twice. The first pass starts from the budget's reach, which
        // is hundreds of kilometres, so its step is coarse; the second re-walks
        // it at the step the sweep itself will use. Without that, level ground
        // reads slightly further than its own reference — the two walks were
        // quantised differently — and the ring never quite closes on Clear.
        double coarse = OpenGroundReach(
            options, centreGround, txM, allowedLoss, wavelength, effectiveRadius,
            BudgetRangeM(options), ReferenceSamples);

        // The model stops being evidence before it stops producing numbers.
        // Capping the reference rather than the drawn edge keeps the reading
        // coherent: a direction that runs to the cap met nothing, which is
        // Clear, and one cut short by ground is still judged against what the
        // rest of the ring managed.
        bool capped = options.MaxCredibleRangeM > 0 && coarse > options.MaxCredibleRangeM;
        if (capped) coarse = options.MaxCredibleRangeM;

        // Looking past it is wasted work — nothing out there closes even with
        // no terrain at all — and it also sets how finely each bearing is
        // stepped, so a station that only reaches a few kilometres reads its
        // terrain at a few tens of metres. A capped sweep takes no headroom
        // past the cap: there is nothing out there it is willing to claim.
        double maxRange = capped ? coarse : coarse * 1.05;
        double spacing = maxRange / RadialSamples;

        double unobstructed = OpenGroundReach(
            options, centreGround, txM, allowedLoss, wavelength, effectiveRadius,
            maxRange, RadialSamples);

        var spokes = new CoverageSpoke[options.Bearings];

        // Margin everywhere, not just at the boundary. Filling it costs the
        // walk continuing past the first failure instead of stopping there,
        // which is the same arithmetic either way — the reach is still read off
        // where it first fails.
        var margins = new float[options.Bearings * RadialSamples];
        double headroom = gains - sensitivity;

        Parallel.For(0, options.Bearings, bearingIndex =>
        {
            double bearing = 360.0 * bearingIndex / options.Bearings;
            double reach = Sweep(
                terrain, options, bearing, centreGround, txM, allowedLoss,
                wavelength, effectiveRadius, spacing, maxRange,
                field: margins, fieldOffset: bearingIndex * RadialSamples, headroom: headroom);

            spokes[bearingIndex] = new CoverageSpoke(bearing, reach, Classify(reach, unobstructed));
        });

        var field = new CoverageField(
            options.Centre, spacing, options.Bearings, RadialSamples, margins);

        return new CoverageRing(options.Centre, spokes, unobstructed, capped, field);
    }

    /// <summary>Level ground everywhere, for measuring the reference reach.
    /// </summary>
    private sealed class LevelGround(double metres) : IElevationSource
    {
        public double? ElevationAt(double lat, double lon) => metres;
    }

    /// <summary>The reach over open ground, which curvature alone can cut to a
    /// fraction of what the link budget promises. One bearing is enough: level
    /// ground is the same in every direction.</summary>
    private static double OpenGroundReach(
        CoverageOptions options, double centreGround, double txM, double allowedLoss,
        double wavelength, double effectiveRadius, double maxRange, int samples) =>
        Sweep(
            new LevelGround(centreGround),
            // Open ground means open: the reference is what this radio does
            // with nothing in the way, which is what the spokes are compared
            // against. Charging it for buildings would move the yardstick
            // along with the thing being measured.
            options with { Buildings = null },
            bearingDegrees: 0,
            centreGround, txM, allowedLoss, wavelength, effectiveRadius,
            spacing: maxRange / samples, maxRange: maxRange, samples: samples);

    private static CoverageQuality Classify(double reachM, double unobstructedM)
    {
        if (unobstructedM <= 0) return CoverageQuality.Blocked;
        double fraction = reachM / unobstructedM;
        return fraction >= ClearFraction ? CoverageQuality.Clear
             : fraction >= BlockedFraction ? CoverageQuality.Weakened
             : CoverageQuality.Blocked;
    }

    /// <summary>Walks one bearing outward and returns how far contiguous
    /// coverage extends along it.</summary>
    private static double Sweep(
        IElevationSource terrain, CoverageOptions options, double bearingDegrees,
        double centreGround, double txM, double allowedLoss,
        double wavelength, double effectiveRadius, double spacing, double maxRange,
        int samples = RadialSamples,
        float[]? field = null, int fieldOffset = 0, double headroom = 0)
    {
        // Ground under the bearing, filled in as the walk goes out, so a
        // direction that stops early never reads terrain past where it stopped.
        var ground = new double[samples + 1];
        ground[0] = centreGround;

        double fresnelScale = Math.Sqrt(2 / wavelength);
        double reached = 0;
        bool failed = false;

        // Buildings accumulate as the walk goes out rather than being counted
        // afresh at every range. A footprint entered once stays entered, so the
        // running total is exactly what a path to this sample has crossed —
        // and the whole bearing costs one pass instead of one per sample.
        bool countBuildings = options.Buildings is { Count: > 0 } && options.BuildingLoss is not null;
        var crossedSoFar = BuildingCrossing.None;
        var previousPoint = options.Centre;

        for (int k = 1; k <= samples; k++)
        {
            double distance = k * spacing;
            if (distance > maxRange) break;

            var at = Along(options.Centre, bearingDegrees, distance);
            if (terrain.ElevationAt(at.Lat, at.Lon) is not double elevation) break;
            ground[k] = elevation;

            if (countBuildings)
            {
                crossedSoFar = crossedSoFar.Plus(options.Buildings!.Along(previousPoint, at));
                previousPoint = at;
            }

            double rxM = elevation + options.PeerAntennaM;

            // The worst knife edge between here and the station. Recomputed
            // per step rather than carried forward: the sight line tilts as the
            // far end moves, so an edge that cleared it at the last step can
            // cut through it at this one.
            double worstV = double.NegativeInfinity;
            for (int j = 1; j < k; j++)
            {
                double d1 = j * spacing;
                double d2 = distance - d1;
                double bulge = d1 * d2 / (2 * effectiveRadius);
                double sight = txM + (rxM - txM) * (d1 / distance) - bulge;
                double v = (ground[j] - sight) * fresnelScale * Math.Sqrt(1 / d1 + 1 / d2);
                if (v > worstV) worstV = v;
            }

            double loss = PathLossDb(distance, options) + LinkProfile.KnifeEdgeLossDb(worstV)
                        + (countBuildings ? options.BuildingLoss!.LossDb(crossedSoFar) : 0);
            if (field is not null) field[fieldOffset + k - 1] = (float)(headroom - loss);

            // The reach is the first failure, but the walk carries on so the
            // field behind it is filled: a hilltop past a blocked valley is a
            // real place, and the ring alone would never show it.
            if (loss > allowedLoss) { failed = true; continue; }
            if (!failed) reached = distance;
        }

        return reached;
    }

    private static double PathLossDb(double distanceM, CoverageOptions options) =>
        options.Calibration is { } fit
            ? fit.PathLossDb(distanceM, options.FrequencyMhz)
            : LinkBudget.FreeSpacePathLossDb(distanceM, options.FrequencyMhz);

    /// <summary>The point a distance away on a bearing, along the great circle.
    /// </summary>
    public static GeoPoint Along(GeoPoint from, double bearingDegrees, double distanceM)
    {
        double angular = distanceM / Geodesy.EarthRadiusM;
        double bearing = bearingDegrees * Math.PI / 180.0;
        double lat = from.Lat * Math.PI / 180.0;
        double lon = from.Lon * Math.PI / 180.0;

        double sinLat = Math.Sin(lat) * Math.Cos(angular)
                      + Math.Cos(lat) * Math.Sin(angular) * Math.Cos(bearing);
        double destLat = Math.Asin(Math.Clamp(sinLat, -1, 1));
        double destLon = lon + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat),
            Math.Cos(angular) - Math.Sin(lat) * sinLat);

        return new GeoPoint(destLat * 180.0 / Math.PI, destLon * 180.0 / Math.PI);
    }
}
