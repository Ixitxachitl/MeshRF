// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class AirtimeTrackerTests
{
    private static readonly DateTime T0 = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ChannelUtilisationCountsBothDirections()
    {
        var t = new AirtimeTracker();
        // 600 ms heard + 600 ms sent inside the minute = 1.2 s of 60 s = 2%.
        t.Record(600, isTx: false, T0);
        t.Record(600, isTx: true, T0);

        t.Compute(out var channel, out _, T0);

        Assert.Equal(2.0f, channel, 3);
    }

    [Fact]
    public void AirUtilTxCountsOnlyOurOwnTransmissions()
    {
        var t = new AirtimeTracker();
        t.Record(36_000, isTx: false, T0);  // heard, not ours
        t.Record(36_000, isTx: true, T0);   // ours: 36 s of 3600 s = 1%

        t.Compute(out _, out var tx, T0);

        Assert.Equal(1.0f, tx, 3);
    }

    [Fact]
    public void ChannelUtilisationLooksAtOneMinute_TxAtOneHour()
    {
        var t = new AirtimeTracker();
        // Two minutes back: outside the channel window, inside the tx window.
        t.Record(6_000, isTx: true, T0.AddMinutes(-2));

        t.Compute(out var channel, out var tx, T0);

        Assert.Equal(0f, channel);
        Assert.True(tx > 0f, "a transmission two minutes old still counts toward the hour");
    }

    [Fact]
    public void SamplesOlderThanAnHourAreDropped()
    {
        var t = new AirtimeTracker();
        t.Record(1_000, isTx: true, T0.AddHours(-2));
        Assert.Equal(1, t.SampleCount);

        // Any later call trims: the old sample can never contribute again.
        t.Compute(out var channel, out var tx, T0);

        Assert.Equal(0f, channel);
        Assert.Equal(0f, tx);
        Assert.Equal(0, t.SampleCount);
    }

    [Fact]
    public void BothFiguresAreClampedToOneHundred()
    {
        var t = new AirtimeTracker();
        // Far more airtime than a minute holds — an impossible reading must
        // still be a legal percentage on the wire.
        for (int i = 0; i < 10; i++) t.Record(60_000, isTx: true, T0);

        t.Compute(out var channel, out var tx, T0);

        Assert.Equal(100f, channel);
        Assert.InRange(tx, 0f, 100f);
    }

    [Fact]
    public void ZeroAndNegativeDurationsAreIgnored()
    {
        var t = new AirtimeTracker();
        t.Record(0, isTx: true, T0);
        t.Record(-5, isTx: false, T0);

        Assert.Equal(0, t.SampleCount);
    }

    [Fact]
    public void AnIdleNodeReportsZero()
    {
        var t = new AirtimeTracker();
        t.Compute(out var channel, out var tx, T0);

        Assert.Equal(0f, channel);
        Assert.Equal(0f, tx);
    }

    [Theory]
    // LongFast: SF11 / 250 kHz / 4-5. Slow preset, so a small frame is
    // hundreds of milliseconds.
    [InlineData(11, 250_000.0, 5, 30, 300, 700)]
    // ShortFast: SF7 / 250 kHz / 4-5. An order of magnitude quicker.
    [InlineData(7, 250_000.0, 5, 30, 20, 80)]
    public void AirtimeEstimateLandsInTheExpectedRange(
        int sf, double bwHz, int cr, int payload, int lowMs, int highMs)
    {
        var ms = AirtimeTracker.EstimateAirtimeMs(sf, bwHz, cr, payload);
        Assert.InRange(ms, lowMs, highMs);
    }

    [Fact]
    public void AirtimeGrowsWithPayloadAndSpreadingFactor()
    {
        Assert.True(AirtimeTracker.EstimateAirtimeMs(9, 250_000, 5, 200) >
                    AirtimeTracker.EstimateAirtimeMs(9, 250_000, 5, 20));
        Assert.True(AirtimeTracker.EstimateAirtimeMs(12, 125_000, 5, 50) >
                    AirtimeTracker.EstimateAirtimeMs(7, 125_000, 5, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnEmptyPayloadHasNoAirtime(int payload)
    {
        Assert.Equal(0, AirtimeTracker.EstimateAirtimeMs(11, 250_000, 5, payload));
    }

    [Fact]
    public void AnUnusableBandwidthOrCodingRateIsZeroRatherThanInfinite()
    {
        Assert.Equal(0, AirtimeTracker.EstimateAirtimeMs(11, 0, 5, 30));
        Assert.Equal(0, AirtimeTracker.EstimateAirtimeMs(11, 250_000, 4, 30));
    }
}
