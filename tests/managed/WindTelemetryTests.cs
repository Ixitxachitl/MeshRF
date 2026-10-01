// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Mqtt;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Wind in environment telemetry: EnvironmentMetrics fields 13 (direction,
/// degrees), 14 (speed), 16 (gust) and 17 (lull), speeds in m/s.
/// </summary>
public class WindTelemetryTests
{
    private static ChannelConfig Channel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    [Fact]
    public void WindRoundTripsThroughTheWire()
    {
        var frame = MeshEncoder.EncodeTelemetryEnvironmentMetrics(Channel(), 0x11223344u, 7,
            temperatureC: 18.5f, barometricPressureHpa: 1009.8f,
            windDirectionDeg: 270, windSpeedMps: 4.2f, windGustMps: 7.9f, windLullMps: 1.5f);

        var t = MeshDecoder.Decode(frame, [Channel()])?.Telemetry;

        Assert.NotNull(t);
        Assert.True(t!.HasEnvironmentMetrics);
        Assert.Equal(18.5f, t.TemperatureC);
        Assert.Equal(1009.8f, t.BarometricPressureHpa);
        Assert.Equal(270u, t.WindDirectionDeg);
        Assert.Equal(4.2f, t.WindSpeedMps);
        Assert.Equal(7.9f, t.WindGustMps);
        Assert.Equal(1.5f, t.WindLullMps);
    }

    /// <summary>A report of wind alone is still an environment report.</summary>
    [Fact]
    public void WindAloneIsEnvironmentTelemetry()
    {
        var frame = MeshEncoder.EncodeTelemetryEnvironmentMetrics(Channel(), 0x11223344u, 8,
            windSpeedMps: 3f);

        var t = MeshDecoder.Decode(frame, [Channel()])?.Telemetry;

        Assert.True(t!.HasEnvironmentMetrics);
        Assert.Null(t.WindDirectionDeg);
    }

    [Theory]
    [InlineData(0u, "0° N")]
    [InlineData(11u, "11° N")]
    [InlineData(12u, "12° NNE")]
    [InlineData(270u, "270° W")]
    [InlineData(350u, "350° N")]
    [InlineData(360u, "0° N")]
    public void DirectionReadsAsACompassPoint(uint degrees, string expected) =>
        Assert.Equal(expected, DisplayUnits.FormatWindDirection(degrees));

    [Fact]
    public void SpeedFollowsTheUnitSetting()
    {
        Assert.Equal("4.0 m/s", DisplayUnits.FormatWindSpeed(4f, UnitSystem.Metric));
        // 4 m/s is 8.948 mph.
        Assert.Equal("8.9 mph", DisplayUnits.FormatWindSpeed(4f, UnitSystem.Imperial));
    }

    /// <summary>Named as firmware's MeshPacketSerializer names them.</summary>
    [Fact]
    public void MqttJsonCarriesWindUnderFirmwaresNames()
    {
        var result = new MeshDecodeResult
        {
            Port = PortNum.Telemetry,
            Telemetry = new MeshTelemetry
            {
                TemperatureC = 18.5f,
                WindDirectionDeg = 270,
                WindSpeedMps = 4.25f,
                WindGustMps = 7.5f,
                WindLullMps = 1.5f,
            },
        };
        var header = new MeshHeader { To = 0xFFFFFFFFu, From = 0x4fa54f59u, PacketId = 1, Flags = 3 };

        var json = MqttJsonSerializer.Serialize(result, header, "!4fa54f59",
            channelIndex: 0, rxTimeEpoch: 1000, rssi: null, snrDb: null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal(270u, payload.GetProperty("wind_direction").GetUInt32());
        Assert.Equal(4.25, payload.GetProperty("wind_speed").GetDouble(), 3);
        Assert.Equal(7.5, payload.GetProperty("wind_gust").GetDouble(), 3);
        Assert.Equal(1.5, payload.GetProperty("wind_lull").GetDouble(), 3);
    }
}
