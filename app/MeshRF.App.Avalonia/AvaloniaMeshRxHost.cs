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
/// classifies reply/reaction text messages, and keeps <see cref="NodeStore"/>
/// updated from NodeInfo/Position/Telemetry. Relaying and MQTT uplink are
/// delegated out to the view model via <see cref="RelayScheduler"/> and
/// <see cref="UplinkHandler"/>; the games stay WPF-only by choice.
/// </summary>
public sealed class AvaloniaMeshRxHost : IMeshRxHost, IDisposable
{
    private readonly NodeStore _nodeStore;
    private readonly ChannelStore _channelStore;
    private readonly WaypointStore _waypointStore;
    private readonly MessageStore _messageStore;
    private readonly Dictionary<uint, ConversationTabViewModel> _conversationsByNode = new();
    private readonly HashSet<ulong> _recentUndecodedKeys = new();
    private readonly Queue<ulong> _recentUndecodedOrder = new();
    private const int RecentUndecodedLimit = 512;
    private const int MaxMessagesPerTab = 500;

    /// <summary>Outstanding traceroute requests we sent: packetId -> destination node.</summary>
    private readonly Dictionary<uint, uint> _pendingTraceroutes = new();

    /// <summary>Channel tabs and DM conversation tabs, in one list (channels
    /// first, in persisted order; conversations appended as they open).</summary>
    public ObservableCollection<ITabItem> Tabs { get; } = new();

