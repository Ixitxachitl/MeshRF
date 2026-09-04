// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>The skyline in one direction: how far above (or below) horizontal
/// the highest ground in that direction sits, and which ground that is.
///
/// <paramref name="DistanceM"/> is the part worth acting on. A ridge that
/// defines the skyline from two hundred metres away can be beaten by raising
/// the antenna a few metres; one at twenty kilometres cannot be beaten at all.
/// </summary>
public readonly record struct HorizonPoint(
    double BearingDegrees,
    double ElevationAngleDeg,
    double DistanceM,
    double GroundM);

/// <summary>A full turn of skyline as seen from one antenna.</summary>
public sealed record HorizonProfile(
    GeoPoint Centre,
    double ObserverElevationM,
    double RadiusM,
    double SampleSpacingM,
    IReadOnlyList<HorizonPoint> Points)
{
    /// <summary>The direction the skyline stands highest in.</summary>
    public HorizonPoint Highest => Points.MaxBy(p => p.ElevationAngleDeg);

    /// <summary>How much of the turn has ground standing above horizontal.
    /// Zero is a station that can see its own horizon all the way round.
    /// </summary>
    public double FractionObstructed =>
        Points.Count == 0 ? 0 : Points.Count(p => p.ElevationAngleDeg > 0) / (double)Points.Count;
}

/// <summary>Something plotted against the skyline — normally a node, placed
/// where it would appear if you stood at the antenna and looked at it.</summary>
/// <param name="OccludingAngleDeg">The highest angle anything <em>between</em>
/// the antenna and it subtends. Deliberately not the drawn skyline: that is the
/// whole horizon, and ground beyond a node cannot hide it. Over open country the
/// horizon sits higher than a node halfway to it, which would read every near
/// neighbour as blocked.</param>
public readonly record struct HorizonTarget(
    string Name,
    double BearingDegrees,
    double DistanceM,
    double ElevationAngleDeg,
    double OccludingAngleDeg)
{
    /// <summary>Whether the ground leaves a clear sight of it. Geometry only:
    /// being visible says nothing about whether the link closes, just that
    /// terrain is not in the way.</summary>
    public bool IsVisible => ElevationAngleDeg > OccludingAngleDeg;

    /// <summary>How far it clears, or falls short of, whatever stands between.
    /// A node a fraction of a degree under is one a taller mast at either end
    /// recovers.</summary>
    public double ClearanceDeg => ElevationAngleDeg - OccludingAngleDeg;
}

/// <summary>What a panorama sweep needs.</summary>
/// <param name="RadiusM">How far out to look. A near radius resolves the ridge
/// at the end of the garden; a far one shows the mountains behind it. Both are
/// real skylines, at different scales.</param>
public sealed record HorizonOptions(
    GeoPoint Centre,
    double AntennaM,
    double RadiusM = 15_000,
    int Bearings = 720,
    int SamplesPerBearing = 400,
    double EarthRadiusFactor = 4.0 / 3.0);

/// <summary>
/// The 360° skyline from one antenna: for every bearing, the highest angle any
/// ground in that direction subtends.
///
/// Purely geometric. Nothing here knows about frequency, power or path loss —
/// it answers "what can this antenna see", which is the question behind where
/// to put a node and how high, and which no amount of link budget will answer.
/// </summary>
/// <remarks>
/// Follows MeshLab RF's horizon panorama
/// (https://github.com/HarukiToreda/MeshLab-RF, MIT,
/// Copyright (c) 2026 HarukiToreda).
/// </remarks>
public static class HorizonPanorama
{
    /// <summary>
    /// Sweeps the skyline. Returns null when the antenna's own ground cannot be
    /// read, since every angle is measured from it.
    /// </summary>
    public static HorizonProfile? Build(IElevationSource terrain, HorizonOptions options)
    {
        if (options.Bearings < 3)
            throw new ArgumentOutOfRangeException(nameof(options), "a panorama needs at least three bearings");
        if (options.RadiusM <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "the radius has to be positive");

        if (terrain.ElevationAt(options.Centre.Lat, options.Centre.Lon) is not double centreGround)
            return null;

        double observerM = centreGround + options.AntennaM;
        double effectiveRadius = Geodesy.EarthRadiusM * options.EarthRadiusFactor;
        double spacing = options.RadiusM / options.SamplesPerBearing;

        var points = new HorizonPoint[options.Bearings];

        Parallel.For(0, options.Bearings, bearingIndex =>
        {
            double bearing = 360.0 * bearingIndex / options.Bearings;

            // Starts at the geometric horizon of a featureless earth, so a
            // direction with no ground standing in it reports the horizon
            // rather than an angle left over from nothing.
            double bestAngle = double.NegativeInfinity;
            double bestDistance = options.RadiusM;
            double bestGround = centreGround;

            for (int k = 1; k <= options.SamplesPerBearing; k++)
            {
                double distance = k * spacing;
                var at = CoverageMap.Along(options.Centre, bearing, distance);
                if (terrain.ElevationAt(at.Lat, at.Lon) is not double ground) break;

                double angle = ElevationAngleDeg(observerM, ground, distance, effectiveRadius);
                if (angle <= bestAngle) continue;

                bestAngle = angle;
                bestDistance = distance;
                bestGround = ground;
            }

            if (double.IsNegativeInfinity(bestAngle))
                bestAngle = ElevationAngleDeg(observerM, centreGround, options.RadiusM, effectiveRadius);

            points[bearingIndex] = new HorizonPoint(bearing, bestAngle, bestDistance, bestGround);
        });

        return new HorizonProfile(options.Centre, observerM, options.RadiusM, spacing, points);
    }

