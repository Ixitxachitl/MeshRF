// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// What a received telemetry packet leaves on the node it came from. Every
/// value it carried lands on the node's row, which is what the peer panel,
/// the map labels and the node filters read; the history table alone is not
/// enough.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class EnvironmentTelemetryNodeTests(HeadlessAvalonia avalonia)
{
    private const uint Us = 0x0A0B0C0Du;
    private const uint Peer = 0x55667788u;

    private static ChannelConfig Channel() => new()
    {
        Index = 0,
        Name = nameof(LoraPreset.MediumFast),
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    private static void Receive(AvaloniaMeshRxHost host, byte[] frame)
    {
        var result = MeshDecoder.Decode(frame, [Channel()]);
        Assert.NotNull(result?.Telemetry);
        Assert.True(MeshHeader.TryParse(frame, out var header));
        host.OnMessageDecoded(frame, header, new MessageRecord { FromNode = Peer }, result!,
                              rxEpoch: 1_700_000_000, SignalReading.None, hopsAway: 0,
                              RxSource.Primary(LoraPreset.MediumFast, isCustom: false, freqMHz: 913.125));
    }

    /// <summary>The regression: only temperature used to reach the row, so a
    /// station's humidity and pressure, and now its wind, were recorded in
    /// history and shown nowhere else.</summary>
    [Fact]
    public void EveryEnvironmentValueLandsOnTheNode() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = new AvaloniaMeshRxHost(nodes, new ChannelStore(), new WaypointStore(),
                                          new MessageStore(), Us, [], null,
                                          primaryList: nameof(LoraPreset.MediumFast));

        Receive(host, MeshEncoder.EncodeTelemetryEnvironmentMetrics(Channel(), Peer, 41,
            temperatureC: 12.5f, relativeHumidityPct: 81f, barometricPressureHpa: 1002.4f,
            windDirectionDeg: 225, windSpeedMps: 6.1f, windGustMps: 9.4f, windLullMps: 2.2f));

        var node = nodes.Get(Peer);
        Assert.NotNull(node);
        Assert.Equal(12.5f, node!.TemperatureC);
        Assert.Equal(81f, node.RelativeHumidityPct);
        Assert.Equal(1002.4f, node.BarometricPressureHpa);
        Assert.Equal(225u, node.WindDirectionDeg);
        Assert.Equal(6.1f, node.WindSpeedMps);
        Assert.Equal(9.4f, node.WindGustMps);
        Assert.Equal(2.2f, node.WindLullMps);

        var history = Assert.Single(nodes.TelemetryHistory(Peer));
        Assert.Equal(6.1, history.WindSpeedMps!.Value, 3);
        Assert.Equal(225, history.WindDirectionDeg!.Value, 3);
    }));

    /// <summary>A report that leaves wind out keeps the wind on file rather
    /// than clearing it, as with every other value.</summary>
    [Fact]
    public void AReportWithoutWindKeepsTheLastWind() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = new AvaloniaMeshRxHost(nodes, new ChannelStore(), new WaypointStore(),
                                          new MessageStore(), Us, [], null,
                                          primaryList: nameof(LoraPreset.MediumFast));

        Receive(host, MeshEncoder.EncodeTelemetryEnvironmentMetrics(Channel(), Peer, 42,
            temperatureC: 12.5f, windSpeedMps: 6.1f));
        Receive(host, MeshEncoder.EncodeTelemetryEnvironmentMetrics(Channel(), Peer, 43,
            temperatureC: 13f));

        var node = nodes.Get(Peer)!;
        Assert.Equal(13f, node.TemperatureC);
        Assert.Equal(6.1f, node.WindSpeedMps);
    }));
}