    public ObservableCollection<NodeRecord> Nodes { get; } = new();
    public ObservableCollection<WaypointRecord> Waypoints { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Session node number: MeshRF.App's UserNodeNum when set (shared
    /// settings.json), otherwise an ephemeral random identity so a
    /// transmitted frame still carries a valid "from" and gets recognized
    /// as our own echo (isFromUs) instead of a new incoming packet.</summary>
    public uint MyNodeNum { get; private set; }

    /// <summary>Changes our node number mid-session (edited via the Node
    /// Identity dialog). Existing DM tabs/history keep their old peer keys —
    /// this only affects how future traffic is classified as ours.</summary>
    public void UpdateMyNodeNum(uint nodeNum) => MyNodeNum = nodeNum;

    uint IMeshRxHost.MyNodeNum => MyNodeNum;
    /// <summary>Supplies our X25519 private key so the shared router can
    /// decrypt PKC direct messages. Returning empty here (as this host did
    /// while PKI was unimplemented) silently disables PKC decode for the whole
    /// app: DMs arrive as "rx undecoded", can't be delivered, and — because an
    /// undecodable packet can't be acked — the sender retries until the mesh
    /// is saturated with refloods.</summary>
    public Func<byte[]>? MyPrivateKeyProvider { get; set; }

    /// <summary>The tab currently on screen, supplied by the owner. Activity on
    /// it isn't "unseen", so it must not be flagged for attention.</summary>
    public Func<ITabItem?>? SelectedTabProvider { get; set; }

    /// <summary>Flags a tab as having unseen activity, unless it's the one being
    /// looked at.</summary>
    private void MarkTabNeedsAttention(ITabItem? tab)
    {
        if (tab is null) return;
        if (ReferenceEquals(SelectedTabProvider?.Invoke(), tab)) return;
        tab.TabNeedsAttention = true;
    }

    /// <summary>Unit-aware formatters for history display strings. Owned by the
    /// view model, which holds the unit setting.</summary>
    public Func<float, string>? FormatTemperature { get; set; }
    public Func<float, string>? FormatPressure { get; set; }

    byte[] IMeshRxHost.MyPrivateKeyBytes => MyPrivateKeyProvider?.Invoke() ?? Array.Empty<byte>();
    IReadOnlyList<ChannelConfig> IMeshRxHost.Channels => Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
    public float CurrentRssiDbfs { get; set; } = float.NegativeInfinity;
    float IMeshRxHost.CurrentRssiDbfs => CurrentRssiDbfs;

    /// <summary>Raised whenever a conversation tab opens or closes, so the
    /// owner can persist the updated open-tabs list.</summary>
    public event Action? OpenConversationsChanged;

    /// <summary>Raised when a text message addressed to us arrives from a node
    /// that isn't ignored, so the owner can play the alert tone.</summary>
    public Action? IncomingDirectMessage { get; set; }

    /// <summary>Raised when broadcast text lands on a channel tab that isn't
    /// muted, from a node that's neither ignored nor individually muted.</summary>
    public Action? IncomingChannelMessage { get; set; }

    /// <summary>Raised when a directed request we're the target of wants an
    /// auto-reply (NodeInfo/Position/Telemetry/Traceroute). The owner (which
    /// holds the transmit-capable MeshtasticCore) wires this up; left null
    /// means such requests are simply not answered.</summary>
    public Action<byte[]>? TransmitAutoReply { get; set; }

    /// <summary>Raised when a peer directs a request at us that we should
    /// answer (port, requester, channel the request arrived on). The owner
    /// holds our identity and the transmitter, so it builds the reply.</summary>
    public Action<PortNum, uint, string?>? AutoReplyRequested { get; set; }

    /// <summary>Raised for a directed telemetry request, carrying which metric
    /// group was asked for so the reply matches rather than always answering
    /// with device metrics.</summary>
    public Action<uint, string?, TelemetryVariants>? TelemetryReplyRequested { get; set; }

    /// <summary>Raised for a unicast addressed to us carrying want_ack, so the
    /// owner can transmit the routing ack (header, channel name, whether it was
    /// PKC). Raised even when the payload could not be decrypted: the
    /// addressing is plaintext, and failing to ack is what makes senders
    /// retransmit and the mesh reflood.</summary>
    public Action<MeshHeader, string?, bool>? AckRequested { get; set; }

    /// <summary>Raised for every decoded packet so the owner can serialise it
    /// into the raw JSON feed.</summary>
    public Action<MeshHeader, MeshDecodeResult, long, float?, float?, byte, string>? DecodedPacketForFeed { get; set; }

    /// <summary>Appends a telemetry history point, skipping a payload that
    /// repeats the previous one for the same metric groups — nodes re-send
    /// unchanged telemetry on a timer, and those would otherwise fill the
    /// history with identical rows.</summary>
    private void RecordTelemetryHistory(uint nodeNum, MeshTelemetry telemetry, long rxEpoch)
    {
        if (!TelemetryHistoryFactory.HasAnyMetrics(telemetry)) return;

        var kind = TelemetryHistoryFactory.Kind(telemetry);
        var signature = TelemetryHistoryFactory.Signature(telemetry);
        if (string.Equals(_nodeStore.LatestTelemetrySignature(nodeNum, kind), signature, StringComparison.Ordinal))
            return;

        var timestamp = rxEpoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(rxEpoch).UtcDateTime
            : DateTime.UtcNow;
        _nodeStore.AddTelemetryHistory(TelemetryHistoryFactory.Build(nodeNum, timestamp, telemetry));
    }

    /// <summary>Stored public key for a peer, as hex; empty when unknown.</summary>
    public string PublicKeyHexFor(uint nodeNum) => _nodeStore.Get(nodeNum)?.PublicKey ?? string.Empty;

    /// <summary>A request aimed specifically at us that asked for a response.
    /// Broadcast requests are ignored — answering those would have every
    /// listener reply at once.</summary>
    private bool IsDirectedRequest(MeshHeader header, MeshDecodeResult result) =>
        MyNodeNum != 0 && !header.IsBroadcast && header.To == MyNodeNum && result.WantResponse;

    public AvaloniaMeshRxHost(NodeStore nodeStore, ChannelStore channelStore, WaypointStore waypointStore,
        MessageStore messageStore, uint myNodeNum, IReadOnlyList<uint> openConversationNodeNums)
    {
        _nodeStore = nodeStore;
        _channelStore = channelStore;
        _waypointStore = waypointStore;
        _messageStore = messageStore;
        MyNodeNum = myNodeNum;

        LoadChannels();
        foreach (var wp in _waypointStore.All()) Waypoints.Add(wp);
        foreach (var n in _nodeStore.All()) Nodes.Add(n);
        LoadMessageHistory(openConversationNodeNums);
    }

    /// <summary>Loads channel chat history, then reopens only the DM tabs that
    /// were left open last session (not every peer we have history with) —
    /// mirrors MeshRF.App's MainViewModel, which persists
    /// <c>AppSettings.OpenConversations</c> and replays only those.</summary>
    private void LoadMessageHistory(IReadOnlyList<uint> openConversationNodeNums)
    {
        // Reactions are stored as their own rows, so replay in two passes per
        // tab: real messages first, then attach each reaction to its target.
        // Otherwise a restart turns every reaction into a stray message row.
        var deferred = new Dictionary<ChannelTabViewModel, List<MessageRecord>>();
        foreach (var m in _messageStore.TextHistory())
        {
            if (m.ToNode != 0xFFFFFFFFu) continue; // DMs are rebuilt separately below.
            var tab = ResolveChannelTab(m.Channel);
            if (tab is null) continue;

            if (IsReactionRecord(m))
            {
                if (!deferred.TryGetValue(tab, out var list))
                    deferred[tab] = list = new List<MessageRecord>();
                list.Add(m);
                continue;
            }
            tab.Messages.Add(BuildHistoryMessage(m, tab.Messages));
        }
        foreach (var (tab, reactions) in deferred)
            ApplyHistoryReactions(tab.Messages, reactions);

        if (MyNodeNum == 0) return;
        foreach (var peer in openConversationNodeNums)
        {
            if (peer == 0 || peer == 0xFFFFFFFFu || peer == MyNodeNum) continue;
            OpenConversation(peer);
        }
    }

    /// <summary>Insert by timestamp. History replays chronologically, but
    /// reactions are resolved in a second pass — appending them would bunch
    /// every orphaned reaction at the bottom instead of leaving it where it
    /// happened. Mirrors MeshRF.App's InsertMessageChronologically.</summary>
    private static void InsertChronologically(IList<ChannelMessage> messages, ChannelMessage message)
    {
        int index = messages.Count;
        while (index > 0 && messages[index - 1].Timestamp > message.Timestamp)
            index--;
        messages.Insert(index, message);
    }

    /// <summary>A stored row that represents a tapback rather than a message.</summary>
    private static bool IsReactionRecord(MessageRecord m) =>
        m.IsReaction || (m.Emoji != 0 && m.ReplyId != 0);

    /// <summary>Replay one stored row, rendering reply-linked messages with
    /// their quoted context the same way live ones are.</summary>
    private ChannelMessage BuildHistoryMessage(MessageRecord m, IList<ChannelMessage> existing) =>
        m.ReplyId != 0 ? BuildReplyLinkedMessage(m, existing) : ToChannelMessage(m);

    private void ApplyHistoryReactions(ObservableCollection<ChannelMessage> messages, List<MessageRecord> reactions)
    {
        foreach (var r in reactions)
        {
            if (!TryApplyReaction(messages, r.ReplyId, r.Text, r.Emoji, r.FromNode))
                InsertChronologically(messages, BuildStandaloneReactionMessage(r));
        }
    }

    /// <summary>Persist a message we transmitted, so it survives a restart —
    /// mirrors MeshRF.App's PersistOutgoingText.</summary>
    public void PersistOutgoingText(uint to, uint packetId, string text, string channel, uint replyId = 0)
    {
        if (MyNodeNum == 0) return;
        try
        {
            _messageStore.Add(new MessageRecord
            {
                PacketId = packetId,
                FromNode = MyNodeNum,
                ToNode = to,
                Channel = channel ?? string.Empty,
                PortNum = (int)PortNum.TextMessage,
                Text = text ?? string.Empty,
                ReplyId = replyId,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Delivery = (int)MessageDelivery.Sent,
            });
        }
        catch (Exception ex) { Log($"message store failed: {ex.Message}"); }
    }

    /// <summary>Persist a tapback we sent — mirrors PersistOutgoingReaction.</summary>
    public void PersistOutgoingReaction(uint to, uint packetId, uint replyId, string emojiText, string channel)
    {
        if (MyNodeNum == 0) return;
        try
        {
            _messageStore.Add(new MessageRecord
            {
                PacketId = packetId,
                FromNode = MyNodeNum,
                ToNode = to,
                Channel = channel ?? string.Empty,
                PortNum = (int)PortNum.TextMessage,
                Text = emojiText ?? string.Empty,
                ReplyId = replyId,
                Emoji = 1,
                IsReaction = true,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Delivery = (int)MessageDelivery.None,
            });
        }
        catch (Exception ex) { Log($"reaction store failed: {ex.Message}"); }
    }

    private ChannelMessage ToChannelMessage(MessageRecord m) => new()
    {
        Timestamp = DateTimeOffset.FromUnixTimeSeconds(m.RxEpoch).LocalDateTime,
        FromId = m.FromNode == 0 ? "note" : NodeDisplayName(m.FromNode),
        SenderNodeNum = m.FromNode,
        Text = m.Text,
        RssiDbm = m.RssiDbfs,
        SnrDb = m.SnrDb,
        PacketId = m.PacketId,
        IsOutgoing = MyNodeNum != 0 && m.FromNode == MyNodeNum,
    };

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

    /// <summary>Adds and persists a new secondary channel with an
    /// auto-generated "Channel N" name and a fresh random PSK — mirrors
    /// MeshRF.App's "+" button exactly (no name prompt; rename via the
    /// channel's Settings dialog afterward).</summary>
    public ChannelTabViewModel AddChannel()
    {
        var taken = Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config.Index).ToHashSet();
        int idx = 1;
        while (taken.Contains(idx)) idx++;
        var config = new ChannelConfig
        {
            Index = idx,
            Name = $"Channel {idx}",
            Role = ChannelRole.Secondary,
            Psk = ChannelConfig.NewRandomPsk(),
            PositionPrecision = 0,
        };
        _channelStore.Upsert(config);
        var tab = new ChannelTabViewModel(config);
        // Keep channel tabs contiguous ahead of conversation tabs.
        int insertAt = Tabs.OfType<ChannelTabViewModel>().Count();
        Tabs.Insert(insertAt, tab);
        return tab;
    }

