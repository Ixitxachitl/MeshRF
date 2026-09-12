// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// A packet the MQTT bridge hands in was heard on no radio, but it is not
/// heard on nothing: it arrives sealed with one mesh's channel, and that is
/// the mesh it came off. The node column was left blank for it, so a list fed
/// by a broker said nothing about where any of it lived.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class DownlinkHeardOnTests(HeadlessAvalonia avalonia)
{
    private const uint Us = 0x11111111u;
    private const uint Peer = 0x4A2B3C4Du;
    private const string Primary = nameof(LoraPreset.MediumFast);
    private const long When = 1_700_000_000;

    private static AvaloniaMeshRxHost Station(NodeStore nodes) =>
        new(nodes, new ChannelStore(), new WaypointStore(), new MessageStore(), Us, [],
            null, primaryList: Primary);

    /// <summary>The bridge hands a frame in as the primary's, carrying the mesh
    /// of the channel that sealed it. Its frequency is the primary's, which is
    /// exactly what must not be recorded: nothing was tuned to it.</summary>
    private static RxSource Downlinked(string mesh) =>
        RxSource.Primary(LoraPreset.MediumFast, isCustom: false, 913.125, mesh) with { FromDownlink = true };

    private static RxSource OnTheAir(LoraPreset preset, double freqMHz) =>
        RxSource.ForPreset(1, preset, freqMHz);

    [Fact]
    public void ADownlinkRecordsTheMeshOfTheChannelItCameOff() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        host.RecordSighting(Peer, When, SignalReading.None, hopsAway: 0, viaMqtt: true, Downlinked(Primary));

        var node = nodes.Get(Peer)!;
        Assert.Equal(Primary, node.HeardOnPreset);
        // No radio heard it, so there is no channel centre to claim: the
        // primary's would say this node had been heard on the air there.
        Assert.Null(node.HeardOnFreqMHz);
        Assert.True(node.IsSeenViaMqtt);
        Assert.Equal($"Heard on {Primary} through MQTT, so nothing says what radio it is tuned to",
                     node.HeardOnTip);
    }));

    /// <summary>A NodeInfo is how most of a broker-fed list arrives, and it
    /// files the node away by its own route.</summary>
    [Fact]
    public void ADownlinkedNodeInfoRecordsItToo() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = Primary,
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };
        var user = new ProtoWriter();
        user.WriteStringField(1, $"!{Peer:x8}");
        user.WriteStringField(2, "Gateway Node");
        user.WriteStringField(3, "GW");
        var frame = MeshEncoder.Encode(channel, Peer, 0xFFFFFFFFu, 1, PortNum.NodeInfo, user.ToArray());
        var result = MeshDecoder.Decode(frame, [channel]);
        Assert.True(MeshHeader.TryParse(frame, out var header));

        host.OnMessageDecoded(frame, header, new MessageRecord { FromNode = Peer }, result!,
                              rxEpoch: When, SignalReading.None, hopsAway: 0, Downlinked(Primary));

        var node = nodes.Get(Peer)!;
        Assert.Equal("Gateway Node", node.LongName);
        Assert.Equal(Primary, node.HeardOnPreset);
        Assert.Null(node.HeardOnFreqMHz);
    }));

    /// <summary>
    /// And a downlink never writes over what a radio answered. The mesh decides
    /// which settings a reply goes out on, so a node heard on a listener and
    /// then downlinked on the primary's channel must not become the primary's.
    /// </summary>
    [Fact]
    public void ADownlinkDoesNotOverwriteWhatARadioHeard() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);

        host.RecordSighting(Peer, When, new SignalReading(-8.5f, -95f, true), hopsAway: 0,
                            viaMqtt: false, OnTheAir(LoraPreset.LongFast, 906.875));
        Assert.Equal(nameof(LoraPreset.LongFast), nodes.Get(Peer)!.HeardOnPreset);

        host.RecordSighting(Peer, When + 60, SignalReading.None, hopsAway: 0, viaMqtt: true,
                            Downlinked(Primary));

        var node = nodes.Get(Peer)!;
        Assert.Equal(nameof(LoraPreset.LongFast), node.HeardOnPreset);
        // The radio's own reading of where it was stands too.
        Assert.Equal(906.875, node.HeardOnFreqMHz!.Value, 6);
    }));

    /// <summary>A node nothing has ever heard says so, rather than leaving the
    /// cell blank with no reason given.</summary>
    [Fact]
    public void ANodeHeardNoWayAtAllSaysSo() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.Upsert(new NodeRecord { NodeNum = Peer, LongName = "typed in" });

        Assert.Equal("Not heard at all since this was recorded", nodes.Get(Peer)!.HeardOnTip);
    }));
}
