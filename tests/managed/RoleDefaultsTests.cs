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

    [Theory]
    [InlineData("ClientHidden")]
    [InlineData("Repeater")]
    public void SilentRolesOriginateNothing(string role)
    {
        var d = RoleDefaults.For(role);
        Assert.False(d.NodeInfoEnabled);
        Assert.False(d.PositionEnabled);
        Assert.False(d.DeviceMetricsEnabled);
        Assert.False(d.EnvironmentMetricsEnabled);
        Assert.False(d.AirQualityMetricsEnabled);
        Assert.False(d.NodeStatusEnabled);
    }

    [Fact]
    public void ClientHiddenAlsoKeepsRebroadcastLocal()
    {
        Assert.Equal("LocalOnly", RoleDefaults.For("ClientHidden").RebroadcastMode);
        // A repeater exists to rebroadcast, so it stays silent without being quiet.
        Assert.Null(RoleDefaults.For("Repeater").RebroadcastMode);
    }
}