    /// <summary>Removes a secondary channel (the primary channel can never be
    /// removed). Returns false if <paramref name="channel"/> is null or primary.</summary>
    public bool RemoveChannel(ChannelTabViewModel? channel)
    {
        if (channel is null || channel.Config.Role == ChannelRole.Primary) return false;
        _channelStore.Delete(channel.Config.Index);
        Tabs.Remove(channel);
        return true;
    }

    /// <summary>Persists in-place edits to a channel's config (name/role/psk/
    /// position precision, made via the Settings dialog) and refreshes its
    /// tab header.</summary>
    public void SaveChannelConfig(ChannelTabViewModel channel)
    {
        _channelStore.Upsert(channel.Config);
        channel.NotifyConfigChanged();
    }

    public ChannelConfig? FindChannelByName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return Tabs.OfType<ChannelTabViewModel>().FirstOrDefault()?.Config;
        return Tabs.OfType<ChannelTabViewModel>()
            .FirstOrDefault(t => string.Equals(t.Config.Name, name, StringComparison.OrdinalIgnoreCase))?.Config;
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

    /// <summary>Finds or opens the DM conversation tab for a peer node,
    /// loading its persisted history (including notes and reactions) the
    /// first time it's opened.</summary>
    public ConversationTabViewModel OpenConversation(uint nodeNum)
    {
        if (_conversationsByNode.TryGetValue(nodeNum, out var existing)) return existing;

        // The store is handed over so the tab can load this peer's recorded
        // location/telemetry history; the formatters keep its display strings
        // on the app's unit setting.
        var convo = new ConversationTabViewModel(nodeNum, NodeDisplayName(nodeNum),
                                                 _nodeStore, () => FormatTemperature, () => FormatPressure)
        {
            Node = _nodeStore.Get(nodeNum),
        };
        _conversationsByNode[nodeNum] = convo;
        Tabs.Add(convo);

        // Same two-pass replay as the channel tabs: messages, then reactions.
        var reactions = new List<MessageRecord>();
        foreach (var m in _messageStore.Conversation(nodeNum, MyNodeNum))
        {
            if (IsReactionRecord(m)) { reactions.Add(m); continue; }
            convo.Messages.Add(BuildHistoryMessage(m, convo.Messages));
        }
        ApplyHistoryReactions(convo.Messages, reactions);

        OpenConversationsChanged?.Invoke();
        return convo;
    }

