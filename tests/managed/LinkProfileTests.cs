// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The terrain half of the link prediction: sight line, first Fresnel zone, and
/// the diffraction loss a ridge in the way costs.
/// </summary>
public class LinkProfileTests
{
    private const double Freq915 = 915.0;

    /// <summary>Flat ground at a fixed height, sampled evenly.</summary>
    private static (double, double)[] Flat(double lengthM, double elevationM, int samples = 129)
    {
        var ground = new (double, double)[samples];
        for (int i = 0; i < samples; i++)
            ground[i] = (lengthM * i / (samples - 1), elevationM);
        return ground;
    }

    private static (double, double)[] WithRidge(
        double lengthM, double baseM, double ridgeM, double atFraction = 0.5, int samples = 129)
    {
        var ground = Flat(lengthM, baseM, samples);
        int peak = (int)Math.Round(atFraction * (samples - 1));
        ground[peak] = (ground[peak].Item1, ridgeM);
        return ground;
    }

    [Fact]
    public void AShortFlatPathWithTallAntennasIsClear()
    {
        var profile = LinkProfile.Build(Flat(2000, 100), 20, 20, Freq915);

        Assert.True(profile.HasLineOfSight);
        Assert.True(profile.IsFresnelClear);
        Assert.Equal(0.0, profile.DiffractionLossDb);
        Assert.Equal(0.0, profile.MetresShortOfClearance);
    }

    [Fact]
    public void ARidgeThroughTheSightLineCostsDiffractionLoss()
    {
        var profile = LinkProfile.Build(WithRidge(2000, 100, 160), 10, 10, Freq915);

        Assert.False(profile.HasLineOfSight);
        Assert.False(profile.IsFresnelClear);
        Assert.True(profile.DiffractionLossDb > 15,
            $"a ridge 50 m through the line should cost well over 15 dB, got {profile.DiffractionLossDb:F1}");
    }

    [Fact]
    public void ARidgeInsideTheFresnelZoneButUnderTheSightLineStillCosts()
    {
        // The case the naive "can I see it" check gets wrong: nothing crosses
        // the line, yet the path is not free space.
        var profile = LinkProfile.Build(WithRidge(2000, 100, 108), 10, 10, Freq915);

        Assert.True(profile.HasLineOfSight);
        Assert.False(profile.IsFresnelClear);
        Assert.True(profile.DiffractionLossDb > 0);
        Assert.True(profile.MetresShortOfClearance > 0);
    }

    [Fact]
    public void TheWorstPointIsTheOneReportedAsWorst()
    {
        var profile = LinkProfile.Build(WithRidge(4000, 100, 150, atFraction: 0.25), 10, 10, Freq915);

        Assert.Equal(150, profile.Worst.GroundM);
        Assert.Equal(1000, profile.Worst.DistanceM, 1);
    }

    [Fact]
    public void GrazingTheEdgeCostsTheClassicSixDecibels()
    {
        Assert.Equal(6.03, LinkProfile.KnifeEdgeLossDb(0), 2);
    }

    [Fact]
    public void AnEdgeWellBelowTheSightLineTakesNothing()
    {
        Assert.Equal(0.0, LinkProfile.KnifeEdgeLossDb(-0.78));
        Assert.Equal(0.0, LinkProfile.KnifeEdgeLossDb(-5));
        Assert.Equal(0.0, LinkProfile.KnifeEdgeLossDb(double.NegativeInfinity));
    }

    [Fact]
    public void DeeperObstructionCostsMore()
    {
        double previous = 0;
        for (double v = 0; v <= 5; v += 0.5)
        {
            double loss = LinkProfile.KnifeEdgeLossDb(v);
            Assert.True(loss > previous, $"loss should climb with v, but v={v} gave {loss:F2}");
            previous = loss;
        }
    }

    [Fact]
    public void TheFresnelZoneIsWidestInTheMiddleAndPinchesAtTheAntennas()
    {
        var profile = LinkProfile.Build(Flat(10_000, 0), 30, 30, Freq915);

        Assert.Equal(0.0, profile.Points[0].FresnelRadiusM, 6);
        Assert.Equal(0.0, profile.Points[^1].FresnelRadiusM, 6);

        // sqrt(lambda * d/2 * d/2 / d) at 915 MHz over 10 km.
        double expected = Math.Sqrt(299.792458 / Freq915 * 10_000 / 4);
        Assert.Equal(expected, profile.Points[profile.Points.Count / 2].FresnelRadiusM, 1);
    }

    [Fact]
    public void TheSightLineSagsWithTheEarthsCurvature()
    {
        var profile = LinkProfile.Build(Flat(30_000, 0), 10, 10, Freq915);
        var middle = profile.Points[profile.Points.Count / 2];

        // d1*d2 / (2 * 4/3 * R) at the midpoint of 30 km: about 13 m.
        double bulge = 15_000.0 * 15_000.0 / (2 * Geodesy.EarthRadiusM * 4.0 / 3.0);
        Assert.Equal(10 - bulge, middle.SightLineM, 1);
    }

    [Fact]
    public void ALongFlatPathIsBlockedByTheCurvatureAlone()
    {
        // Two ground-level radios 30 km apart over flat terrain cannot see each
        // other: the earth itself is the obstruction. Getting this wrong is how
        // a predictor claims a link that is over the horizon.
        var profile = LinkProfile.Build(Flat(30_000, 0), 2, 2, Freq915);

        Assert.False(profile.HasLineOfSight);
        Assert.True(profile.DiffractionLossDb > 6,
            $"a path under the horizon should cost more than a grazing edge, got {profile.DiffractionLossDb:F1}");
    }

    [Fact]
    public void RaisingBothAntennasClearsThatSamePath()
    {
        var profile = LinkProfile.Build(Flat(30_000, 0), 40, 40, Freq915);

        Assert.True(profile.HasLineOfSight);
    }

    [Fact]
    public void RefractionMakesThePathClearerThanPureGeometryDoes()
    {
        var refracted = LinkProfile.Build(Flat(30_000, 0), 15, 15, Freq915);
        var geometric = LinkProfile.Build(Flat(30_000, 0), 15, 15, Freq915, earthRadiusFactor: 1.0);

        Assert.True(refracted.DiffractionLossDb < geometric.DiffractionLossDb);
    }

    [Fact]
    public void EndpointsAloneLeaveNothingToObstructThePath()
    {
        var profile = LinkProfile.Build([(0, 100), (1000, 100)], 2, 2, Freq915);

        Assert.True(profile.HasLineOfSight);
        Assert.Equal(0.0, profile.DiffractionLossDb);
    }

    [Fact]
    public void ADegeneratePathIsRefusedRatherThanDividedBy()
    {
        Assert.Throws<ArgumentException>(() => LinkProfile.Build([(0, 10)], 2, 2, Freq915));
        Assert.Throws<ArgumentException>(() => LinkProfile.Build([(0, 10), (0, 10)], 2, 2, Freq915));
        Assert.Throws<ArgumentOutOfRangeException>(() => LinkProfile.Build(Flat(1000, 0), 2, 2, 0));
    }
}
