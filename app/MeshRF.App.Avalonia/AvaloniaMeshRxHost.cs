// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Minimal <see cref="IMeshRxHost"/> for the Avalonia scaffold: decodes
/// public-channel (default-PSK LongFast) broadcast traffic into a flat
/// message list and keeps <see cref="NodeStore"/> updated from NodeInfo/
/// Position/Telemetry. No chat tabs, relay, MQTT, geofencing, or games yet —
/// those stay WPF-only until ported. Node identity (PKC direct messages) is
/// intentionally unset, so only public-channel broadcasts decode for now.
/// </summary>
public sealed class AvaloniaMeshRxHost : IMeshRxHost
{
    private readonly NodeStore _nodeStore;
    private readonly HashSet<ulong> _recentUndecodedKeys = new();
    private readonly Queue<ulong> _recentUndecodedOrder = new();
    private const int RecentUndecodedLimit = 512;

    public ObservableCollection<ChannelMessage> Messages { get; } = new();
    public ObservableCollection<NodeRecord> Nodes { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public IReadOnlyList<ChannelConfig> Channels { get; } =
    [
        new ChannelConfig { Index = 0, Name = "LongFast", Role = ChannelRole.Primary },
    ];

    /// <summary>
    /// Ephemeral session node number (random, not persisted) — needed so a
    /// transmitted frame carries a valid "from" and gets recognized as our
    /// own echo (isFromUs) instead of a new incoming packet. Real node
    /// identity/PKI management doesn't exist in this scaffold yet.
    /// </summary>
    public uint MyNodeNum { get; set; }

    uint IMeshRxHost.MyNodeNum => MyNodeNum;
    byte[] IMeshRxHost.MyPrivateKeyBytes => Array.Empty<byte>();
    IReadOnlyList<ChannelConfig> IMeshRxHost.Channels => Channels;
    public float CurrentRssiDbfs { get; set; } = float.NegativeInfinity;
    float IMeshRxHost.CurrentRssiDbfs => CurrentRssiDbfs;

    public AvaloniaMeshRxHost(NodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    string? IMeshRxHost.GetStoredPublicKeyHex(uint nodeNum) => _nodeStore.Get(nodeNum)?.PublicKey;

    public void Log(string message)
    {
        LogLines.Add(message);
        while (LogLines.Count > 500) LogLines.RemoveAt(0);
    }

    public void MarkNodeDirty(uint nodeNum)
    {
        var rec = _nodeStore.Get(nodeNum);
        if (rec is null) return;
        var existingIndex = -1;
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].NodeNum == nodeNum) { existingIndex = i; break; }
        }
        if (existingIndex >= 0) Nodes[existingIndex] = rec;
        else Nodes.Add(rec);
    }

    public bool RememberUndecodedPacket(MeshHeader header)
    {
        ulong key = ((ulong)header.From << 32) ^ header.PacketId;
        if (!_recentUndecodedKeys.Add(key)) return false;
        _recentUndecodedOrder.Enqueue(key);
        while (_recentUndecodedOrder.Count > RecentUndecodedLimit)
            _recentUndecodedKeys.Remove(_recentUndecodedOrder.Dequeue());
        return true;
    }

    // No relay, MQTT uplink, or per-duplicate bookkeeping yet.
    public void HandleDuplicateForRelay(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb) { }
    public void RelayIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb) { }
    public void UplinkIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, bool isFromUs, float? snrDb, float? rssiDbm) { }

    public void OnOwnPacketHeard(MeshHeader header, MeshDecodeResult? ownDecode) { }

    public void RecordSighting(uint fromNode, long rxEpoch, float? rssiDbm, float? snrDb, byte hopsAway, bool viaMqtt)
    {
        _nodeStore.RecordSighting(fromNode, rssiDbm: rssiDbm, snrDb: snrDb, hopsAway: hopsAway, seenViaMqtt: viaMqtt);
        MarkNodeDirty(fromNode);
    }

    public void OnMessageDecoded(byte[] frame, MeshHeader header, MessageRecord record, MeshDecodeResult result,
        long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway)
    {
        switch (result.Port)
        {
            case PortNum.TextMessage:
                Messages.Insert(0, new ChannelMessage
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(rxEpoch).LocalDateTime,
                    FromId = header.FromId,
                    SenderNodeNum = header.From,
                    Text = record.Text,
                    RssiDbm = record.RssiDbfs,
                    SnrDb = record.SnrDb,
                    PacketId = header.PacketId,
                });
                while (Messages.Count > 500) Messages.RemoveAt(Messages.Count - 1);
                break;

            case PortNum.NodeInfo when result.User is not null:
                _nodeStore.Upsert(new NodeRecord
                {
                    NodeNum = header.From,
                    UserId = string.IsNullOrEmpty(result.User.Id) ? header.FromId : result.User.Id,
                    LongName = result.User.LongName,
                    ShortName = result.User.ShortName,
                    Role = string.IsNullOrEmpty(result.User.Role) ? "Client" : result.User.Role,
                    PublicKey = result.User.PublicKey.Length == 32 ? Convert.ToHexString(result.User.PublicKey) : string.Empty,
                    LastHeardEpoch = rxEpoch,
                    SeenViaMqtt = header.ViaMqtt,
                    RssiDbm = packetRssiDbm,
                    SnrDb = snrDb,
                    HopsAway = hopsAway,
                });
                MarkNodeDirty(header.From);
                break;

            case PortNum.Position when result.Position is not null:
                _nodeStore.Upsert(new NodeRecord
                {
                    NodeNum = header.From,
                    Latitude = result.Position.Latitude,
                    Longitude = result.Position.Longitude,
                    AltitudeM = result.Position.AltitudeM,
                });
                MarkNodeDirty(header.From);
                break;

            case PortNum.Telemetry when result.Telemetry is not null:
                var t = result.Telemetry;
                _nodeStore.Upsert(new NodeRecord
                {
                    NodeNum = header.From,
                    LastHeardEpoch = rxEpoch,
                    SeenViaMqtt = header.ViaMqtt,
                    BatteryPct = t.BatteryLevel,
                    VoltageV = t.Voltage,
                    ChannelUtilPct = t.ChannelUtilization,
                    AirUtilTxPct = t.AirUtilTx,
                    UptimeSeconds = t.UptimeSeconds,
                    TemperatureC = t.TemperatureC,
                });
                MarkNodeDirty(header.From);
                break;
        }
    }
}
