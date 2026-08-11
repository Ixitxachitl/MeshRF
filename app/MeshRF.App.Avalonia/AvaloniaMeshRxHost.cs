// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// <see cref="IMeshRxHost"/> for the Avalonia app: decodes traffic on any
/// configured channel (persisted via <see cref="ChannelStore"/>, same
/// %APPDATA%/config path the WPF app uses) into per-channel message tabs,
/// routes direct messages addressed to us into per-peer conversation tabs,
/// and keeps <see cref="NodeStore"/> updated from NodeInfo/Position/
/// Telemetry. No relay, MQTT, geofencing, or games yet — those stay
/// WPF-only until ported. PKC (public-key) direct messages aren't
/// supported (no node identity/PKI management in this scaffold yet); DMs
/// are sent/received as legacy channel-PSK-encrypted unicast instead,
/// exactly like a broadcast but addressed to one node.
/// </summary>
public sealed class AvaloniaMeshRxHost : IMeshRxHost, IDisposable
{
    private readonly NodeStore _nodeStore;
    private readonly ChannelStore _channelStore;
    private readonly WaypointStore _waypointStore;
    private readonly Dictionary<uint, ConversationTabViewModel> _conversationsByNode = new();
    private readonly HashSet<ulong> _recentUndecodedKeys = new();
    private readonly Queue<ulong> _recentUndecodedOrder = new();
    private const int RecentUndecodedLimit = 512;
    private const int MaxMessagesPerTab = 500;

    /// <summary>Channel tabs and DM conversation tabs, in one list (channels
    /// first, in persisted order; conversations appended as they open).</summary>
    public ObservableCollection<ITabItem> Tabs { get; } = new();

    public ObservableCollection<NodeRecord> Nodes { get; } = new();
    public ObservableCollection<WaypointRecord> Waypoints { get; } = new();
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
    IReadOnlyList<ChannelConfig> IMeshRxHost.Channels => Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
    public float CurrentRssiDbfs { get; set; } = float.NegativeInfinity;
    float IMeshRxHost.CurrentRssiDbfs => CurrentRssiDbfs;

    public AvaloniaMeshRxHost(NodeStore nodeStore, ChannelStore channelStore, WaypointStore waypointStore)
    {
        _nodeStore = nodeStore;
        _channelStore = channelStore;
        _waypointStore = waypointStore;
        LoadChannels();
        foreach (var wp in _waypointStore.All()) Waypoints.Add(wp);
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
            Tabs.Add(new ChannelTabViewModel(c));
    }

    /// <summary>Adds and persists a new secondary channel with a fresh random PSK.</summary>
    public ChannelTabViewModel AddChannel(string name)
    {
        var existingChannels = Tabs.OfType<ChannelTabViewModel>().ToList();
        int nextIndex = existingChannels.Count == 0 ? 0 : existingChannels.Max(t => t.Config.Index) + 1;
        var config = new ChannelConfig
        {
            Index = nextIndex,
            Name = name,
            Role = ChannelRole.Secondary,
            Psk = ChannelConfig.NewRandomPsk(),
        };
        _channelStore.Upsert(config);
        var tab = new ChannelTabViewModel(config);
        // Keep channel tabs contiguous ahead of conversation tabs.
        int insertAt = Tabs.OfType<ChannelTabViewModel>().Count();
        Tabs.Insert(insertAt, tab);
        return tab;
    }

    private ChannelTabViewModel? ResolveChannelTab(string? channelName)
    {
        var channelTabs = Tabs.OfType<ChannelTabViewModel>();
        if (!string.IsNullOrEmpty(channelName))
        {
            var match = channelTabs.FirstOrDefault(t =>
                string.Equals(t.Config.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return channelTabs.FirstOrDefault();
    }

    /// <summary>Finds or opens the DM conversation tab for a peer node.</summary>
    public ConversationTabViewModel OpenConversation(uint nodeNum)
    {
        if (_conversationsByNode.TryGetValue(nodeNum, out var existing)) return existing;

        var convo = new ConversationTabViewModel(nodeNum, NodeDisplayName(nodeNum));
        _conversationsByNode[nodeNum] = convo;
        Tabs.Add(convo);
        return convo;
    }

    private string NodeDisplayName(uint nodeNum)
    {
        var rec = _nodeStore.Get(nodeNum);
        if (rec is not null)
        {
            if (!string.IsNullOrWhiteSpace(rec.LongName)) return rec.LongName;
            if (!string.IsNullOrWhiteSpace(rec.ShortName)) return rec.ShortName;
        }
        return $"!{nodeNum:x8}";
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

        if (_conversationsByNode.TryGetValue(nodeNum, out var convo))
            convo.PeerName = NodeDisplayName(nodeNum);
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
                bool isDirectToUs = MyNodeNum != 0 && !header.IsBroadcast && header.To == MyNodeNum;
                var messages = isDirectToUs ? OpenConversation(header.From).Messages : ResolveChannelTab(result.ChannelName)?.Messages;
                if (messages is not null)
                {
                    messages.Insert(0, new ChannelMessage
                    {
                        Timestamp = DateTimeOffset.FromUnixTimeSeconds(rxEpoch).LocalDateTime,
                        FromId = header.FromId,
                        SenderNodeNum = header.From,
                        Text = record.Text,
                        RssiDbm = record.RssiDbfs,
                        SnrDb = record.SnrDb,
                        PacketId = header.PacketId,
                    });
                    while (messages.Count > MaxMessagesPerTab)
                        messages.RemoveAt(messages.Count - 1);
                }

                if (isDirectToUs) _conversationsByNode[header.From].TabNeedsAttention = true;
                else if (ResolveChannelTab(result.ChannelName) is { } chanTab) chanTab.TabNeedsAttention = true;
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

            case PortNum.Waypoint when result.Waypoint is not null:
                var wp = result.Waypoint;
                // Some senders omit waypoint id (0); fall back to packet id
                // as a stable per-sender key, same as MainViewModel.
                uint waypointId = wp.Id != 0 ? wp.Id : header.PacketId;
                var waypointRecord = new WaypointRecord
                {
                    FromNode = header.From,
                    WaypointId = waypointId,
                    PacketId = header.PacketId,
                    Channel = result.ChannelName,
                    Name = wp.Name,
                    Description = wp.Description,
                    Icon = wp.Icon,
                    Latitude = wp.Latitude,
                    Longitude = wp.Longitude,
                    ExpireEpoch = wp.ExpireEpoch,
                    LockedTo = wp.LockedTo,
                    RxEpoch = rxEpoch,
                    GeofenceRadius = wp.GeofenceRadius,
                    BboxWest = wp.BoundingBox?.West,
                    BboxSouth = wp.BoundingBox?.South,
                    BboxEast = wp.BoundingBox?.East,
                    BboxNorth = wp.BoundingBox?.North,
                };
                _waypointStore.Upsert(waypointRecord);
                var existingWpIndex = -1;
                for (int i = 0; i < Waypoints.Count; i++)
                {
                    if (Waypoints[i].FromNode == header.From && Waypoints[i].WaypointId == waypointId)
                    {
                        existingWpIndex = i;
                        break;
                    }
                }
                if (existingWpIndex >= 0) Waypoints[existingWpIndex] = waypointRecord;
                else Waypoints.Add(waypointRecord);
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

    public void Dispose()
    {
        _channelStore.Dispose();
        _waypointStore.Dispose();
    }
}
