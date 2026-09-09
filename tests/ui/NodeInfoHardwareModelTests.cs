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
/// A NodeInfo says what the node is built on, and that is the only way this
/// station ever learns it — nothing else on the air carries a hardware model.
/// The decoder read it all along; the upsert that files a NodeInfo away simply
/// never carried it across, so every node heard over the air showed a blank
/// hardware row no matter how many NodeInfos it sent.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class NodeInfoHardwareModelTests(HeadlessAvalonia avalonia)
{
    private const uint Us = 0x11111111u;
    private const uint Peer = 0x4A2B3C4Du;

    private static ChannelConfig Channel() => new()
    {
        Index = 0,
        Name = nameof(LoraPreset.MediumFast),
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    /// <summary>A NODEINFO_APP frame whose User carries <paramref name="hwModel"/>
    /// in field 5, the number firmware puts on the wire.</summary>
    private static byte[] NodeInfo(int hwModel)
    {
        var user = new ProtoWriter();
        user.WriteStringField(1, $"!{Peer:x8}");
        user.WriteStringField(2, "Hardware Node");
        user.WriteStringField(3, "HW");
        if (hwModel != 0) user.WriteVarintField(5, (ulong)hwModel);
        return MeshEncoder.Encode(Channel(), Peer, 0xFFFFFFFFu, 1,
                                  PortNum.NodeInfo, user.ToArray());
    }

    /// <summary>Decodes the frame and files it away exactly as a reception
    /// does, then reports what the node store ended up holding.</summary>
    private static string? FileAway(NodeStore nodes, int hwModel)
    {
        var host = new AvaloniaMeshRxHost(nodes, new ChannelStore(), new WaypointStore(),
                                          new MessageStore(), Us, [], null,
                                          primaryList: nameof(LoraPreset.MediumFast));
        var frame = NodeInfo(hwModel);
        var result = MeshDecoder.Decode(frame, [Channel()]);
        Assert.NotNull(result?.User);
        Assert.True(MeshHeader.TryParse(frame, out var header));

        host.OnMessageDecoded(frame, header, new MessageRecord { FromNode = Peer }, result!,
                              rxEpoch: 1_700_000_000, SignalReading.None, hopsAway: 0,
                              RxSource.Primary(LoraPreset.MediumFast, isCustom: false, freqMHz: 913.125));
        return nodes.Get(Peer)?.HwModel;
    }

    /// <summary>The regression: a node that says what it is gets it recorded.</summary>
    [Fact]
    public void ANodeInfoRecordsTheHardwareModelItAdvertised() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        // 9 is RAK4631, and it is stored by name so the record outlives the
        // enum it was read through.
        Assert.Equal("RAK4631", FileAway(nodes, 9));
    }));

    /// <summary>An id this build's enum does not know is still worth keeping:
    /// a newer board is better recorded by number than lost.</summary>
    [Fact]
    public void AnUnknownModelIsKeptRatherThanDropped() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        Assert.Equal("UNKNOWN_254", FileAway(nodes, 254));
    }));

    /// <summary>Zero is UNSET, which is what a node that does not say sends.
    /// Writing "UNSET" over a model an earlier NodeInfo did name would lose it,
    /// so an absent value leaves what is on file alone.</summary>
    [Fact]
    public void ANodeInfoWithoutAModelLeavesTheOneOnFileAlone() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var nodes = new NodeStore();
        Assert.Equal("RAK4631", FileAway(nodes, 9));
        Assert.Equal("RAK4631", FileAway(nodes, 0));
    }));
}