    /// <summary>
    /// How far above horizontal a point of ground appears from the antenna,
    /// in degrees. Negative for anything below the line of sight, which most
    /// distant ground is: the earth curves away faster than terrain rises.
    /// </summary>
    public static double ElevationAngleDeg(
        double observerM, double targetGroundM, double distanceM, double effectiveEarthRadiusM)
    {
        if (distanceM <= 0)
            throw new ArgumentOutOfRangeException(nameof(distanceM), "distance has to be positive");

        // The earth falling away under the sight line, which is what puts a
        // distant ridge below the horizon however tall it is.
        double drop = distanceM * distanceM / (2 * effectiveEarthRadiusM);
        return Math.Atan2(targetGroundM - drop - observerM, distanceM) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Places points of interest against a swept skyline — normally the nodes
    /// on the map, so a glance says which of them the ground hides.
    /// </summary>
    /// <param name="targetAntennaM">Height assumed for each target above its
    /// own ground. A node on a two-metre pole clears a ridge its ground does
    /// not.</param>
    public static IReadOnlyList<HorizonTarget> Place(
        HorizonProfile profile,
        IElevationSource terrain,
        IEnumerable<(string Name, GeoPoint At)> targets,
        double targetAntennaM,
        double earthRadiusFactor = 4.0 / 3.0)
    {
        double effectiveRadius = Geodesy.EarthRadiusM * earthRadiusFactor;
        var placed = new List<HorizonTarget>();

        foreach (var (name, at) in targets)
        {
            double distance = Geodesy.DistanceM(profile.Centre, at);

            // Beyond the sweep there is no skyline to compare against, and at
            // the antenna itself there is no direction to place it in.
            if (distance <= 0 || distance > profile.RadiusM) continue;
            if (terrain.ElevationAt(at.Lat, at.Lon) is not double ground) continue;

            double bearing = BearingDeg(profile.Centre, at);
            double angle = ElevationAngleDeg(
                profile.ObserverElevationM, ground + targetAntennaM, distance, effectiveRadius);

            double occluding = HighestBetween(
                terrain, profile, bearing, distance, effectiveRadius);

            placed.Add(new HorizonTarget(name, bearing, distance, angle, occluding));
        }

        return placed;
    }

    /// <summary>The highest angle any ground short of a target subtends, which
    /// is the only ground that can hide it.</summary>
    private static double HighestBetween(
        IElevationSource terrain, HorizonProfile profile,
        double bearing, double distanceM, double effectiveRadius)
    {
        double highest = double.NegativeInfinity;

        for (double d = profile.SampleSpacingM; d < distanceM; d += profile.SampleSpacingM)
        {
            var at = CoverageMap.Along(profile.Centre, bearing, d);
            if (terrain.ElevationAt(at.Lat, at.Lon) is not double ground) break;

            double angle = ElevationAngleDeg(profile.ObserverElevationM, ground, d, effectiveRadius);
            if (angle > highest) highest = angle;
        }

        // Nothing in between at all — a target inside the first step. Then the
        // only thing that could hide it is the antenna's own mast, which this
        // does not model.
        return double.IsNegativeInfinity(highest) ? double.NegativeInfinity : highest;
    }

    /// <summary>Initial bearing from one point to another, in degrees from
    /// north.</summary>
    public static double BearingDeg(GeoPoint from, GeoPoint to)
    {
        double lat1 = from.Lat * Math.PI / 180, lat2 = to.Lat * Math.PI / 180;
        double dLon = (to.Lon - from.Lon) * Math.PI / 180;

        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0;
    }
}
