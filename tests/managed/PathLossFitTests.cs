// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Fitting a log-distance path-loss model to what the radio has actually heard
/// from its direct neighbours.
/// </summary>
public class PathLossFitTests
{
    private const double Freq = 906.875;

    /// <summary>Neighbours generated from a known model, so the fit has a right
    /// answer to find.</summary>
    private static PathLossSample[] From(
        double exponent, double offsetDb, params double[] distances)
    {
        double reference = LinkBudget.FreeSpacePathLossAtOneMetreDb(Freq);
        var samples = new PathLossSample[distances.Length];
        for (int i = 0; i < distances.Length; i++)
            samples[i] = new PathLossSample(
                (uint)(i + 1), distances[i],
                reference + 10 * exponent * Math.Log10(distances[i]) + offsetDb);
        return samples;
    }

    [Fact]
    public void NothingToFitComesBackAsNothing()
    {
        Assert.Null(PathLossFit.Fit([], Freq));
    }

    [Fact]
    public void NeighboursBehavingLikeFreeSpaceFitFreeSpace()
    {
        var fit = PathLossFit.Fit(From(2.0, 0, 300, 900, 2500, 6000, 11000), Freq)!;

        Assert.True(fit.ExponentFitted);
        Assert.Equal(2.0, fit.Exponent, 6);
        Assert.Equal(0.0, fit.OffsetDb, 6);
        Assert.Equal(0.0, fit.RmsResidualDb, 6);
        Assert.True(fit.IsPlausible);
    }

    [Fact]
    public void ClutteredNeighboursFitASteeperExponent()
    {
        var fit = PathLossFit.Fit(From(3.4, 0, 250, 700, 1800, 4200, 9000), Freq)!;

        Assert.True(fit.ExponentFitted);
        Assert.Equal(3.4, fit.Exponent, 6);
        Assert.True(fit.IsPlausible);
    }

    [Fact]
    public void AConstantErrorLandsInTheOffsetAndLeavesTheExponentAlone()
    {
        // The case that matters most: the mesh never reports peer transmit
        // power, so every prediction can be wrong by a constant. The exponent
        // has to survive that, because it is the number being read.
        var honest = PathLossFit.Fit(From(3.1, 0, 200, 800, 3000, 7000, 15000), Freq)!;
        var biased = PathLossFit.Fit(From(3.1, 8, 200, 800, 3000, 7000, 15000), Freq)!;

        Assert.Equal(honest.Exponent, biased.Exponent, 6);
        Assert.Equal(8.0, biased.OffsetDb - honest.OffsetDb, 6);
    }

    [Fact]
    public void ScatterShowsUpAsResidualRatherThanBendingTheFit()
    {
        var samples = From(3.0, 0, 300, 1000, 3000, 9000, 20000);
        // Two neighbours heard 5 dB either side of the model.
        samples[1] = samples[1] with { PropagationLossDb = samples[1].PropagationLossDb + 5 };
        samples[3] = samples[3] with { PropagationLossDb = samples[3].PropagationLossDb - 5 };

        var fit = PathLossFit.Fit(samples, Freq)!;

        Assert.True(fit.RmsResidualDb > 2, $"scatter should show, got {fit.RmsResidualDb:F2} dB");
        Assert.InRange(fit.Exponent, 2.5, 3.5);
    }

    [Fact]
    public void TooFewNeighboursHoldTheExponentAtFreeSpaceAndMeasureTheOffset()
    {
        // Three neighbours all 12 dB down on free space. The honest reading is
        // "12 dB of something", not an exponent invented from three points.
        var fit = PathLossFit.Fit(From(2.0, 12, 500, 2000, 6000), Freq)!;

        Assert.False(fit.ExponentFitted);
        Assert.Equal(PathLossFit.FreeSpaceExponent, fit.Exponent);
        Assert.Equal(12.0, fit.OffsetDb, 6);
        Assert.Equal(3, fit.SampleCount);
    }

    [Fact]
    public void NeighboursAllAtTheSameRangeCannotGiveASlope()
    {
        // Five neighbours in a ring around the station: there is no lever arm
        // to measure a falloff with, however many of them there are.
        var fit = PathLossFit.Fit(From(2.0, 9, 1000, 1010, 1020, 1005, 995), Freq)!;

        Assert.False(fit.ExponentFitted);
        Assert.Equal(PathLossFit.FreeSpaceExponent, fit.Exponent);
        Assert.Equal(9.0, fit.OffsetDb, 1);
    }

    [Fact]
    public void ASpreadOfLessThanTwiceTheRangeIsNotEnough()
    {
        var narrow = PathLossFit.Fit(From(3.0, 0, 1000, 1200, 1500, 1900), Freq)!;
        var wide = PathLossFit.Fit(From(3.0, 0, 1000, 2000, 4000, 8000), Freq)!;

        Assert.False(narrow.ExponentFitted);
        Assert.True(wide.ExponentFitted);
    }

    [Fact]
    public void ANonsenseFitIsReportedRatherThanClampedIntoLookingReasonable()
    {
        var fit = PathLossFit.Fit(From(9.0, 0, 400, 1200, 4000, 12000), Freq)!;

        Assert.Equal(9.0, fit.Exponent, 6);
        Assert.False(fit.IsPlausible);
    }

    [Fact]
    public void ThePredictedLossIsTheModelItFitted()
    {
        var samples = From(3.2, 4, 300, 1100, 4000, 12000);
        var fit = PathLossFit.Fit(samples, Freq)!;

        foreach (var sample in samples)
            Assert.Equal(sample.PropagationLossDb, fit.PathLossDb(sample.DistanceM, Freq), 6);
    }

    [Fact]
    public void FreeSpaceSamplesLeaveNothingInExcessOfFreeSpace()
    {
        var fit = PathLossFit.Fit(From(2.0, 0, 400, 1600, 5000, 14000), Freq)!;

        Assert.Equal(0.0, fit.ExcessOverFreeSpaceDb(1000), 6);
        Assert.Equal(0.0, fit.ExcessOverFreeSpaceDb(10_000), 6);
    }

    [Fact]
    public void ASteeperExponentCostsMoreTheFurtherOutYouGo()
    {
        var fit = PathLossFit.Fit(From(3.0, 0, 400, 1600, 5000, 14000), Freq)!;

        double near = fit.ExcessOverFreeSpaceDb(1000);
        double far = fit.ExcessOverFreeSpaceDb(10_000);
        Assert.Equal(10.0, far - near, 6);
    }

    [Fact]
    public void TheResidualSaysHowWrongTheModelIsAboutOneNeighbour()
    {
        var samples = From(3.0, 0, 500, 2000, 6000, 18000);
        var fit = PathLossFit.Fit(samples, Freq)!;

        // Heard 7 dB better than the model expects.
        var lucky = samples[1] with { PropagationLossDb = samples[1].PropagationLossDb - 7 };
        Assert.Equal(7.0, fit.ResidualDb(lucky, Freq), 6);
    }

    [Fact]
    public void ANeighbourAtZeroRangeIsRefusedRatherThanLoggedAsNegativeInfinity()
    {
        Assert.Throws<ArgumentException>(() =>
            PathLossFit.Fit([new PathLossSample(1, 0, 100)], Freq));
    }
}
