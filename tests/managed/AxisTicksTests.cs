// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The round numbers every chart labels its axes with.
/// </summary>
public class AxisTicksTests
{
    [Fact]
    public void TicksAreRoundNumbersSpanningTheRange()
    {
        Assert.Equal([0, 20, 40, 60, 80, 100], AxisTicks.Between(0, 100, 5));
    }

    [Fact]
    public void TheStepIsOneTwoOrFiveTimesAPowerOfTen()
    {
        foreach ((double lo, double hi) in new[] { (0.0, 3.0), (0.0, 7.0), (0.0, 40.0), (0.0, 900.0) })
        {
            var ticks = AxisTicks.Between(lo, hi, 5).ToList();
            Assert.True(ticks.Count >= 2, $"{lo}..{hi} produced {ticks.Count} ticks");

            double step = ticks[1] - ticks[0];
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(step)));
            double normalised = step / magnitude;
            Assert.Contains(Math.Round(normalised, 6), new[] { 1.0, 2.0, 5.0 });
        }
    }

    [Fact]
    public void EveryTickLandsInsideTheRange()
    {
        foreach (double tick in AxisTicks.Between(-37, 91, 6))
            Assert.InRange(tick, -37, 91);
    }

    [Fact]
    public void ARangeCrossingZeroPutsATickOnZero()
    {
        Assert.Contains(0.0, AxisTicks.Between(-40, 60, 5));
    }

    [Fact]
    public void ZeroIsNeverNegativeZero()
    {
        // The bug this rule exists for: a tick landing on negative zero prints
        // as "-0", which reads as a broken axis rather than as the origin.
        foreach ((double lo, double hi) in new[]
                 { (-40.0, 60.0), (-1.0, 3.0), (-250.0, 250.0), (-0.5, 0.5) })
        {
            foreach (double tick in AxisTicks.Between(lo, hi, 5))
            {
                if (tick != 0) continue;
                Assert.False(double.IsNegative(tick),
                    $"{lo}..{hi} produced a negative zero, which prints as \"-0\"");
                Assert.Equal("0", tick.ToString("0"));
            }
        }
    }

    [Fact]
    public void ARangeWithNoSpanHasNothingToLabel()
    {
        Assert.Empty(AxisTicks.Between(5, 5, 5));
        Assert.Empty(AxisTicks.Between(9, 1, 5));
    }

    [Fact]
    public void AskingForNoTicksGivesNone()
    {
        Assert.Empty(AxisTicks.Between(0, 100, 0));
    }

    [Fact]
    public void ARangeThatIsNotANumberIsRefusedRatherThanLoopingForever()
    {
        // A chart handed an empty series can compute these, and a loop stepping
        // by NaN never terminates.
        Assert.Empty(AxisTicks.Between(double.NaN, 10, 5));
        Assert.Empty(AxisTicks.Between(0, double.PositiveInfinity, 5));
    }

    [Fact]
    public void MoreTicksAreAskedForMoreTicksAppear()
    {
        Assert.True(AxisTicks.Between(0, 100, 10).Count() > AxisTicks.Between(0, 100, 3).Count());
    }

    [Fact]
    public void ATinyRangeStillGetsLabels()
    {
        // Elevation angles over flat ground span hundredths of a degree.
        var ticks = AxisTicks.Between(-0.09, 0.01, 5).ToList();
        Assert.True(ticks.Count >= 2);
        Assert.All(ticks, t => Assert.InRange(t, -0.09, 0.01));
    }
}
