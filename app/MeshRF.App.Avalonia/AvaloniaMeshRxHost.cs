// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// <see cref="IMeshRxHost"/> for the Avalonia app: decodes traffic on any
/// configured channel (persisted via <see cref="ChannelStore"/>, same
/// %APPDATA%/config path the WPF app uses) into per-channel message tabs,
/// and keeps <see cref="NodeStore"/> updated from NodeInfo/Position/
/// Telemetry. No DM chat tabs, relay, MQTT, geofencing, or games yet —
/// those stay WPF-only until ported. Node identity (PKC direct messages) is
/// intentionally unset, so only channel-PSK traffic decodes for now.
/// </summary>
public sealed class AvaloniaMeshRxHost : IMeshRxHost, IDisposable
{
    private readonly NodeStore _nodeStore;
    private readonly ChannelStore _channelStore;
    private readonly HashSet<ulong> _recentUndecodedKeys = new();
    private readonly Queue<ulong> _recentUndecodedOrder = new();
    private const int RecentUndecodedLimit = 512;
    private const int MaxMessagesPerChannel = 500;

    public ObservableCollection<ChannelTabViewModel> ChannelTabs { get; } = new();
    public ObservableCollection<NodeRecord> Nodes { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>
    /// Ephemeral session node number (random, not persisted) — needed so a
    /// transmitted frame carries a valid "from" and gets recognized as our
    /// own echo (isFromUs) instead of a new incoming packet. Real node
    /// identity/PKI management doesn't exist in this scaffold yet.
    /// </summary>
    public uint MyNodeNum { get; set; }

    uint IMeshRxHost.MyNodeNum => MyNodeNum;
    byte[] IMeshRxHost.MyPrivateKeyBytes => Array.Empty<byte>();
    IReadOnlyList<ChannelConfig> IMeshRxHost.Channels => ChannelTabs.Select(t => t.Config).ToList();
    public float CurrentRssiDbfs { get; set; } = float.NegativeInfinity;
    float IMeshRxHost.CurrentRssiDbfs => CurrentRssiDbfs;

    public AvaloniaMeshRxHost(NodeStore nodeStore, ChannelStore channelStore)
    {
        _nodeStore = nodeStore;
        _channelStore = channelStore;
        LoadChannels();
    }

    private void LoadChannels()
    {
        var configs = _channelStore.All();
        if (configs.Count == 0)
        {
            var primary = new ChannelConfig { Index = 0, Name = "LongFast", Role = ChannelRole.Primary };
            _channelStore.Upsert(primary);
            configs = new[] { primary };
        }

        foreach (var c in configs.OrderBy(c => c.Index))
            ChannelTabs.Add(new ChannelTabViewModel(c));
    }

    /// <summary>Adds and persists a new secondary channel with a fresh random PSK.</summary>
    public ChannelTabViewModel AddChannel(string name)
    {
        int nextIndex = ChannelTabs.Count == 0 ? 0 : ChannelTabs.Max(t => t.Config.Index) + 1;
        var config = new ChannelConfig
        {
            Index = nextIndex,
            Name = name,
            Role = ChannelRole.Secondary,
            Psk = ChannelConfig.NewRandomPsk(),
        };
        _channelStore.Upsert(config);
        var tab = new ChannelTabViewModel(config);
        ChannelTabs.Add(tab);
        return tab;
    }

    private ChannelTabViewModel? ResolveChannelTab(string? channelName)
    {
        if (!string.IsNullOrEmpty(channelName))
        {
            var match = ChannelTabs.FirstOrDefault(t =>
                string.Equals(t.Config.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return ChannelTabs.FirstOrDefault();
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
                var tab = ResolveChannelTab(result.ChannelName);
                if (tab is not null)
                {
                    tab.Messages.Insert(0, new ChannelMessage
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(rxEpoch).LocalDateTime,
                        FromId = header.FromId,
                        SenderNodeNum = header.From,
                        Text = record.Text,
                        RssiDbm = record.RssiDbfs,
                        SnrDb = record.SnrDb,
                        PacketId = header.PacketId,
                    });
                    while (tab.Messages.Count > MaxMessagesPerChannel)
                        tab.Messages.RemoveAt(tab.Messages.Count - 1);
                    tab.TabNeedsAttention = true;
                }
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

    public void Dispose() => _channelStore.Dispose();
}
