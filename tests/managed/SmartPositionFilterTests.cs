// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Location;
using Xunit;

namespace MeshRF.Tests;

public class SmartPositionFilterTests
{
    private static readonly DateTime T0 = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(30);
    private const double MinMove = 10.0;
    private const double Lat = 39.19053;
    private const double Lon = -120.76974;

    /// <summary>Metres north of the reference point, near enough at this
    /// latitude for a threshold measured in tens of metres.</summary>
    private static double North(double metres) => Lat + metres / 111_320.0;

    [Fact]
    public void TakesTheFirstFixWhateverTheThresholds()
    {
        var filter = new SmartPositionFilter();
        Assert.True(filter.ShouldTake(Lat, Lon, T0, MinMove, Gap, out var moved));
        Assert.Equal(0.0, moved);
    }

    [Fact]
    public void HoldsAStationaryReceiverIndefinitely()
    {
        var filter = new SmartPositionFilter();
        filter.ShouldTake(Lat, Lon, T0, MinMove, Gap, out _);

        // A metre of wander, an hour later: still the same place.
        Assert.False(filter.ShouldTake(North(1), Lon, T0.AddHours(1), MinMove, Gap, out var moved));
        Assert.InRange(moved, 0.5, 1.5);
    }

    [Fact]
    public void HoldsRealMovementUntilTheIntervalIsUp()
    {
        var filter = new SmartPositionFilter();
        filter.ShouldTake(Lat, Lon, T0, MinMove, Gap, out _);

        Assert.False(filter.ShouldTake(North(500), Lon, T0.AddSeconds(29), MinMove, Gap, out _));
        Assert.True(filter.ShouldTake(North(500), Lon, T0.AddSeconds(30), MinMove, Gap, out _));
    }

    [Fact]
    public void AccumulatesDistanceAgainstTheLastFixTaken()
    {
        var filter = new SmartPositionFilter();
        filter.ShouldTake(Lat, Lon, T0, MinMove, Gap, out _);

        // Four metres per interval never clears 10 m in one step, but the
        // reference does not move until a fix is taken, so it adds up.
        Assert.False(filter.ShouldTake(North(4), Lon, T0.AddSeconds(30), MinMove, Gap, out _));
        Assert.False(filter.ShouldTake(North(8), Lon, T0.AddSeconds(60), MinMove, Gap, out _));
        Assert.True(filter.ShouldTake(North(12), Lon, T0.AddSeconds(90), MinMove, Gap, out var moved));
        Assert.InRange(moved, 11.0, 13.0);
    }

    [Fact]
    public void MeasuresTheIntervalFromTheLastFixTakenNotTheLastSeen()
    {
        var filter = new SmartPositionFilter();
        filter.ShouldTake(Lat, Lon, T0, MinMove, Gap, out _);

        // Held fixes in between must not restart the clock.
        filter.ShouldTake(North(1), Lon, T0.AddSeconds(10), MinMove, Gap, out _);
        filter.ShouldTake(North(2), Lon, T0.AddSeconds(20), MinMove, Gap, out _);
        Assert.True(filter.ShouldTake(North(50), Lon, T0.AddSeconds(30), MinMove, Gap, out _));
    }

    [Fact]
    public void ResetTakesTheNextFixAsIs()
    {
        var filter = new SmartPositionFilter();
        filter.ShouldTake(Lat, Lon, T0, MinMove, Gap, out _);
        Assert.False(filter.ShouldTake(North(1), Lon, T0.AddSeconds(1), MinMove, Gap, out _));

        filter.Reset();
        Assert.True(filter.ShouldTake(North(1), Lon, T0.AddSeconds(2), MinMove, Gap, out var moved));
        Assert.Equal(0.0, moved);
    }

    [Fact]
    public void ZeroThresholdsTakeEveryFix()
    {
        var filter = new SmartPositionFilter();
        filter.ShouldTake(Lat, Lon, T0, 0, TimeSpan.Zero, out _);
        Assert.True(filter.ShouldTake(Lat, Lon, T0, 0, TimeSpan.Zero, out _));
    }
}
