// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>One sampled point along a link's terrain cross-section.
/// <paramref name="SightLineM"/> already carries the earth's curvature, so the
/// clearance at a point is simply the gap between it and the ground.</summary>
public readonly record struct ProfilePoint(
    double DistanceM,
    double GroundM,
    double SightLineM,
    double FresnelRadiusM)
{
    /// <summary>Metres between the terrain and the sight line. Negative when
    /// the ground is above the line, which is a blocked path.</summary>
    public double ClearanceM => SightLineM - GroundM;
}

/// <summary>
/// A terrain cross-section between two radios, with the first Fresnel zone and
/// the diffraction loss the terrain imposes on the link.
///
/// The obstruction model is a single knife edge, the ITU-R P.526 approximation:
/// the worst intrusion along the path is treated as one ridge and the rest is
/// ignored. That understates a path blocked by several ridges in a row, and
/// nothing here models reflections, foliage or buildings. It is a screening
/// tool — enough to tell a clear path from a diffracting one and to put a
/// number on how far a marginal one is from clearing.
/// </summary>
/// <remarks>
/// The propagation model follows the approach taken by MeshLab RF
/// (https://github.com/HarukiToreda/MeshLab-RF, MIT licensed,
/// Copyright (c) 2026 HarukiToreda).
/// </remarks>
public sealed record LinkProfile(
    IReadOnlyList<ProfilePoint> Points,
    double DistanceM,
    double DiffractionLossDb,
    double WorstClearanceRatio,
    int WorstIndex)
{
    /// <summary>Fraction of the first Fresnel zone that has to stay clear for
    /// the path to behave as free space. Below this the link starts paying
    /// diffraction loss even though nothing crosses the sight line itself.
    /// </summary>
    public const double FresnelClearanceTarget = 0.6;

    /// <summary>Nothing rises above the straight sight line.</summary>
    public bool HasLineOfSight => WorstClearanceRatio > 0;

    /// <summary>The path is clear enough to behave as free space.</summary>
    public bool IsFresnelClear => WorstClearanceRatio >= FresnelClearanceTarget;

    public ProfilePoint Worst => Points[WorstIndex];

    /// <summary>How much the worst obstruction would have to drop — or the
    /// antennas rise — for the first Fresnel zone to clear it.</summary>
    public double MetresShortOfClearance
    {
        get
        {
            var worst = Worst;
            double needed = FresnelClearanceTarget * worst.FresnelRadiusM;
            return Math.Max(0, needed - worst.ClearanceM);
        }
    }

    /// <summary>
    /// Builds the profile from ground samples taken along the path.
    /// </summary>
    /// <param name="ground">Distance from the first radio and ground elevation,
    /// in order, starting at the first radio and ending at the second.</param>
    /// <param name="txHeightAglM">First radio's antenna, metres above its own
    /// ground.</param>
    /// <param name="rxHeightAglM">Second radio's antenna, above its ground.</param>
    /// <param name="frequencyMhz">Carrier the link runs on.</param>
    /// <param name="earthRadiusFactor">Refraction allowance. The standard 4/3
    /// accounts for the atmosphere bending radio slightly around the horizon,
    /// so a path grazing the earth's bulge is treated as clearer than pure
    /// geometry makes it.</param>
    public static LinkProfile Build(
        IReadOnlyList<(double DistanceM, double GroundM)> ground,
        double txHeightAglM,
        double rxHeightAglM,
        double frequencyMhz,
        double earthRadiusFactor = 4.0 / 3.0)
    {
        if (ground.Count < 2)
            throw new ArgumentException("a profile needs at least both endpoints", nameof(ground));
        if (frequencyMhz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyMhz), "frequency has to be positive");

        double distance = ground[^1].DistanceM;
        if (distance <= 0)
            throw new ArgumentException("the radios are at the same place", nameof(ground));

        double txM = ground[0].GroundM + txHeightAglM;
        double rxM = ground[^1].GroundM + rxHeightAglM;
        double wavelength = 299.792458 / frequencyMhz; // metres
        double effectiveRadius = Geodesy.EarthRadiusM * earthRadiusFactor;

        var points = new ProfilePoint[ground.Count];
        double worstRatio = double.MaxValue;
        double worstV = double.MinValue;
        int worstIndex = 0;

        for (int i = 0; i < ground.Count; i++)
        {
            double d1 = Math.Clamp(ground[i].DistanceM, 0, distance);
            double d2 = distance - d1;

            // The sight line is straight; bending it down by the earth's bulge
            // is the same geometry as drawing curved ground under a straight
            // line, and keeps the plotted terrain as real elevations.
            double bulge = d1 * d2 / (2 * effectiveRadius);
            double sight = txM + (rxM - txM) * (d1 / distance) - bulge;
            double fresnel = Math.Sqrt(wavelength * d1 * d2 / distance);

            points[i] = new ProfilePoint(d1, ground[i].GroundM, sight, fresnel);

            // The endpoints are the antennas themselves: their Fresnel radius
            // is zero, so a clearance ratio there is a division by zero and an
            // obstruction there is meaningless.
            if (i == 0 || i == ground.Count - 1) continue;

            double clearance = sight - ground[i].GroundM;
            double ratio = clearance / fresnel;
            if (ratio < worstRatio)
            {
                worstRatio = ratio;
                worstIndex = i;
            }

            // Tracked separately from the worst ratio: the deepest intrusion
            // into the Fresnel zone and the strongest knife edge need not be
            // the same sample, and the loss belongs to the latter.
            double v = -clearance * Math.Sqrt(2 / wavelength * (1 / d1 + 1 / d2));
            if (v > worstV) worstV = v;
        }

        if (worstRatio == double.MaxValue)
        {
            // Endpoints only: nothing between them was sampled, so there is
            // nothing to obstruct the path.
            worstRatio = double.PositiveInfinity;
            worstV = double.NegativeInfinity;
        }

        return new LinkProfile(points, distance, KnifeEdgeLossDb(worstV), worstRatio, worstIndex);
    }

    /// <summary>
    /// Single knife-edge diffraction loss, the ITU-R P.526 approximation.
    /// </summary>
    /// <param name="v">Fresnel-Kirchhoff diffraction parameter: positive when
    /// the edge rises above the sight line, negative when it stays below.</param>
    public static double KnifeEdgeLossDb(double v)
    {
        // Below this the edge is far enough under the sight line that it takes
        // nothing out of the wave.
        if (double.IsNegativeInfinity(v) || v <= -0.78) return 0;
        return 6.9 + 20 * Math.Log10(Math.Sqrt((v - 0.1) * (v - 0.1) + 1) + v - 0.1);
    }
}
