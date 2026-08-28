// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The overlay's promise: a role never consumes the user's own settings, so
/// wearing one for an afternoon and taking it off leaves nothing to retype.
/// </summary>
public class SettingOverlayTests
{
    private const string NoFloorReason = "min";
    private const int NoFloor = 0;

    // ---- The promise ----

    // The whole reason this is an overlay rather than firmware's rewrite: what
    // the user set is still there underneath, whatever the role says.
    [Fact]
    public void RoleDoesNotConsumeTheUserSetting()
    {
        const int userInterval = 900;   // what they tuned

        var asRouter = SettingOverlay.Interval(
            userInterval, RoleDefaults.For("Router").DeviceMetricsSeconds, "Router", NoFloor, NoFloorReason);
        Assert.Equal(12 * 60 * 60, asRouter.Value);
        Assert.True(asRouter.IsOverridden);

        // Same user value, role taken off: their number is back untouched.
        var asClient = SettingOverlay.Interval(
            userInterval, RoleDefaults.For("Client").DeviceMetricsSeconds, "Client", NoFloor, NoFloorReason);
        Assert.Equal(userInterval, asClient.Value);
        Assert.False(asClient.IsOverridden);
    }

    [Fact]
    public void FlagRoundTripsThroughARoleToo()
    {
        var asSensor = SettingOverlay.Flag(false, RoleDefaults.For("Sensor").IsUnmessagable, "Sensor");
        Assert.True(asSensor.Value);
        Assert.True(asSensor.IsOverridden);

        var asClient = SettingOverlay.Flag(false, RoleDefaults.For("Client").IsUnmessagable, "Client");
        Assert.False(asClient.Value);
        Assert.False(asClient.IsOverridden);
    }

    // ---- Nothing to say when nothing changed ----

    [Fact]
    public void NoRoleValueAndNoFloorLeavesTheUserAlone()
    {
        var r = SettingOverlay.Interval(900, null, "Client", NoFloor, NoFloorReason);
        Assert.Equal(900, r.Value);
        Assert.Null(r.Reason);
    }

    // A role that happens to ask for what the user already set has overruled
    // nothing, so there is no note to show.
    [Fact]
    public void RoleAgreeingWithTheUserIsNotAnOverride()
    {
        var r = SettingOverlay.Interval(3600, 3600, "Sensor", NoFloor, NoFloorReason);
        Assert.False(r.IsOverridden);
    }

    [Fact]
    public void FloorBelowTheUserValueIsNotAnOverride()
    {
        var r = SettingOverlay.Interval(7200, null, "Client", floorSeconds: 3600, "min 1 h");
        Assert.Equal(7200, r.Value);
        Assert.False(r.IsOverridden);
    }

    // ---- Which rule gets the blame ----

    [Fact]
    public void RoleIsNamedWhenTheRoleIsWhatMoved()
    {
        var r = SettingOverlay.Interval(300, 86400, "TAK", NoFloor, NoFloorReason);
        Assert.Equal(86400, r.Value);
        Assert.Equal("role TAK", r.Reason);
    }

    [Fact]
    public void FloorIsNamedWhenTheFloorIsWhatMoved()
    {
        var r = SettingOverlay.Interval(300, null, "Client", floorSeconds: 3600, "min 1 h");
        Assert.Equal(3600, r.Value);
        Assert.Equal("min 1 h", r.Reason);
    }

    // Both apply and the floor wins: the note has to name the floor, because
    // that is the rule standing between the user and their number.
    [Fact]
    public void FloorOutranksTheRoleWhenItIsHigher()
    {
        var r = SettingOverlay.Interval(15, roleSeconds: 180, "TakTracker",
                                        floorSeconds: 3600, "default channel");
        Assert.Equal(3600, r.Value);
        Assert.Equal("default channel", r.Reason);
    }

    [Fact]
    public void RoleWinsWhenItIsAboveTheFloor()
    {
        var r = SettingOverlay.Interval(60, roleSeconds: 86400, "TAK",
                                        floorSeconds: 3600, "default channel");
        Assert.Equal(86400, r.Value);
        Assert.Equal("role TAK", r.Reason);
    }

    // ---- The real cases these were built for ----

    // Firmware's unconditional hourly NodeInfo minimum.
    [Fact]
    public void NodeInfoCannotBeatOncePerHour()
    {
        var r = SettingOverlay.Interval(60, null, "Client",
                                        BroadcastIntervals.MinNodeInfoSeconds, "min 1 h");
        Assert.Equal(3600, r.Value);
    }

    // A TAK_TRACKER asks for a 15-second smart gap; the shared channel says no.
    [Fact]
    public void TakTrackerSmartGapIsHeldBackOnADefaultChannel()
    {
        var role = RoleDefaults.For("TakTracker").PositionSmartMinSeconds;
        var r = SettingOverlay.Interval(300, role, "TakTracker",
                                        DefaultChannelMinimums.SmartPositionSeconds, "default channel");
        Assert.Equal(300, r.Value);
        // The user's own 300 is what stands, so nothing was overruled.
        Assert.False(r.IsOverridden);
    }

    [Fact]
    public void DistanceFollowsTheSameRule()
    {
        var coerced = SettingOverlay.Distance(100, RoleDefaults.For("TakTracker").PositionSmartMinMoveMeters, "TakTracker");
        Assert.Equal(20u, coerced.Value);
        Assert.True(coerced.IsOverridden);

        var free = SettingOverlay.Distance(100, RoleDefaults.For("Client").PositionSmartMinMoveMeters, "Client");
        Assert.Equal(100u, free.Value);
        Assert.False(free.IsOverridden);
    }

    // ---- Formatting ----

    [Theory]
    [InlineData(86400, "1 d")]
    [InlineData(43200, "12 h")]
    [InlineData(3600, "1 h")]
    [InlineData(1800, "30 min")]
    [InlineData(300, "5 min")]
    [InlineData(180, "3 min")]
    [InlineData(15, "15 s")]
    [InlineData(0, "0 s")]
    public void DurationReadsInWholeUnits(int seconds, string expected) =>
        Assert.Equal(expected, SettingOverlay.Duration(seconds));

    [Fact]
    public void UnnamedRoleStillProducesAReason() =>
        Assert.Equal("role", SettingOverlay.Flag(false, true, "  ").Reason);
}
