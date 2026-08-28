// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class RoleDefaultsTests
{
    private const int OneDay = 24 * 60 * 60;

    [Fact]
    public void ClientCoercesNothing()
    {
        var d = RoleDefaults.For("Client");
        Assert.Null(d.NodeInfoEnabled);
        Assert.Null(d.PositionSeconds);
        Assert.Null(d.DeviceMetricsSeconds);
        Assert.Null(d.RebroadcastMode);
        Assert.Null(d.IsUnmessagable);
    }

    [Fact]
    public void UnknownRoleCoercesNothing() =>
        Assert.Null(RoleDefaults.For("NotARole").RebroadcastMode);

    // Firmware's default_telemetry_broadcast_interval_secs is IF_ROUTER, so the
    // same expression yields half a day for a router and an hour for anyone else.
    [Fact]
    public void RouterTelemetryIsHalfADayAndSensorIsAnHour()
    {
        Assert.Equal(OneDay / 2, RoleDefaults.For("Router").DeviceMetricsSeconds);
        Assert.Equal(3600, RoleDefaults.For("Sensor").DeviceMetricsSeconds);
        Assert.Equal(3600, RoleDefaults.For("Tracker").DeviceMetricsSeconds);
        Assert.Equal(OneDay, RoleDefaults.For("RouterLate").DeviceMetricsSeconds);
    }

    [Fact]
    public void RouterRestrictsRebroadcastToCorePorts() =>
        Assert.Equal("CorePortnumsOnly", RoleDefaults.For("Router").RebroadcastMode);

    [Theory]
    [InlineData("Router")]
    [InlineData("RouterLate")]
    [InlineData("Sensor")]
    [InlineData("Tracker")]
    [InlineData("TakTracker")]
    public void InfrastructureRolesAdvertiseUnmessagable(string role) =>
        Assert.True(RoleDefaults.For(role).IsUnmessagable);

    [Fact]
    public void SensorTurnsOnEnvironmentTelemetry()
    {
        var d = RoleDefaults.For("Sensor");
        Assert.True(d.EnvironmentMetricsEnabled);
        Assert.Equal(300, d.EnvironmentMetricsSeconds);
    }

    [Fact]
    public void LostAndFoundBroadcastsPositionEveryFiveMinutes()
    {
        var d = RoleDefaults.For("LostAndFound");
        Assert.True(d.PositionEnabled);
        Assert.Equal(300, d.PositionSeconds);
    }

    [Fact]
    public void TakTrackerFavoursPositionOverEverythingElse()
    {
        var d = RoleDefaults.For("TakTracker");
        Assert.Equal(180, d.PositionSeconds);
        Assert.Equal(OneDay, d.NodeInfoSeconds);
        Assert.Equal(OneDay, d.DeviceMetricsSeconds);
    }

    [Fact]
    public void ClientHiddenOriginatesNothing()
    {
        var d = RoleDefaults.For("ClientHidden");
        Assert.False(d.NodeInfoEnabled);
        Assert.False(d.PositionEnabled);
        Assert.False(d.PositionSmartEnabled);
        Assert.False(d.DeviceMetricsEnabled);
        Assert.False(d.EnvironmentMetricsEnabled);
        Assert.False(d.AirQualityMetricsEnabled);
        Assert.False(d.NodeStatusEnabled);
    }

    [Fact]
    public void ClientHiddenAlsoKeepsRebroadcastLocal() =>
        Assert.Equal("LocalOnly", RoleDefaults.For("ClientHidden").RebroadcastMode);

    // ---- Deprecated roles ----

    // AdminModule rewrites both to CLIENT the moment a device config carrying
    // one is applied, so no live node holds either and we must not claim one.
    [Theory]
    [InlineData("RouterClient")]
    [InlineData("Repeater")]
    [InlineData("ROUTER_CLIENT")]
    public void DeprecatedRolesResolveToClient(string role)
    {
        Assert.True(RoleDefaults.IsDeprecated(role));
        Assert.Equal("Client", RoleDefaults.Effective(role));
        // And carry no coercions of their own.
        Assert.Null(RoleDefaults.For(role).RebroadcastMode);
        Assert.Null(RoleDefaults.For(role).NodeInfoEnabled);
    }

    [Theory]
    [InlineData("Client")]
    [InlineData("Router")]
    [InlineData("ClientBase")]
    public void LiveRolesAreNotDeprecated(string role)
    {
        Assert.False(RoleDefaults.IsDeprecated(role));
        Assert.Equal(role, RoleDefaults.Effective(role));
    }

    // ---- Smart position ----

    // The point of LOST_AND_FOUND is an unconditional beacon: a stationary lost
    // node still has to be findable, which smart broadcast would prevent.
    [Theory]
    [InlineData("LostAndFound")]
    [InlineData("TAK")]
    [InlineData("ClientHidden")]
    public void RolesThatMustNotFilterPositions(string role) =>
        Assert.False(RoleDefaults.For(role).PositionSmartEnabled);

    [Fact]
    public void TakTrackerBroadcastsSmartlyAndOften()
    {
        var d = RoleDefaults.For("TakTracker");
        Assert.True(d.PositionSmartEnabled);
        Assert.Equal(20u, d.PositionSmartMinMoveMeters);
        Assert.Equal(15, d.PositionSmartMinSeconds);
    }

    // CoTs carry height above the ellipsoid, so the TAK roles clear ALTITUDE_MSL.
    [Theory]
    [InlineData("TAK")]
    [InlineData("TakTracker")]
    public void TakRolesSendHaeAltitude(string role) =>
        Assert.False(RoleDefaults.For(role).PositionAltitudeMsl);

    [Theory]
    [InlineData("Client")]
    [InlineData("Router")]
    [InlineData("Tracker")]
    public void EveryOtherRoleLeavesAltitudeAlone(string role) =>
        Assert.Null(RoleDefaults.For(role).PositionAltitudeMsl);

    // ---- NodeInfo reply solicitation ----

    // A tracker or sensor beacons often enough that asking for a reply each
    // time would set off a round of NodeInfo from everything in earshot.
    [Theory]
    [InlineData("Tracker")]
    [InlineData("Sensor")]
    public void TrackersAndSensorsNeverAskForReplies(string role) =>
        Assert.False(RoleDefaults.AllowsRequestingReplies(role));

    [Theory]
    [InlineData("Client")]
    [InlineData("Router")]
    [InlineData("TakTracker")]
    public void OtherRolesMayAskForReplies(string role) =>
        Assert.True(RoleDefaults.AllowsRequestingReplies(role));
}