    /// <summary>Closes a DM conversation tab (channels can't be closed).</summary>
    public void CloseConversation(ConversationTabViewModel convo)
    {
        _conversationsByNode.Remove(convo.NodeNum);
        Tabs.Remove(convo);
        OpenConversationsChanged?.Invoke();
    }

    public IEnumerable<uint> OpenConversationNodeNums => _conversationsByNode.Keys;

    /// <summary>Append a locally-generated note (request sent, traceroute
    /// result, etc.) to a peer's DM tab and persist it so it survives a
    /// restart — mirrors MainViewModel's PersistConversationNote.</summary>
    public void AddNote(uint peer, bool outgoing, uint packetId, string tag, string text,
        float? rssi = null, float? snr = null)
    {
        if (MyNodeNum == 0 || peer == 0 || peer == 0xFFFFFFFFu) return;
        var convo = OpenConversation(peer);
        convo.Messages.Add(new ChannelMessage
        {
            FromId = tag,
            Text = text,
            IsOutgoing = outgoing,
            PacketId = packetId,
        });
        try
        {
            _messageStore.Add(new MessageRecord
            {
                PacketId = packetId,
                FromNode = outgoing ? MyNodeNum : peer,
                ToNode = outgoing ? peer : MyNodeNum,
                Channel = tag,
                PortNum = MessageStore.ConversationNotePort,
                Text = text,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RssiDbfs = rssi,
                SnrDb = snr,
                Delivery = (int)MessageDelivery.None,
            });
        }
        catch (Exception ex) { Log($"note store failed: {ex.Message}"); }
    }

    public string NodeDisplayName(uint nodeNum)
    {
        // Our own record is checked first rather than short-circuiting to "me":
        // once the identity is configured, our long name is the useful label,
        // and it's what other clients see. "me" is only the fallback for a
        // self node that has no name yet.
        var rec = _nodeStore.Get(nodeNum);
        if (rec is not null)
        {
            if (!string.IsNullOrWhiteSpace(rec.LongName)) return rec.LongName;
            if (!string.IsNullOrWhiteSpace(rec.ShortName)) return rec.ShortName;
        }
        if (MyNodeNum != 0 && nodeNum == MyNodeNum) return "me";
        return $"!{nodeNum:x8}";
    }

    /// <summary>Writes our own node into the store so our configured name
    /// resolves everywhere a node number is displayed — the node grid, chat
    /// sender labels, and reaction attributions.</summary>
    public void UpsertSelf(string longName, string shortName, string hwModel, string role,
                           string nodeStatus, string publicKeyHex)
    {
        if (MyNodeNum == 0) return;
        _nodeStore.Upsert(new NodeRecord
        {
            NodeNum = MyNodeNum,
            UserId = $"!{MyNodeNum:x8}",
            LongName = longName ?? string.Empty,
            ShortName = shortName ?? string.Empty,
            HwModel = hwModel ?? string.Empty,
            Role = role ?? string.Empty,
            NodeStatus = nodeStatus ?? string.Empty,
            PublicKey = publicKeyHex ?? string.Empty,
        });
        MarkNodeDirty(MyNodeNum);
    }

    public bool IsNodeIgnored(uint nodeNum) => _nodeStore.Get(nodeNum)?.Ignored == true;

    public void SetNodeIgnored(uint nodeNum, bool ignored)
    {
        _nodeStore.SetIgnored(nodeNum, ignored);
        MarkNodeDirty(nodeNum);
    }

