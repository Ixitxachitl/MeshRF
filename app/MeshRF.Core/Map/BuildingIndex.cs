// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>A building footprint, as a closed ring of points in degrees.
/// </summary>
public sealed record Footprint(IReadOnlyList<GeoPoint> Outline)
{
    public double MinLat { get; } = Outline.Min(p => p.Lat);
    public double MaxLat { get; } = Outline.Max(p => p.Lat);
    public double MinLon { get; } = Outline.Min(p => p.Lon);
    public double MaxLon { get; } = Outline.Max(p => p.Lon);
}

/// <summary>What a path met on its way through the buildings.</summary>
public readonly record struct BuildingCrossing(int Count, double MetresInside)
{
    public static readonly BuildingCrossing None = new(0, 0);

    public BuildingCrossing Plus(BuildingCrossing other) =>
        new(Count + other.Count, MetresInside + other.MetresInside);
}

/// <summary>
/// How much a building costs a signal passing through it.
/// </summary>
/// <param name="PerCrossingDb">A flat charge for each footprint entered, which
/// is the two walls.</param>
/// <param name="PerHundredMetresInsideDb">What the contents cost, per hundred
/// metres of path inside a footprint — a long diagonal through a warehouse is
/// not the same as clipping a corner.</param>
/// <remarks>
/// The defaults are MeshLab RF's, fitted to a paired field survey: 10.8 dB per
/// crossed footprint plus 0.3 dB per 100 m inside. They are that project's
/// measurements of its own region, not a law of nature — somewhere with
/// different construction wants different numbers, which is what the path-loss
/// calibration is for.
/// </remarks>
public sealed record BuildingLossModel(
    double PerCrossingDb = 10.8,
    double PerHundredMetresInsideDb = 0.3)
{
    public double LossDb(BuildingCrossing crossing) =>
        crossing.Count * PerCrossingDb
        + crossing.MetresInside / 100.0 * PerHundredMetresInsideDb;
}

/// <summary>
/// Building footprints, indexed so a path can ask what it crosses without
/// testing every one.
///
/// The index is a uniform grid of cells holding the footprints that touch them.
/// A radial sweep asks this question a few hundred thousand times, and against
/// a city's worth of buildings a linear scan is the difference between a sweep
/// that takes a moment and one that never finishes.
/// </summary>
public sealed class BuildingIndex
{
    /// <summary>Grid cell size in degrees of latitude, about 110 m. Small
    /// enough that a cell holds a handful of buildings, large enough that a
    /// long segment does not walk thousands of cells.</summary>
    private const double CellDegrees = 0.001;

    private readonly Dictionary<(int X, int Y), List<Footprint>> _cells = [];

    public int Count { get; }

    public BuildingIndex(IEnumerable<Footprint> footprints)
    {
        foreach (var footprint in footprints)
        {
            Count++;
            for (int y = Cell(footprint.MinLat); y <= Cell(footprint.MaxLat); y++)
                for (int x = Cell(footprint.MinLon); x <= Cell(footprint.MaxLon); x++)
                {
                    if (!_cells.TryGetValue((x, y), out var bucket))
                        _cells[(x, y)] = bucket = [];
                    bucket.Add(footprint);
                }
        }
    }

    public static BuildingIndex Empty { get; } = new([]);

    /// <summary>
    /// What one straight step of a path crosses: how many footprints it enters,
    /// and how far it travels inside them.
    ///
    /// A step that begins inside a footprint does not count as entering it
    /// again, which is what makes this safe to accumulate along a walk — the
    /// charge for a wall is paid once however finely the path is sampled.
    /// </summary>
    public BuildingCrossing Along(GeoPoint from, GeoPoint to)
    {
        if (Count == 0) return BuildingCrossing.None;

        double length = Geodesy.DistanceM(from, to);
        if (length <= 0) return BuildingCrossing.None;

        var crossing = BuildingCrossing.None;

        foreach (var footprint in Candidates(from, to))
        {
            bool startsInside = Contains(footprint, from);
            bool endsInside = Contains(footprint, to);

            // The fraction of this step spent inside, from where it crosses the
            // outline. Both ends inside means all of it; neither, and only a
            // chord through a corner is left.
            double inside = InsideFraction(footprint, from, to, startsInside, endsInside) * length;

            // Entering counts when the step starts outside and any of it is
            // spent within, so a wall is charged once no matter how many steps
            // the path is cut into afterwards.
            int entered = !startsInside && inside > 0 ? 1 : 0;

            crossing = crossing.Plus(new BuildingCrossing(entered, inside));
        }

        return crossing;
    }

    /// <summary>Total loss along a whole path, walked in steps.</summary>
    public BuildingCrossing AlongPath(GeoPoint from, GeoPoint to, double stepM = 25)
    {
        double length = Geodesy.DistanceM(from, to);
        if (length <= 0 || Count == 0) return BuildingCrossing.None;

        int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, stepM)));
        var total = BuildingCrossing.None;
        var previous = from;

        for (int i = 1; i <= steps; i++)
        {
            var next = Geodesy.Interpolate(from, to, i / (double)steps);
            total = total.Plus(Along(previous, next));
            previous = next;
        }

        return total;
    }

    /// <summary>Footprints whose cells the segment passes through.</summary>
    private IEnumerable<Footprint> Candidates(GeoPoint from, GeoPoint to)
    {
        var seen = new HashSet<Footprint>();

        int x0 = Cell(Math.Min(from.Lon, to.Lon)), x1 = Cell(Math.Max(from.Lon, to.Lon));
        int y0 = Cell(Math.Min(from.Lat, to.Lat)), y1 = Cell(Math.Max(from.Lat, to.Lat));

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                if (!_cells.TryGetValue((x, y), out var bucket)) continue;
                foreach (var footprint in bucket)
                    if (seen.Add(footprint)) yield return footprint;
            }
    }

    /// <summary>How much of a segment lies inside a footprint, as a fraction.
    /// Sampled rather than clipped: a footprint is an arbitrary polygon, the
    /// segments are short, and an exact clip buys precision the 10 dB constant
    /// in front of it cannot use.</summary>
    private static double InsideFraction(
        Footprint footprint, GeoPoint from, GeoPoint to, bool startsInside, bool endsInside)
    {
        if (startsInside && endsInside) return 1;

        const int probes = 8;
        int within = 0;
        for (int i = 0; i < probes; i++)
        {
            var at = Geodesy.Interpolate(from, to, (i + 0.5) / probes);
            if (Contains(footprint, at)) within++;
        }

        return within / (double)probes;
    }

    /// <summary>Even-odd crossing test. Footprints are simple rings, so the
    /// winding rule makes no difference and this is the cheaper one.</summary>
    public static bool Contains(Footprint footprint, GeoPoint point)
    {
        if (point.Lat < footprint.MinLat || point.Lat > footprint.MaxLat ||
            point.Lon < footprint.MinLon || point.Lon > footprint.MaxLon) return false;

        var ring = footprint.Outline;
        bool inside = false;

        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            bool straddles = ring[i].Lat > point.Lat != ring[j].Lat > point.Lat;
            if (!straddles) continue;

            double at = (ring[j].Lon - ring[i].Lon) * (point.Lat - ring[i].Lat)
                        / (ring[j].Lat - ring[i].Lat) + ring[i].Lon;
            if (point.Lon < at) inside = !inside;
        }

        return inside;
    }

    private static int Cell(double degrees) => (int)Math.Floor(degrees / CellDegrees);
}
