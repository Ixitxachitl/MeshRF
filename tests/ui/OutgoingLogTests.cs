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
/// What this station puts on the air, in the log, said the way a received
/// packet is said.
///
/// Before this the only evidence a send had happened was "tx confirmed (heard
/// own packet id …)" — and only when a receiver happened to hear it back. What
/// was in the frame appeared nowhere, so a request sealed with the wrong
/// channel, aimed at the wrong node, or asking for no response looked exactly
/// like one that did the right thing.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class OutgoingLogTests(HeadlessAvalonia avalonia)
{
    private const uint Us = 0x11111111u;
    private const uint Them = 0x22222222u;

    private static AvaloniaMeshRxHost Station(NodeStore nodes) =>
        new(nodes, new ChannelStore(), new WaypointStore(), new MessageStore(), Us, [],
            null, primaryList: nameof(LoraPreset.MediumFast));

    /// <summary>The channel every frame in here is sealed with: the primary's
    /// own, as the host seeded it.</summary>
    private static ChannelConfig PrimaryChannel(AvaloniaMeshRxHost host) =>
        host.ChannelIn(nameof(LoraPreset.MediumFast), null)!;

    private static string LastLine(AvaloniaMeshRxHost host) => host.LogLines[^1];

    /// <summary>A broadcast says what it carried and that it was a broadcast,
    /// with the packet id so the "tx confirmed" line that follows can be read
    /// with it.</summary>
    [Fact]
    public void ABroadcastSaysWhatItCarried() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);
        var channel = PrimaryChannel(host);

        var frame = MeshEncoder.EncodeNodeInfo(channel, Us, 0x0BADF00D,
            longName: "Test Station", shortName: "TEST", hwModel: 0, role: 0);
        Assert.True(MeshHeader.TryParse(frame, out var header));

        host.LogTransmitted(header, MeshDecoder.Decode(frame, [channel]), string.Empty);

        var line = LastLine(host);
        Assert.Contains("tx 0badf00d", line);
        Assert.Contains($"[{channel.Name}]", line);
        Assert.Contains("broadcast", line);
        Assert.Contains("NodeInfo", line);
        Assert.Contains("Test Station", line);
    }));

    /// <summary>A directed frame names who it is for, by the name the node
    /// list shows — the point being to see at a glance that it went where it
    /// was meant to.</summary>
    [Fact]
    public void ADirectedFrameNamesItsAddressee() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.Upsert(new NodeRecord { NodeNum = Them, LongName = "Alta Repeater", ShortName = "ALTA" });
        var host = Station(nodes);
        var channel = PrimaryChannel(host);

        var frame = MeshEncoder.EncodeTraceroute(channel, Us, Them, 0x0BADF00D);
        Assert.True(MeshHeader.TryParse(frame, out var header));

        host.LogTransmitted(header, MeshDecoder.Decode(frame, [channel]), string.Empty);

        var line = LastLine(host);
        Assert.Contains("tx 0badf00d", line);
        Assert.Contains("to ", line);
        Assert.Contains("Alta Repeater", line);
        Assert.Contains("Traceroute", line);
        Assert.DoesNotContain("broadcast", line);
    }));

    /// <summary>A send on a secondary listener is tagged with its mesh, as a
    /// packet heard on one is. The primary's lines carry no tag, so a station
    /// on one mesh reads exactly as it did before.</summary>
    [Fact]
    public void AFrameOnASecondaryMeshIsTagged() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        var host = Station(nodes);
        var channel = PrimaryChannel(host);

        var frame = MeshEncoder.EncodeTraceroute(channel, Us, Them, 0x0BADF00D);
        Assert.True(MeshHeader.TryParse(frame, out var header));
        var decoded = MeshDecoder.Decode(frame, [channel]);

        host.LogTransmitted(header, decoded, "LongFast 906.875");
        Assert.Contains("[LongFast 906.875]", LastLine(host));

        host.LogTransmitted(header, decoded, string.Empty);
        Assert.DoesNotContain("906.875", LastLine(host));
    }));

    /// <summary>A frame no key here opens still says who it was for and that
    /// it went — the addressing is in the clear whatever the payload is.
    /// </summary>
    [Fact]
    public void AnUnreadableFrameStillSaysWhereItWent() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        nodes.Upsert(new NodeRecord { NodeNum = Them, LongName = "Alta Repeater", ShortName = "ALTA" });
        var host = Station(nodes);

        var frame = MeshEncoder.EncodeTraceroute(PrimaryChannel(host), Us, Them, 0x0BADF00D);
        Assert.True(MeshHeader.TryParse(frame, out var header));

        host.LogTransmitted(header, result: null, string.Empty);

        var line = LastLine(host);
        Assert.Contains("tx 0badf00d", line);
        Assert.Contains("Alta Repeater", line);
        Assert.Contains("not readable here", line);
    }));
}