    public void SetNodeFavorite(uint nodeNum, bool favorite)
    {
        _nodeStore.SetFavorite(nodeNum, favorite);
        MarkNodeDirty(nodeNum);
    }

    public void ForgetNode(uint nodeNum)
    {
        _nodeStore.Forget(nodeNum);
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].NodeNum == nodeNum) { Nodes.RemoveAt(i); break; }
        }
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
        {
            convo.PeerName = NodeDisplayName(nodeNum);
            // Same record instance as before, so assigning it wouldn't raise
            // the setter — refresh explicitly so the telemetry panel follows.
            if (ReferenceEquals(convo.Node, rec)) convo.RefreshNodeSnapshot();
            else convo.Node = rec;
        }
    }

    public bool RememberUndecodedPacket(MeshHeader header)
    {
        ulong key = ((ulong)header.From << 32) ^ header.PacketId;
        if (!_recentUndecodedKeys.Add(key)) return false;
        _recentUndecodedOrder.Enqueue(key);
        while (_recentUndecodedOrder.Count > RecentUndecodedLimit)
            _recentUndecodedKeys.Remove(_recentUndecodedOrder.Dequeue());
        // Deliberately no ack here, matching MeshRF.App: a packet we could not
        // decode is one we cannot claim to have received. The fix for an
        // unacked PKC direct message is to decrypt it (see
        // MyPrivateKeyProvider) so it takes the decoded path, not to
        // acknowledge blind.
        return true;
    }

    /// <summary>Supplies the current relay configuration. Owned by the view
    /// model, which holds the role, rebroadcast mode and modem preset.</summary>
    public Func<RelayContext?>? RelayContextProvider { get; set; }

    /// <summary>Rebroadcasts eligible traffic after a contention delay. Null
    /// until the owner wires it up, which leaves relaying off.</summary>
    public RelayScheduler? RelayScheduler { get; set; }

    public void HandleDuplicateForRelay(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb)
    {
        if (RelayScheduler is null || RelayContextProvider?.Invoke() is not { } ctx) return;
        // True means this copy arrived with more hops left than the one we had
        // queued, so it's worth relaying instead.
        if (RelayScheduler.HandleDuplicate(ctx, header))
            RelayIfEligible(frame, header, result, snrDb);
    }

    public void RelayIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb)
    {
        if (RelayScheduler is null || RelayContextProvider?.Invoke() is not { } ctx) return;
        if (!RelayPolicy.ShouldRelay(ctx, header, result, IsNodeIgnored(header.From))) return;

        byte nextHopLimit = RelayPolicy.ShouldDecrementHopLimit(ctx, header)
            ? (byte)Math.Max(0, header.HopLimit - 1)
            : header.HopLimit;

        var relayFrame = RelayPolicy.BuildRelayFrame(ctx, frame, nextHopLimit);
        int delayMs = RelayPolicy.GetTxDelayMsecWeighted(
            ctx.Preset, snrDb ?? 0f, RelayPolicy.IsRouterRole(ctx.Role));

        RelayScheduler.Schedule(header, relayFrame, nextHopLimit, delayMs);
    }

    /// <summary>Publishes eligible traffic to the MQTT bridge. Null until the
    /// owner wires it up, which leaves uplink off.</summary>
    public Action<byte[], MeshHeader, MeshDecodeResult?, bool, float?, float?>? UplinkHandler { get; set; }

    public void UplinkIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, bool isFromUs, float? snrDb, float? rssiDbm) =>
        UplinkHandler?.Invoke(frame, header, result, isFromUs, snrDb, rssiDbm);

    public void OnOwnPacketHeard(MeshHeader header, MeshDecodeResult? ownDecode) { }

    public void RecordSighting(uint fromNode, long rxEpoch, float? rssiDbm, float? snrDb, byte hopsAway, bool viaMqtt)
    {
        _nodeStore.RecordSighting(fromNode, rssiDbm: rssiDbm, snrDb: snrDb, hopsAway: hopsAway, seenViaMqtt: viaMqtt);
        MarkNodeDirty(fromNode);
    }

    public void OnMessageDecoded(byte[] frame, MeshHeader header, MessageRecord record, MeshDecodeResult result,
        long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway)
    {
        // Log every successful decode, like MeshRF.App's OnMessageDecoded does.
        // Without this the log only ever shows MeshRxRouter's "(dup)" and
        // "rx undecoded" lines, making normal flood retransmissions look like
        // every packet was being rejected as a duplicate.
        var summary = BuildDecodedPortSummary(header, result, NodeDisplayName(header.From));
        Log(summary);
        DecodedPacketForFeed?.Invoke(header, result, rxEpoch, snrDb, packetRssiDbm, hopsAway, summary);

        // Only reached for a packet that decoded and passed dedupe, so this is
        // exactly one ack per unique message — matching MeshRF.App.
        if (header.WantAck)
            AckRequested?.Invoke(header, result.ChannelName,
                                 string.Equals(result.ChannelName, "PKC", StringComparison.Ordinal));

        switch (result.Port)
        {
            case PortNum.TextMessage:
                HandleTextMessage(header, record, result);
                break;

            case PortNum.NodeInfo when result.User is not null:
                // An empty NodeInfo payload carrying want_response is a pure
                // *request*, not an advertisement. Answering it is how other
                // nodes learn our name; upserting it would overwrite the
                // sender's record with blanks.
                if (result.AppPayload.Length == 0)
                {
                    if (IsDirectedRequest(header, result)) AutoReplyRequested?.Invoke(PortNum.NodeInfo, header.From, result.ChannelName);
                    break;
                }
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
                // An advertisement may still ask us to reply with ours.
                if (IsDirectedRequest(header, result)) AutoReplyRequested?.Invoke(PortNum.NodeInfo, header.From, result.ChannelName);
                break;

            case PortNum.Position when result.Position is not null:
                // A zero-island position with want_response is the request
                // form, the same convention NodeInfo uses.
                if (result.Position.Latitude == 0 && result.Position.Longitude == 0 &&
                    IsDirectedRequest(header, result))
                {
                    AutoReplyRequested?.Invoke(PortNum.Position, header.From, result.ChannelName);
                    break;
                }
                // Record a history point only when the coordinates actually
                // moved: position packets repeat unchanged, and storing every
                // one would bury real movement in duplicates.
                var previous = _nodeStore.Get(header.From);
                bool positionChanged =
                    previous?.Latitude != result.Position.Latitude ||
                    previous?.Longitude != result.Position.Longitude;

                _nodeStore.Upsert(new NodeRecord
                {
                    NodeNum = header.From,
                    Latitude = result.Position.Latitude,
                    Longitude = result.Position.Longitude,
                    AltitudeM = result.Position.AltitudeM,
                });
                if (positionChanged)
                    _nodeStore.AddLocationHistory(
                        header.From,
                        DateTimeOffset.FromUnixTimeSeconds(rxEpoch).UtcDateTime,
                        result.Position.Latitude, result.Position.Longitude, result.Position.AltitudeM);
                MarkNodeDirty(header.From);
                break;

            case PortNum.Waypoint when result.Waypoint is not null:
                HandleWaypoint(header, result);
                break;

            case PortNum.Telemetry when result.Telemetry is not null:
                // Firmware's DeviceTelemetryModule answers a directed telemetry
                // request with its device metrics; an empty payload is the
                // request form.
                if (result.AppPayload.Length == 0 && IsDirectedRequest(header, result))
                {
                    TelemetryReplyRequested?.Invoke(header.From, result.ChannelName,
                                                    result.Telemetry.PresentVariants);
                    break;
                }
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
                RecordTelemetryHistory(header.From, t, rxEpoch);
                MarkNodeDirty(header.From);
                if (IsDirectedRequest(header, result))
                    TelemetryReplyRequested?.Invoke(header.From, result.ChannelName, t.PresentVariants);
                break;

            case PortNum.Traceroute:
                HandleTraceroute(header, result);
                break;
        }
    }

    /// <summary>One-line log summary of a decoded packet, mirroring
    /// MeshRF.App's BuildDecodedPortSummary.</summary>
    private string BuildDecodedPortSummary(MeshHeader header, MeshDecodeResult result, string senderName)
    {
        string prefix = $"  [{result.ChannelName}] {senderName} {result.Port}";
        string size = $" ({result.AppPayload.Length} B)";

        return result.Port switch
        {
            PortNum.TextMessage when result.ReplyId != 0 && result.Emoji != 0
                => $"{prefix}: reaction {ResolveReactionGlyph(result.Text, result.Emoji)} -> {result.ReplyId:x8}{size}",
            PortNum.TextMessage
                => $"{prefix}: \"{TrimForReplyPreview(result.Text)}\"{size}",
            PortNum.NodeInfo when result.User is not null
                => $"{prefix}: user={result.User.LongName} ({result.User.ShortName}){size}",
            PortNum.Position when result.Position is not null
                => $"{prefix}: lat={result.Position.Latitude:F5} lon={result.Position.Longitude:F5}{size}",
            PortNum.Waypoint when result.Waypoint is not null
                => $"{prefix}: waypoint={result.Waypoint.Name} lat={result.Waypoint.Latitude:F5} lon={result.Waypoint.Longitude:F5}{size}",
            PortNum.Telemetry when result.Telemetry is not null
                => $"{prefix}: telemetry{size}",
            PortNum.NodeStatus when result.StatusMessage is not null
                => $"{prefix}: status=\"{TrimForReplyPreview(result.StatusMessage.Status)}\"{size}",
            PortNum.Routing when result.RoutingError >= 0
                => $"{prefix}: {(result.RoutingError == 0 ? "ACK" : $"NAK reason={result.RoutingError}")}{size}",
            PortNum.Traceroute when result.RouteDiscovery is not null
                => $"{prefix}: route={result.RouteDiscovery.Route.Count} back={result.RouteDiscovery.RouteBack.Count}{size}",
            PortNum.NeighborInfo when result.NeighborInfo is not null
                => $"{prefix}: node=!{result.NeighborInfo.NodeId:x8} neighbors={result.NeighborInfo.Neighbors.Count}{size}",
            PortNum.StoreForward when result.StoreForward is not null
                => $"{prefix}: type={result.StoreForward.Type}{size}",
            _ => $"{prefix}: to={header.ToId}{size}",
        };
    }

    private void HandleTextMessage(MeshHeader header, MessageRecord record, MeshDecodeResult result)
    {
        uint reactionTargetId = ResolveReactionTargetId(result);
        bool isReaction = reactionTargetId != 0 && result.Emoji != 0;
        bool isReplyLinkedNonReaction = reactionTargetId != 0 && !isReaction;
        bool isDirectToUs = MyNodeNum != 0 && !header.IsBroadcast && header.To == MyNodeNum;

        ObservableCollection<ChannelMessage>? messages;
        bool existed;
        if (isDirectToUs)
        {
            existed = _conversationsByNode.ContainsKey(header.From);
            messages = OpenConversation(header.From).Messages;
        }
        else
        {
            var chanTab = ResolveChannelTab(result.ChannelName);
            existed = chanTab is not null;
            messages = chanTab?.Messages;
        }

        if (messages is not null)
        {
            if (isReaction)
            {
                if (!TryApplyReaction(messages, reactionTargetId, result.Text, result.Emoji, header.From))
                    messages.Add(BuildStandaloneReactionMessage(record));
            }
            else if (isReplyLinkedNonReaction)
            {
                if (existed) messages.Add(BuildReplyLinkedMessage(record, messages));
            }
            else if (existed)
            {
                messages.Add(new ChannelMessage
                {
                    // Resolved name, not the raw !id — history replay uses
                    // NodeDisplayName, so using the id here is what made a
                    // message change its sender label on reload.
                    FromId = NodeDisplayName(header.From),
                    SenderNodeNum = header.From,
                    Text = record.Text,
                    RssiDbm = record.RssiDbfs,
                    SnrDb = record.SnrDb,
                    PacketId = header.PacketId,
                    IsIgnoredSender = IsNodeIgnored(header.From),
                });
            }
            while (messages.Count > MaxMessagesPerTab) messages.RemoveAt(0); // oldest first now
        }

        if (isDirectToUs)
        {
            MarkTabNeedsAttention(_conversationsByNode[header.From]);
            // Alert only for real messages from nodes we haven't ignored —
            // a tapback shouldn't ring.
            if (!isReaction && !IsNodeIgnored(header.From)) IncomingDirectMessage?.Invoke();
        }
        else if (ResolveChannelTab(result.ChannelName) is { } chanTab2)
        {
            MarkTabNeedsAttention(chanTab2);
            // Ring on channel traffic unless the channel is muted, the sender is
            // ignored, or that node is individually muted. Matches MeshRF.App,
            // which also rings for channel reactions (unlike the DM path above).
            if (!chanTab2.MuteRtttl && !IsNodeIgnored(header.From) && !IsNodeRtttlMuted(header.From))
                IncomingChannelMessage?.Invoke();
        }
    }

    private bool IsNodeRtttlMuted(uint nodeNum) => _nodeStore.Get(nodeNum)?.MuteRtttl == true;

    private void HandleWaypoint(MeshHeader header, MeshDecodeResult result)
    {
        var wp = result.Waypoint!;
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
            RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
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
    }

    /// <summary>Handle a TRACEROUTE_APP frame: either the accumulated-path
    /// reply to a request we sent, or (if want_response and addressed to us)
    /// a request from someone else, auto-replied via <see cref="TransmitAutoReply"/>.</summary>
    private void HandleTraceroute(MeshHeader header, MeshDecodeResult result)
    {
        if (result.RequestId != 0 && _pendingTraceroutes.TryGetValue(result.RequestId, out var dest))
        {
            _pendingTraceroutes.Remove(result.RequestId);
            var path = FormatTraceroute(MyNodeNum, dest, result.RouteDiscovery);
            AddNote(dest, outgoing: false, header.PacketId, "traceroute", $"Route to {NodeDisplayName(dest)}: {path}");
        }

        if (MyNodeNum != 0 && !header.IsBroadcast && header.To == MyNodeNum && result.WantResponse
            && TransmitAutoReply is { } reply)
        {
            var primary = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault(t => t.Config.Role == ChannelRole.Primary)
                          ?? Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
            if (primary is not null)
            {
                var frame = MeshEncoder.EncodeTracerouteReply(primary.Config, MyNodeNum, header.From,
                    (uint)Random.Shared.NextInt64(1, uint.MaxValue), header.PacketId, route: null, snrTowards: null);
                reply(frame);
            }
        }
    }

    public void RegisterOutgoingTraceroute(uint packetId, uint destination) => _pendingTraceroutes[packetId] = destination;

    private static string FormatTraceroute(uint origin, uint dest, MeshRouteDiscovery? rd)
    {
        var nodes = new List<uint> { origin };
        if (rd?.Route is { Count: > 0 } hops) nodes.AddRange(hops);
        nodes.Add(dest);
        var snr = rd?.SnrTowards ?? (IReadOnlyList<int>)Array.Empty<int>();

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" -> ");
                int idx = i - 1;
                if (idx < snr.Count)
                {
                    int raw = snr[idx];
                    sb.Append(raw <= -128 ? "(?) " : $"({(raw / 4.0):0.#} dB) ");
                }
            }
            sb.Append(nodes[i] == 0 || nodes[i] == 0xFFFFFFFFu ? "unknown" : $"!{nodes[i]:x8}");
        }
        int hopCount = nodes.Count - 1;
        sb.Append(hopCount <= 1 ? "  [direct]" : $"  [{hopCount} hops]");
        return sb.ToString();
    }

    private bool TryApplyReaction(IList<ChannelMessage> messages, uint replyId, string? reactionText, uint emoji, uint fromNode)
    {
        if (replyId == 0 || emoji == 0 || messages.Count == 0) return false;
        var glyph = ResolveReactionGlyph(reactionText, emoji);
        if (glyph.Length == 0) return false;

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.PacketId != replyId) continue;
            msg.AddReaction(glyph, NodeDisplayName(fromNode));
            return true;
        }
        return false;
    }

    private static string ResolveReactionGlyph(string? reactionText, uint emoji)
    {
        var text = (reactionText ?? string.Empty).Trim();
        if (text.Length > 0) return text;
        return CodePointToEmoji(emoji);
    }

    private ChannelMessage BuildStandaloneReactionMessage(MessageRecord reaction)
    {
        bool outgoing = MyNodeNum != 0 && reaction.FromNode == MyNodeNum;
        var glyph = ResolveReactionGlyph(reaction.Text, reaction.Emoji);
        if (glyph.Length == 0) glyph = "(reaction)";
        var targetText = reaction.ReplyId != 0 ? $"{reaction.ReplyId:x8}" : "unknown";

        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(reaction.RxEpoch).LocalDateTime,
            FromId = NodeDisplayName(reaction.FromNode),
            SenderNodeNum = reaction.FromNode,
            Text = $"reacted {glyph} (original message {targetText} not found)",
            RssiDbm = reaction.RssiDbfs,
            SnrDb = reaction.SnrDb,
            PacketId = reaction.PacketId,
            IsOutgoing = outgoing,
            IsIgnoredSender = !outgoing && IsNodeIgnored(reaction.FromNode),
        };
    }

    private ChannelMessage BuildReplyLinkedMessage(MessageRecord reply, IList<ChannelMessage> messages)
    {
        bool outgoing = MyNodeNum != 0 && reply.FromNode == MyNodeNum;
        var body = string.IsNullOrWhiteSpace(reply.Text) ? "(empty reply)" : reply.Text;

        ChannelMessage? target = null;
        foreach (var candidate in messages)
        {
            if (candidate.PacketId != reply.ReplyId) continue;
            target = candidate;
            break;
        }

        string context = target is not null
            ? BuildReplyContextText(target)
            : $"replying to {reply.ReplyId:x8} (original message not found)";

        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(reply.RxEpoch).LocalDateTime,
            FromId = NodeDisplayName(reply.FromNode),
            SenderNodeNum = reply.FromNode,
            Text = $"{context}\n{body}",
            RssiDbm = reply.RssiDbfs,
            SnrDb = reply.SnrDb,
            PacketId = reply.PacketId,
            IsOutgoing = outgoing,
            IsIgnoredSender = !outgoing && IsNodeIgnored(reply.FromNode),
            IsReplyLinked = true,
            ReplyTargetFound = target is not null,
            ReplyToPacketId = reply.ReplyId,
        };
    }

    private static string BuildReplyContextText(ChannelMessage message)
    {
        var from = string.IsNullOrWhiteSpace(message.FromId) ? "unknown" : message.FromId.Trim();
        var original = TrimForReplyPreview(ExtractReplyLeafText(message.Text));
        return $"replying to {from}: \"{original}\"";
    }

    private static string ExtractReplyLeafText(string? text)
    {
        var raw = text ?? string.Empty;
        if (raw.Length == 0) return string.Empty;
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? string.Empty : lines[^1].Trim();
    }

    private static string TrimForReplyPreview(string? text)
    {
        var normalized = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length == 0) return "(empty)";
        return normalized.Length <= 80 ? normalized : normalized[..80] + "...";
    }

    private static uint ResolveReactionTargetId(MeshDecodeResult result)
    {
        if (result.ReplyId != 0) return result.ReplyId;
        if (result.RequestId != 0) return result.RequestId;
        return 0;
    }

    private static string CodePointToEmoji(uint codePoint)
    {
        if (codePoint is 0 or > 0x10FFFFu) return string.Empty;
        try { return char.ConvertFromUtf32((int)codePoint); }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        _channelStore.Dispose();
        _waypointStore.Dispose();
    }
}
