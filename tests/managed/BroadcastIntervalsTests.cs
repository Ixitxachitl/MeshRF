// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class BroadcastIntervalsTests
{
    // Below firmware's threshold the mesh isn't busy enough to throttle.
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    public void QuietMeshDoesNotScale(int online) =>
        Assert.Equal(1.0, BroadcastIntervals.CongestionScalingCoefficient(online, LoraPreset.LongFast));

    [Fact]
    public void PastFortyNodesTheIntervalStretches()
    {
        double coef = BroadcastIntervals.CongestionScalingCoefficient(80, LoraPreset.LongFast);
        Assert.True(coef > 1.0);
        // Firmware's worked example: SF11/250 kHz gives 0.08192 per node over 40.
        Assert.Equal(1.0 + 40 * (2048.0 / (250.0 * 100.0)), coef, 6);
    }

    // A slow preset holds the channel longer per packet, so it backs off harder
    // for the same node count.
    [Fact]
    public void SlowerPresetsThrottleHarder()
    {
        double slow = BroadcastIntervals.CongestionScalingCoefficient(100, LoraPreset.LongSlow);
        double fast = BroadcastIntervals.CongestionScalingCoefficient(100, LoraPreset.ShortFast);
        Assert.True(slow > fast);
    }

    // Routers already run long intervals, and the tracker family exists to be
    // timely — firmware exempts both from scaling.
    [Theory]
    [InlineData("Router")]
    [InlineData("RouterLate")]
    [InlineData("Sensor")]
    [InlineData("Tracker")]
    [InlineData("TakTracker")]
    [InlineData("ROUTER_LATE")]
    public void ExemptRolesKeepTheirConfiguredInterval(string role)
    {
        Assert.True(BroadcastIntervals.IsExempt(role));
        Assert.Equal(3600, BroadcastIntervals.ScaledSeconds(3600, role, onlineNodes: 500, LoraPreset.LongFast));
    }

    [Theory]
    [InlineData("Client")]
    [InlineData("ClientBase")]
    [InlineData("TAK")]
    [InlineData("LostAndFound")]
    public void EveryoneElseScales(string role)
    {
        Assert.False(BroadcastIntervals.IsExempt(role));
        Assert.True(BroadcastIntervals.ScaledSeconds(3600, role, onlineNodes: 200, LoraPreset.LongFast) > 3600);
    }

    [Fact]
    public void QuietMeshLeavesEvenAScalingRoleAlone() =>
        Assert.Equal(3600, BroadcastIntervals.ScaledSeconds(3600, "Client", onlineNodes: 12, LoraPreset.LongFast));

    // A very large mesh on a very slow preset must not overflow into a negative
    // or zero interval, which would turn a beacon into a transmit loop.
    [Fact]
    public void ExtremeCongestionSaturatesRatherThanOverflowing()
    {
        int scaled = BroadcastIntervals.ScaledSeconds(
            int.MaxValue / 2, "Client", onlineNodes: 4000, LoraPreset.LongSlow);
        Assert.InRange(scaled, 1, int.MaxValue / 1000);
    }

    [Fact]
    public void ZeroOrNegativeIntervalPassesThroughUntouched() =>
        Assert.Equal(0, BroadcastIntervals.ScaledSeconds(0, "Client", onlineNodes: 500, LoraPreset.LongFast));
}
