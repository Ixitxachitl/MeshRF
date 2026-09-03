// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;
using MeshRF.Scripting;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// A routing ack we owe the sender of a unicast that set want_ack.
/// </summary>
/// <param name="Header">The packet being acked; supplies the sender, the id to
/// reference as request_id, and the hop counts the reply's hop limit derives from.</param>
/// <param name="ChannelName">Channel the packet decoded on, so the ack goes back
/// the same way it arrived.</param>
/// <param name="Pkc">The packet was public-key encrypted, so the ack must be too.</param>
/// <param name="TextMessage">The packet was a direct text message. Firmware
/// singles these out (<c>ReliableRouter::shouldSuccessAckWithWantAck</c>) and
/// acks them reliably, because this ack is what turns the sender's message from
/// pending into delivered.</param>
/// <param name="Duplicate">The packet is a retransmission of one we already
/// acked, meaning our first ack was lost. Firmware answers these at hop limit 0:
/// the repeat exists only to stop the immediate relayer retrying, so it must not
/// be flooded back across the mesh.</param>
/// <param name="HasBitfield">The packet carried Data.bitfield, which is what
/// makes its hop_start trustworthy when it reads zero. False for a packet we
/// could not decrypt — the field lives inside the ciphertext.</param>
/// <param name="ErrorReason">
/// <see cref="RoutingError.None"/> for a real acknowledgement, or the reason
/// this is a negative one. A NAK still answers the sender's want_ack, which is
/// the point: staying silent because we could not read the packet just makes it
/// retransmit and the mesh reflood.
/// </param>
public sealed record AckRequest(MeshHeader Header, string? ChannelName, bool Pkc,
                                bool TextMessage, bool Duplicate, bool HasBitfield,
                                uint ErrorReason = RoutingError.None);

/// <summary>
/// <see cref="IMeshRxHost"/> for the app: decodes traffic on any configured
/// channel (persisted via <see cref="ChannelStore"/> under %APPDATA%/config)
/// into per-channel message tabs, routes direct messages addressed to us into
/// per-peer conversation tabs, classifies reply/reaction text messages, and
/// keeps <see cref="NodeStore"/> updated from NodeInfo/Position/Telemetry.
/// Relaying and MQTT uplink are delegated out to the view model via
/// <see cref="RelayScheduler"/> and <see cref="UplinkHandler"/>.
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

    /// <summary>
    /// Marks the first conversation tab so the header strip can draw a rule
    /// between the channels and the DMs.
    /// </summary>
    /// <remarks>
    /// Driven off the collection rather than set at each of the half-dozen
    /// places that add, remove or reorder a tab, so it cannot be forgotten at
    /// one of them. Assignment is guarded on the current value because these
    /// are observable properties and rewriting an unchanged one would notify
    /// on every list change.
    /// </remarks>
    private void MarkTabGroups()
    {
        bool seen = false;
        foreach (var tab in Tabs)
        {
            bool starts = !seen && tab is ConversationTabViewModel;
            if (starts) seen = true;
            if (tab.StartsTabGroup != starts) tab.StartsTabGroup = starts;
        }
    }

    public ObservableCollection<NodeRecord> Nodes { get; } = new();
    public ObservableCollection<WaypointRecord> Waypoints { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Session node number: <c>UserNodeNum</c> from settings.json when
    /// set, otherwise an ephemeral random identity so a
    /// transmitted frame still carries a valid "from" and gets recognized
    /// as our own echo (isFromUs) instead of a new incoming packet.</summary>
    public uint MyNodeNum { get; private set; }

    /// <summary>Changes our node number mid-session (edited via the Node
    /// Identity dialog). Existing DM tabs/history keep their old peer keys —
    /// this only affects how future traffic is classified as ours.</summary>
    public void UpdateMyNodeNum(uint nodeNum)
    {
        MyNodeNum = nodeNum;
        // Taking a new identity changes which waypoint locks are ours, so the
        // rows have to be re-stamped and redrawn.
        foreach (var wp in Waypoints)
        {
            wp.ViewerNodeNum = nodeNum;
            wp.NotifyChanged();
        }
        // Taking over a number we had already heard from: it is us now, so drop
        // the peer row. MarkNodeDirty stops maintaining it from here on, which
        // would otherwise leave it in the grid frozen at its last-heard state.
        if (nodeNum == 0) return;
        for (int i = Nodes.Count - 1; i >= 0; i--)
            if (Nodes[i].NodeNum == nodeNum) Nodes.RemoveAt(i);
    }

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
    public Func<int, string>? FormatAltitude { get; set; }

    /// <summary>Raised after a history row is persisted, so an open history
    /// window can show it without being reopened. Carries the row rather than
    /// just the node number: the view models hold display points built from it,
    /// and re-reading the store for one new row would be a query per packet.</summary>
    public event Action<uint, NodeLocationHistoryRecord>? LocationHistoryRecorded;
    public event Action<uint, NodeTelemetryHistoryRecord>? TelemetryHistoryRecorded;

    byte[] IMeshRxHost.MyPrivateKeyBytes => MyPrivateKeyProvider?.Invoke() ?? Array.Empty<byte>();
    IReadOnlyList<ChannelConfig> IMeshRxHost.Channels => Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
    public float CurrentRssiDbfs { get; set; } = float.NegativeInfinity;
    float IMeshRxHost.CurrentRssiDbfs => CurrentRssiDbfs;

    /// <summary>Raised whenever a conversation tab opens or closes, so the
    /// owner can persist the updated open-tabs list.</summary>
    public event Action? OpenConversationsChanged;

    /// <summary>Raised when a text message addressed to us arrives from a node
    /// that isn't ignored, so the owner can play the alert tone. The flag says
    /// the text carried Meshtastic's alert bell, which gets its own tone.</summary>
    public Action<bool>? IncomingDirectMessage { get; set; }

    /// <summary>Raised when broadcast text lands on a channel tab that isn't
    /// muted, from a node that's neither ignored nor individually muted. The
    /// flag says the text carried an alert bell.</summary>
    public Action<bool>? IncomingChannelMessage { get; set; }

    /// <summary>Sounded for a geofence crossing. Separate from the message
    /// alerts so a crossing can be a short chime rather than the ringtone that
    /// announces someone talking to you.</summary>
    public Action? GeofenceCrossed { get; set; }

    /// <summary>Raised when a directed request we're the target of wants an
    /// auto-reply (NodeInfo/Position/Telemetry/Traceroute). The owner (which
    /// holds the transmit-capable MeshtasticCore) wires this up; left null
    /// means such requests are simply not answered.</summary>
    public Action<byte[]>? TransmitAutoReply { get; set; }

    /// <summary>How far a reply to a request addressed to us may travel, given
    /// the request's header and whether it carried Data.bitfield. The owner
    /// holds the configured hop limit and firmware's response rule, so it
    /// supplies the number; left null falls back to the Meshtastic default.</summary>
    public Func<MeshHeader, bool, byte>? ResponseHopLimitProvider { get; set; }

    /// <summary>Raised when a peer directs a request at us that we should
    /// answer (port, requester, channel the request arrived on, hops the answer
    /// may travel). The owner holds our identity and the transmitter, so it
    /// builds the reply; the hop limit comes from here because only the request
    /// says how far away the asker was.</summary>
    public Action<PortNum, uint, string?, byte>? AutoReplyRequested { get; set; }

    /// <summary>Raised on hearing a node we hold no NodeInfo for (node, channel
    /// it was heard on, hops it travelled). The owner decides whether to
    /// introduce us — see <see cref="PerhapsIntroduceOurselves"/>.</summary>
    public Action<uint, string?, byte>? UnknownNodeHeard { get; set; }

    /// <summary>Raised for a directed telemetry request, carrying which metric
    /// group was asked for so the reply matches rather than always answering
    /// with device metrics, and how far the answer may travel.</summary>
    public Action<uint, string?, TelemetryVariants, byte>? TelemetryReplyRequested { get; set; }

    /// <summary>Raised for a unicast addressed to us carrying want_ack, so the
    /// owner (which holds the transmitter) can send the routing ack. Failing to
    /// ack is what makes senders retransmit and the mesh reflood.
    ///
    /// Only raised for a frame we could decrypt. A packet we can't decode is
    /// still known to be addressed to us — firmware answers those with a
    /// NO_CHANNEL or PKI_UNKNOWN_PUBKEY nak — but MeshRF has no nak path yet.</summary>
    public Action<AckRequest>? AckRequested { get; set; }

    /// <summary>Raised for any ROUTING_APP reply addressed to us, carrying the
    /// packet id it answers, before it is matched against our outgoing
    /// messages. Lets the owner retire a reliable ack it is still retrying.</summary>
    public Action<uint>? RoutingReplyReceived { get; set; }

    /// <summary>Raised for every decoded packet so the owner can serialise it
    /// into the raw JSON feed.</summary>
    public Action<MeshHeader, MeshDecodeResult, long, float?, float?, byte, string>? DecodedPacketForFeed { get; set; }

    /// <summary>Raised when a node's stored public key is replaced by a
    /// different one. The router caches parsed sender keys for PKC decode, so
    /// without this it would keep decrypting against the key that is no longer
    /// on file. The owner holds the router, not this host.</summary>
    public Action<uint>? StoredPublicKeyChanged { get; set; }

    /// <summary>
    /// Raised for anything an automation script could be triggered by: a text
    /// message, a tapback, or a node heard for the first time.
    /// </summary>
    /// <remarks>
    /// Raised only for traffic that reached here through the decode path, which
    /// has already dropped our own transmissions — so a script can never be
    /// triggered by a message a script sent. The owner holds the engine and the
    /// transmitter; left null, nothing is automated.
    /// </remarks>
    public Action<ScriptEvent>? ScriptEventObserved { get; set; }

    /// <summary>Fills in the parts of a script event only the owner knows — our
    /// own name and battery. Null when no engine is attached.</summary>
    public Func<ScriptSelf>? ScriptSelfProvider { get; set; }

    /// <summary>Builds the flat snapshot the engine matches against. Everything
    /// a condition or placeholder could want is copied in here, so evaluation
    /// never reaches back into the stores.</summary>
    private ScriptEvent BuildScriptEvent(
        ScriptEventKind kind, MeshHeader header, MessageRecord record, MeshDecodeResult result,
        bool isDirect, byte hopsAway, string emoji = "")
    {
        var node = _nodeStore.Get(header.From);
        return new ScriptEvent
        {
            Kind = kind,
            Text = record.Text,
            FromNode = header.From,
            FromShort = node?.ShortName ?? string.Empty,
            // Falls back to the display name rather than the raw id, so a
            // {from.long} in a greeting reads as a name either way.
            FromLong = string.IsNullOrEmpty(node?.LongName) ? NodeDisplayName(header.From) : node!.LongName,
            FromLatitude = node?.Latitude,
            FromLongitude = node?.Longitude,
            Channel = result.ChannelName ?? string.Empty,
            // Only for a packet that actually named a channel: FindChannelByName
            // falls back to the first tab when given nothing, which would make
            // a direct message look like it arrived on the primary.
            IsPrimaryChannel = !string.IsNullOrEmpty(result.ChannelName) &&
                               FindChannelByName(result.ChannelName) is { Index: 0 },
            IsDirect = isDirect,
            SnrDb = record.SnrDb,
            RssiDbm = record.RssiDbfs,
            Hops = hopsAway,
            SenderIsFavorite = node?.Favorite == true,
            SenderHasKey = !string.IsNullOrEmpty(node?.PublicKey),
            PacketId = header.PacketId,
            Emoji = emoji,
            Self = ScriptSelfProvider?.Invoke() ?? ScriptSelf.Unknown,
            At = DateTimeOffset.Now,
        };
    }

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
        var record = TelemetryHistoryFactory.Build(nodeNum, timestamp, telemetry);
        long id = _nodeStore.AddTelemetryHistory(record);
        TelemetryHistoryRecorded?.Invoke(nodeNum, record with { Id = id });
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

        Tabs.CollectionChanged += (_, _) => MarkTabGroups();

        // Every waypoint that arrives learns who is looking at it, so the lock
        // column can tell our own locks from someone else's. Done from the
        // collection rather than at each of the several places that add one.
        Waypoints.CollectionChanged += (_, e) =>
        {
            foreach (var wp in e.NewItems?.OfType<WaypointRecord>() ?? [])
                wp.ViewerNodeNum = MyNodeNum;
        };

        LoadChannels();
        foreach (var wp in _waypointStore.All()) Waypoints.Add(wp);
        // Our own node lives in the database so chats can show our name, but we
        // don't list ourselves among the discovered peers. Excluded here rather
        // than in the node filter so Nodes.Count stays the peer count — the
        // header compares it against FilteredNodes.Count to decide whether a
        // filter is narrowing the list, and a permanently-hidden row would make
        // it read "n-1 of n" with no filter set.
        foreach (var n in _nodeStore.All())
            if (MyNodeNum == 0 || n.NodeNum != MyNodeNum)
                Nodes.Add(n);
        LoadMessageHistory(openConversationNodeNums);
    }

    /// <summary>Loads channel chat history, then reopens only the DM tabs that
    /// were left open last session (not every peer we have history with):
    /// <c>AppSettings.OpenConversations</c> is persisted, and only those are
    /// replayed.</summary>
    private void LoadMessageHistory(IReadOnlyList<uint> openConversationNodeNums)
    {
        // Reactions are stored as their own rows, so replay in two passes per
        // tab: real messages first, then attach each reaction to its target.
        // Otherwise a restart turns every reaction into a stray message row.
        var deferred = new Dictionary<ChannelTabViewModel, List<MessageRecord>>();
        foreach (var m in _messageStore.TextHistory())
        {
            // A geofence note is addressed to us rather than to the broadcast
            // address, so it needs letting into the room on its own terms.
            bool channelNote = IsChannelNote(m);
            if (!channelNote && m.ToNode != 0xFFFFFFFFu) continue; // DMs are rebuilt separately below.
            var tab = ResolveChannelTab(m.Channel);
            if (tab is null) continue;

            if (IsReactionRecord(m))
            {
                if (!deferred.TryGetValue(tab, out var list))
                    deferred[tab] = list = new List<MessageRecord>();
                list.Add(m);
                continue;
            }

            var replayed = BuildHistoryMessage(m, tab.Messages);
            if (channelNote) replayed.FromId = GeofenceNoteLabel;
            tab.Messages.Add(replayed);
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
    /// happened.</summary>
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

    /// <summary>Sender label on a geofence crossing, live and replayed alike.
    /// The row carries the channel it was posted into, not the fence, so a
    /// waypoint's own name could not survive a restart — and it is already in
    /// the alert's text.</summary>
    private const string GeofenceNoteLabel = "Geofence";

    /// <summary>True for a note this app wrote into a channel rather than into
    /// a conversation — today, a geofence crossing. Conversation notes carry
    /// the peer or us in from_node; these carry nobody.</summary>
    private static bool IsChannelNote(MessageRecord m) =>
        m.PortNum == MessageStore.ConversationNotePort
        && m.FromNode == 0
        && !string.IsNullOrWhiteSpace(m.Channel);

    private void ApplyHistoryReactions(ObservableCollection<ChannelMessage> messages, List<MessageRecord> reactions)
    {
        foreach (var r in reactions)
        {
            if (!TryApplyReaction(messages, r.ReplyId, r.Text, r.Emoji, r.FromNode))
                InsertChronologically(messages, BuildStandaloneReactionMessage(r));
        }
    }

    /// <summary>Persist a message we transmitted, so it survives a
    /// restart.</summary>
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

    private ChannelMessage ToChannelMessage(MessageRecord m)
    {
        bool outgoing = MyNodeNum != 0 && m.FromNode == MyNodeNum;
        return new ChannelMessage
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(m.RxEpoch).LocalDateTime,
            FromId = m.FromNode == 0 ? "note" : NodeDisplayName(m.FromNode),
            SenderNodeNum = m.FromNode,
            Text = m.Text,
            RssiDbm = m.RssiDbfs,
            SnrDb = m.SnrDb,
            PacketId = m.PacketId,
            IsOutgoing = outgoing,
            Delivery = RestoredDelivery(m, outgoing),
        };
    }

    /// <summary>
    /// Delivery state to restore onto a replayed row. Without this a ✓ or ✗
    /// lived only in memory and vanished on restart, even though the store had
    /// it all along.
    ///
    /// Unlike MeshRF.App this does not exclude broadcasts: a channel message
    /// now earns its mark by being heard relayed, so its state is worth
    /// restoring too. Values outside the enum (rows written by a future
    /// version, or a hand-edited database) degrade to None rather than
    /// rendering a garbage glyph.
    /// </summary>
    private static MessageDelivery RestoredDelivery(MessageRecord m, bool outgoing) =>
        outgoing && Enum.IsDefined(typeof(MessageDelivery), m.Delivery)
            ? (MessageDelivery)m.Delivery
            : MessageDelivery.None;

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
            Tabs.Add(NewChannelTab(c));
    }

    /// <summary>The channel a keyless secondary borrows from, per firmware
    /// <c>getKey()</c>. Looked up live so it follows role edits.</summary>
    private ChannelConfig? PrimaryChannelConfig() =>
        Tabs.OfType<ChannelTabViewModel>()
            .FirstOrDefault(t => t.Config.Role == ChannelRole.Primary)?.Config;

    /// <summary>Wires a config to its siblings before it reaches a tab, so key
    /// and hash resolution work the same on every path that later hands the
    /// config to the decoder or encoder.</summary>
    private ChannelTabViewModel NewChannelTab(ChannelConfig config)
    {
        config.PrimaryProvider = PrimaryChannelConfig;
        return new ChannelTabViewModel(config);
    }

    /// <summary>Adds and persists a new secondary channel with an
    /// auto-generated "Channel N" name and a fresh random PSK. Backs the "+"
    /// button: no name prompt, rename via the channel's Settings dialog
    /// afterward.</summary>
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
        var tab = NewChannelTab(config);
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
    /// position precision/MQTT, made via the Settings dialog) and refreshes its
    /// tab header.</summary>
    public void SaveChannelConfig(ChannelTabViewModel channel)
    {
        // Firmware invariant: exactly one channel may be Primary. Promoting one
        // has to demote whichever held it, or the app ends up with two — and
        // "the primary channel" is what a blank channel name, the map report's
        // has_default_channel flag and the default send target all resolve
        // through.
        if (channel.Config.Role == ChannelRole.Primary)
        {
            foreach (var other in Tabs.OfType<ChannelTabViewModel>())
            {
                if (other.Config.Index == channel.Config.Index) continue;
                if (other.Config.Role != ChannelRole.Primary) continue;
                other.Config.Role = ChannelRole.Secondary;
                _channelStore.Upsert(other.Config);
                other.NotifyConfigChanged();
            }
        }

        _channelStore.Upsert(channel.Config);
        channel.NotifyConfigChanged();
    }

    /// <summary>
    /// Keeps an auto-named primary channel in step with the modem preset.
    ///
    /// A primary on the default key whose name is blank — or is still just a
    /// preset name from a previous sync — takes the current preset's name, which
    /// is what firmware shows for an unnamed default channel. A channel the user
    /// actually named is left alone, as is one with its own PSK.
    /// </summary>
    /// <returns>True if a rename happened, so the caller can persist.</returns>
    public bool SyncPrimaryChannelName(LoraPreset preset)
    {
        var primary = Tabs.OfType<ChannelTabViewModel>()
            .FirstOrDefault(t => t.Config.Role == ChannelRole.Primary);
        if (primary is null) return false;

        var cfg = primary.Config;
        if (!cfg.UsesDefaultKey) return false;

        var presetName = preset.ToString();
        bool autoNamed = string.IsNullOrEmpty(cfg.Name) ||
                         Enum.GetNames<LoraPreset>().Contains(cfg.Name);
        if (!autoNamed || cfg.Name == presetName) return false;

        cfg.Name = presetName;
        _channelStore.Upsert(cfg);
        primary.NotifyConfigChanged();
        return true;
    }

    /// <summary>Removes one channel row by index. Used by drag-reordering,
    /// which clears the affected indices before writing the new mapping.</summary>
    public void DeleteChannelIndex(int index) => _channelStore.Delete(index);

    /// <summary>Writes a channel row straight through, without the
    /// primary-demotion logic in <see cref="SaveChannelConfig"/> — reordering
    /// never changes a role.</summary>
    public void UpsertChannelConfig(ChannelConfig config) => _channelStore.Upsert(config);

    public ChannelConfig? FindChannelByName(string? name)
    {
        // Disabled channels are skipped: callers use this to pick a channel to
        // send on, and a disabled one has no key or hash to send with.
        var candidates = Tabs.OfType<ChannelTabViewModel>().Where(t => !t.Config.IsDisabled);
        if (string.IsNullOrEmpty(name)) return candidates.FirstOrDefault()?.Config;
        return candidates
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
                                                 _nodeStore, () => FormatTemperature, () => FormatPressure,
                                                 () => FormatAltitude)
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
        // Stamped here, at the single funnel, so every line gets one. Uses the
        // unit-system-aware convention.
        LogLines.Add($"[{UiFormats.Stamp(DateTime.Now)}] {message}");
        while (LogLines.Count > 500) LogLines.RemoveAt(0);
    }

    public void MarkNodeDirty(uint nodeNum)
    {
        var rec = _nodeStore.Get(nodeNum);
        if (rec is null) return;
        // UpsertSelf calls this for our own node, which the peer list excludes
        // (see the constructor) — without this guard the "not found, append it"
        // branch below would put us straight back in. Only the list is skipped;
        // any conversation bound to this node still refreshes.
        if (MyNodeNum == 0 || nodeNum != MyNodeNum)
        {
            var existingIndex = -1;
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].NodeNum == nodeNum) { existingIndex = i; break; }
            }
            if (existingIndex >= 0) Nodes[existingIndex] = rec;
            else Nodes.Add(rec);
        }

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

    /// <summary>
    /// Both halves have to agree before we call a frame our own echo: relay_node
    /// says the last station to transmit it was us, and the scheduler confirms we
    /// really did put this packet on the air. relay_node alone is only the low
    /// byte of a node number, so on its own it would silently swallow 1 in 256 of
    /// other stations' rebroadcasts.
    /// </summary>
    public bool WasRelayedByUs(MeshHeader header) =>
        MyNodeNum != 0 &&
        header.RelayNode == (byte)(MyNodeNum & 0xFF) &&
        RelayScheduler?.WasRelayedByUs(header.From, header.PacketId) == true;

    public void HandleDuplicateForRelay(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb)
    {
        if (RelayScheduler is null || RelayContextProvider?.Invoke() is not { } ctx) return;
        // True means this copy arrived with more hops left than the one we had
        // queued, so it's worth relaying instead.
        if (RelayScheduler.HandleDuplicate(ctx, header, snrDb ?? 0f))
            RelayIfEligible(frame, header, result, snrDb);
    }

    /// <summary>Firmware's ignore_mqtt: while set, the relay never puts
    /// MQTT-derived traffic back onto RF. Owned by the view model's
    /// Ignore MQTT toggle.</summary>
    public bool IgnoreMqttNodes { get; set; }

    public void RelayIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result, float? snrDb)
    {
        if (RelayScheduler is null || RelayContextProvider?.Invoke() is not { } ctx) return;
        // Suppressed senders fold into the same gate as user-ignored nodes:
        // with Ignore MQTT on, a packet that itself arrived via downlink is
        // skipped, and so is anything from a node this store has marked as
        // heard via MQTT — by default both DO relay, which is precisely what
        // makes a downlink gateway a gateway.
        bool senderSuppressed = IsNodeIgnored(header.From) ||
            (IgnoreMqttNodes && (header.ViaMqtt || _nodeStore.Get(header.From)?.SeenViaMqtt == true));
        if (!RelayPolicy.ShouldRelay(ctx, header, result, senderSuppressed)) return;

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

    // -- ACK / NAK tracking ---------------------------------------------------

    /// <summary>Outgoing messages waiting to be confirmed, keyed by packet id.</summary>
    private readonly Dictionary<uint, PendingAck> _pendingAcks = new();

    /// <summary>How long to wait before giving up on a message. Generous
    /// because a DM's ACK has to make the return trip across the mesh.</summary>
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);

    /// <param name="Broadcast">
    /// Channel messages are confirmed differently from DMs. A DM sets want_ack
    /// and gets an explicit ROUTING reply from the recipient, so it can be
    /// Delivered *or* Failed on a NAK. A broadcast is never acknowledged by
    /// anyone — the only evidence it went anywhere is hearing a neighbour
    /// rebroadcast it, which says "the mesh picked it up", not "someone read
    /// it". So a channel message only ever goes Delivered (relay heard) or
    /// Failed (nothing heard in time); there is no NAK for it.
    /// </param>
    private sealed record PendingAck(ChannelMessage Message, DateTime SentUtc, bool Broadcast);

    /// <summary>Register a message we just transmitted so an ACK, a heard
    /// rebroadcast, or the timeout can settle its delivery state.</summary>
    public void TrackPendingAck(ChannelMessage message, bool broadcast)
    {
        if (message.PacketId == 0) return;
        _pendingAcks[message.PacketId] = new PendingAck(message, DateTime.UtcNow, broadcast);
    }

    /// <summary>
    /// Our own frame heard back off the air — Meshtastic's implicit ACK.
    ///
    /// What it proves depends on who the message was for. A broadcast is never
    /// acknowledged by anyone, so a neighbour relaying it is the only delivery
    /// confirmation it can ever get, and it settles as delivered. A DM has a
    /// real recipient who will answer for it, so the relay is only the first of
    /// two stages: the mesh carried it. It stays pending, and the recipient's
    /// ACK is what finishes the trip.
    ///
    /// hop_limit &lt; hop_start is what distinguishes a relay from simply hearing
    /// our own transmitter (a receive-only SDR alongside a separate transmit
    /// SDR hears every frame we send, undecremented). Firmware older than 2.3
    /// leaves hop_start at 0, so the comparison is only meaningful above zero.
    /// </summary>
    public void OnOwnPacketHeard(MeshHeader header, MeshDecodeResult? ownDecode)
    {
        // Decode our own frame to surface the ok_to_mqtt bitfield, so the user
        // can confirm the flag is actually present on the wire.
        string mqttNote = ownDecode is not null
            ? $", ok_to_mqtt={(ownDecode.OkToMqtt ? "yes" : "no")}"
            : string.Empty;
        bool relayed = header.HopStart > 0 && header.HopLimit < header.HopStart;
        Log($"  tx confirmed (heard own packet id {header.PacketId:x8}{mqttNote}"
            + (relayed ? ", relayed" : string.Empty) + ")");

        if (!relayed) return;
        if (!_pendingAcks.TryGetValue(header.PacketId, out var pending)) return;

        if (pending.Broadcast)
        {
            _pendingAcks.Remove(header.PacketId);
            SettleDelivery(pending.Message, MessageDelivery.Delivered,
                $"heard {RelayDisplayName(header.RelayNode)} relay our broadcast");
            Log($"  relayed by {RelayDisplayName(header.RelayNode)} — channel message {header.PacketId:x8} reached the mesh");
            return;
        }

        // Only ever an upgrade from Sent. Several neighbours relay the same DM,
        // and the recipient's ACK can beat the last of them back to us — without
        // this guard a late relay would demote a delivered message.
        if (pending.Message.Delivery != MessageDelivery.Sent) return;

        SettleDelivery(pending.Message, MessageDelivery.DeliveredToMesh,
            $"heard {RelayDisplayName(header.RelayNode)} relay our direct message");
        Log($"  relayed by {RelayDisplayName(header.RelayNode)} — direct message {header.PacketId:x8} reached the mesh, "
            + "waiting on the recipient");
    }

    /// <summary>
    /// Names the station that put a relayed frame back on the air. A rebroadcast
    /// keeps the original sender in <c>from</c> — us, on our own echo — so the
    /// relayer is only identifiable by relay_node, and that is just the low byte
    /// of its node number. A name is claimed only when exactly one known node
    /// ends in that byte; otherwise the byte itself is all we can honestly say.
    /// </summary>
    private string RelayDisplayName(byte relayByte)
    {
        uint match = 0;
        int hits = 0;
        foreach (var node in _nodeStore.All())
        {
            // We never relay our own transmissions, so a match on our own node
            // is a neighbour that happens to share our low byte, not us.
            if (node.NodeNum == MyNodeNum) continue;
            if ((node.NodeNum & 0xFF) != relayByte) continue;
            if (++hits > 1) break;
            match = node.NodeNum;
        }
        return hits == 1 ? NodeDisplayName(match) : $"relay 0x{relayByte:X2}";
    }

    /// <summary>
    /// ROUTING_APP addressed to us: match request_id against a DM we sent and
    /// mark it delivered (ACK) or failed (NAK). Mirrors firmware's
    /// <c>Router::handleReceived</c> ack handling.
    /// </summary>
    private void HandleRouting(MeshHeader header, MeshDecodeResult result)
    {
        if (MyNodeNum == 0 || header.To != MyNodeNum || result.RequestId == 0) return;

        // Announce it before the _pendingAcks lookup: an ack we sent reliably is
        // not a message bubble, so it will never be in that dictionary, and its
        // confirmation would otherwise be dropped here.
        RoutingReplyReceived?.Invoke(result.RequestId);

        if (!_pendingAcks.TryGetValue(result.RequestId, out var pending)) return;

        // A broadcast has no recipient to answer for it; anything claiming to
        // route-ack one is not evidence about our message.
        if (pending.Broadcast) return;

        _pendingAcks.Remove(result.RequestId);
        bool ack = result.RoutingError == 0;
        SettleDelivery(pending.Message, ack ? MessageDelivery.Delivered : MessageDelivery.Failed,
            ack
                ? $"ACK from {NodeDisplayName(header.From)}"
                : $"NAK from {NodeDisplayName(header.From)}, routing error {result.RoutingError}");
        Log(ack
            ? $"  ACK from {NodeDisplayName(header.From)} for id {result.RequestId:x8}"
            : $"  NAK (reason={result.RoutingError}) from {NodeDisplayName(header.From)} for id {result.RequestId:x8}");
    }

    /// <summary>Give up on anything that has waited past the timeout. Driven by
    /// the view model's poll tick.</summary>
    public void SweepPendingAcks()
    {
        if (_pendingAcks.Count == 0) return;
        var now = DateTime.UtcNow;
        List<uint>? expired = null;
        foreach (var kv in _pendingAcks)
        {
            if (now - kv.Value.SentUtc < AckTimeout) continue;
            (expired ??= []).Add(kv.Key);
        }
        if (expired is null) return;

        foreach (var id in expired)
        {
            if (!_pendingAcks.Remove(id, out var pending)) continue;
            // Only a message with nothing at all behind it fails. A DM already
            // at DeliveredToMesh was heard being relayed, so the mesh
            // demonstrably carried it; the silence afterwards is the
            // recipient's alone, and a red cross would deny the one thing we do
            // know. It keeps the grey check it earned.
            if (pending.Message.Delivery is not MessageDelivery.Sent) continue;
            SettleDelivery(pending.Message, MessageDelivery.Failed,
                $"no ack within {AckTimeout.TotalSeconds:0}s and nothing heard relaying it");
        }
    }

    /// <summary>Apply a delivery state to the live bubble and persist it, so the
    /// mark survives a restart.</summary>
    /// <param name="reason">
    /// Why the state changed, for the log. Every transition is logged, because
    /// a mark that changes on its own is otherwise impossible to account for
    /// after the fact: the timeout in particular used to turn a check into a
    /// cross while saying nothing at all about having done so.
    /// </param>
    private void SettleDelivery(ChannelMessage message, MessageDelivery delivery, string reason)
    {
        var previous = message.Delivery;
        message.Delivery = delivery;
        if (previous != delivery)
        {
            Log($"  delivery {message.PacketId:x8}: {DeliveryName(previous)} -> {DeliveryName(delivery)} ({reason})");
        }
        if (message.PacketId == 0 || MyNodeNum == 0) return;
        try { _messageStore.UpdateDelivery(message.PacketId, MyNodeNum, (int)delivery); }
        catch (Exception ex) { Log($"delivery update failed: {ex.Message}"); }
    }

    /// <summary>What the log calls each state. The stored enum names would do,
    /// but these match the marks the user actually sees.</summary>
    private static string DeliveryName(MessageDelivery delivery) => delivery switch
    {
        MessageDelivery.None            => "none",
        MessageDelivery.Sent            => "sent",
        MessageDelivery.DeliveredToMesh => "reached mesh (grey check)",
        MessageDelivery.Delivered       => "delivered (green check)",
        MessageDelivery.Failed          => "failed (cross)",
        _ => delivery.ToString(),
    };

    public void RecordSighting(uint fromNode, long rxEpoch, float? rssiDbm, float? snrDb, byte hopsAway, bool viaMqtt)
    {
        // Checked before the upsert, since the upsert is what creates the row:
        // no record yet means this node number has never been heard on this
        // install. Nothing else in the app distinguishes a first sighting, and
        // a node that was forgotten and heard again counts as new — which is
        // what a user who forgot it would expect.
        bool firstSighting = IsFirstSighting(fromNode);

        _nodeStore.RecordSighting(fromNode, rssiDbm: rssiDbm, snrDb: snrDb, hopsAway: hopsAway, seenViaMqtt: viaMqtt);
        MarkNodeDirty(fromNode);

        if (firstSighting) RaiseNewNode(fromNode, snrDb, rssiDbm, hopsAway);
    }

    /// <summary>True when this node number has no record yet, so the packet
    /// being handled is the first time it has ever been heard. Must be called
    /// before the upsert that creates the row.</summary>
    private bool IsFirstSighting(uint nodeNum) =>
        ScriptEventObserved is not null && nodeNum != 0 && nodeNum != MyNodeNum && _nodeStore.Get(nodeNum) is null;

    /// <summary>
    /// Raises the new_node trigger. Called after the node's row exists, so a
    /// script reading the sender's name or key sees whatever this packet
    /// carried.
    /// </summary>
    /// <remarks>
    /// When the first packet is not a NodeInfo the name has not arrived yet and
    /// {from.long} falls back to the id, which is why a greeting script wants a
    /// delay: in front of it. The help window says so.
    /// </remarks>
    private void RaiseNewNode(uint nodeNum, float? snrDb, float? rssiDbm, byte hopsAway)
    {
        var node = _nodeStore.Get(nodeNum);
        ScriptEventObserved?.Invoke(new ScriptEvent
        {
            Kind = ScriptEventKind.NewNode,
            FromNode = nodeNum,
            FromShort = node?.ShortName ?? string.Empty,
            FromLong = string.IsNullOrEmpty(node?.LongName) ? NodeDisplayName(nodeNum) : node!.LongName,
            SnrDb = snrDb,
            RssiDbm = rssiDbm,
            Hops = hopsAway,
            SenderIsFavorite = node?.Favorite == true,
            SenderHasKey = !string.IsNullOrEmpty(node?.PublicKey),
            Self = ScriptSelfProvider?.Invoke() ?? ScriptSelf.Unknown,
            At = DateTimeOffset.Now,
        });
    }

    public void OnMessageDecoded(byte[] frame, MeshHeader header, MessageRecord record, MeshDecodeResult result,
        long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway)
    {
        // Log every successful decode.
        // Without this the log only ever shows MeshRxRouter's "(dup)" and
        // "rx undecoded" lines, making normal flood retransmissions look like
        // every packet was being rejected as a duplicate.
        var summary = BuildDecodedPortSummary(header, result, NodeDisplayName(header.From));
        Log(summary);
        DecodedPacketForFeed?.Invoke(header, result, rxEpoch, snrDb, packetRssiDbm, hopsAway, summary);

        // First sight of this packet, so this is the full-strength ack. A
        // retransmission of it lands in OnDuplicateDecoded instead and gets the
        // cheaper 0-hop repeat.
        if (header.WantAck) AckRequested?.Invoke(BuildAckRequest(header, result, duplicate: false));

        PerhapsIntroduceOurselves(header, result, hopsAway);

        // The sender's public key as it stood before this packet. Both the
        // NodeInfo case (which has to notice a substituted key rather than
        // absorb it) and the signature check after the switch need the key we
        // already trusted, not the one this packet may be about to store.
        string? knownKeyHex = NeedsStoredPublicKey(header, result) ? _nodeStore.Get(header.From)?.PublicKey : null;

        switch (result.Port)
        {
            case PortNum.TextMessage:
                HandleTextMessage(header, record, result, hopsAway);
                break;

            case PortNum.NodeInfo when result.User is not null:
                // An empty NodeInfo payload carrying want_response is a pure
                // *request*, not an advertisement. Answering it is how other
                // nodes learn our name; upserting it would overwrite the
                // sender's record with blanks.
                if (result.AppPayload.Length == 0)
                {
                    if (IsDirectedRequest(header, result))
                        AutoReplyRequested?.Invoke(PortNum.NodeInfo, header.From, result.ChannelName,
                                                   ReplyHopLimit(header, result));
                    break;
                }
                // The router skips RecordSighting for a NodeInfo record (its own
                // upsert folds those fields in), so this is the only place a
                // node whose very first packet is a NodeInfo can be noticed as
                // new — and it is a common way to first hear one.
                bool firstNodeInfo = IsFirstSighting(header.From);
                string newKeyHex = result.User.PublicKey.Length == 32
                    ? Convert.ToHexString(result.User.PublicKey)
                    : string.Empty;
                bool keyIsNew = newKeyHex.Length > 0
                    && !string.Equals(knownKeyHex, newKeyHex, StringComparison.OrdinalIgnoreCase);
                // A key that contradicts one we already hold is a substitution,
                // not an update. The old key is kept and the node flagged, which
                // is what turns the key badge red — silently adopting the new
                // key is exactly how somebody would take over a conversation.
                // "Request new keys" forgets the stored key, after which the
                // next one heard is accepted normally.
                bool keyMismatch = keyIsNew && !string.IsNullOrEmpty(knownKeyHex);
                bool keyAccepted = keyIsNew && !keyMismatch;
                _nodeStore.Upsert(new NodeRecord
                {
                    NodeNum = header.From,
                    UserId = string.IsNullOrEmpty(result.User.Id) ? header.FromId : result.User.Id,
                    LongName = result.User.LongName,
                    ShortName = result.User.ShortName,
                    Role = string.IsNullOrEmpty(result.User.Role) ? "Client" : result.User.Role,
                    // Empty preserves what is on file, so a later NodeInfo sent
                    // from a reloaded NodeDB (which zero-fills the MAC) does not
                    // erase the one the node advertised when it booted.
                    MacAddress = result.User.MacAddress,
                    // Empty preserves whatever is on file (the upsert NULLIFs
                    // it), which is how a mismatch keeps the old key.
                    PublicKey = keyMismatch ? string.Empty : newKeyHex,
                    // Only a NodeInfo that carried a key has anything to say
                    // about the flag; null leaves it as it stands.
                    KeyMismatch = newKeyHex.Length > 0 ? keyMismatch : (bool?)null,
                    IsUnmessagable = result.User.IsUnmessagable,
                    // is_licensed is a plain proto3 bool, so an unlicensed node
                    // simply omits it. Resolving absent to false here is what
                    // lets a node that leaves ham mode stop looking licensed —
                    // a null would COALESCE the stale true back in.
                    IsLicensed = result.User.IsLicensed ?? false,
                    LastHeardEpoch = rxEpoch,
                    SeenViaMqtt = header.ViaMqtt,
                    RssiDbm = packetRssiDbm,
                    SnrDb = snrDb,
                    HopsAway = hopsAway,
                });
                if (keyMismatch)
                    Log($"  {header.FromId}: KEY MISMATCH — the public key changed; keeping the one on file. " +
                        "Right-click the node → Request new keys to accept the new one.");
                if (keyAccepted) StoredPublicKeyChanged?.Invoke(header.From);
                MarkNodeDirty(header.From);
                // Raised after the upsert, so a greeting script sees the name
                // and key this packet carried rather than a bare node id.
                if (firstNodeInfo) RaiseNewNode(header.From, snrDb, packetRssiDbm, hopsAway);
                // An advertisement may still ask us to reply with ours.
                if (IsDirectedRequest(header, result))
                    AutoReplyRequested?.Invoke(PortNum.NodeInfo, header.From, result.ChannelName,
                                               ReplyHopLimit(header, result));
                break;

            case PortNum.Position when result.Position is not null:
                // A Position that omits the coordinates carries no location. With
                // want_response it is the request form, the same convention
                // NodeInfo uses; either way there is nothing here to store, and
                // writing the missing fields as 0,0 would erase what we know of
                // the sender — including when we overhear a request aimed at
                // somebody else.
                if (result.Position is not { Latitude: double lat, Longitude: double lon })
                {
                    if (IsDirectedRequest(header, result))
                        AutoReplyRequested?.Invoke(PortNum.Position, header.From, result.ChannelName,
                                                   ReplyHopLimit(header, result));
                    break;
                }
                // Record a history point only when the coordinates actually
                // moved: position packets repeat unchanged, and storing every
                // one would bury real movement in duplicates.
                var previous = _nodeStore.Get(header.From);
                bool positionChanged = previous?.Latitude != lat || previous?.Longitude != lon;

                _nodeStore.Upsert(new NodeRecord
                {
                    NodeNum = header.From,
                    Latitude = lat,
                    Longitude = lon,
                    AltitudeM = result.Position.AltitudeM,
                });
                if (positionChanged)
                {
                    var when = DateTimeOffset.FromUnixTimeSeconds(rxEpoch).UtcDateTime;
                    long id = _nodeStore.AddLocationHistory(
                        header.From, when, lat, lon, result.Position.AltitudeM);
                    LocationHistoryRecorded?.Invoke(header.From, new NodeLocationHistoryRecord(
                        id, header.From, when, lat, lon, result.Position.AltitudeM));
                }
                EvaluateGeofenceCrossing(header.From, lat, lon, snrDb, packetRssiDbm, hopsAway);
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
                                                    result.Telemetry.PresentVariants,
                                                    ReplyHopLimit(header, result));
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
                    TelemetryReplyRequested?.Invoke(header.From, result.ChannelName, t.PresentVariants,
                                                    ReplyHopLimit(header, result));
                break;

            case PortNum.NodeStatus when result.StatusMessage is not null:
                // The router has already recorded the sighting, so the row
                // exists for this to update.
                _nodeStore.SetNodeStatus(header.From, result.StatusMessage.Status);
                MarkNodeDirty(header.From);
                break;

            case PortNum.Traceroute:
                HandleTraceroute(header, result);
                break;

            case PortNum.Routing:
                HandleRouting(header, result);
                break;
        }

        TryVerifyXeddsaBroadcast(header, result, knownKeyHex);
    }

    /// <summary>A broadcast carrying a 64-byte <c>xeddsa_signature</c>
    /// (<c>Data</c> field 10) — the only shape worth verifying.</summary>
    private static bool IsSignedBroadcast(MeshHeader header, MeshDecodeResult result)
        => header.IsBroadcast && result.DataField10.Length == MeshCrypto.XeddsaSignatureSize;

    /// <summary>Whether handling this packet depends on the key already on file
    /// for the sender: a NodeInfo advertisement (key substitution) or a signed
    /// broadcast (signature verification).</summary>
    private static bool NeedsStoredPublicKey(MeshHeader header, MeshDecodeResult result)
        => IsSignedBroadcast(header, result)
           || (result.Port == PortNum.NodeInfo && result.User is not null && result.AppPayload.Length > 0);

    /// <summary>
    /// Verifies a signed broadcast against the sender's X25519 public key and,
    /// on success, records the node as a verified signer — the shield column,
    /// and the mirror of firmware's per-node <c>HAS_XEDDSA_SIGNED</c> bit.
    ///
    /// For a NODEINFO_APP broadcast the key carried in that same packet is
    /// preferred over the one on file: that is firmware's first-contact
    /// bootstrap, where trust comes from a single self-consistent signed
    /// NodeInfo with no prior key exchange. A carried key that contradicts one
    /// we already stored is not used — a substituted key never vouches for
    /// itself — leaving the stored key to verify against, which it won't.
    /// </summary>
    /// <param name="knownKeyHex">The sender's stored public key as it stood
    /// before this packet was applied to the node store.</param>
    private void TryVerifyXeddsaBroadcast(MeshHeader header, MeshDecodeResult result, string? knownKeyHex)
    {
        if (!IsSignedBroadcast(header, result)) return;

        byte[]? senderCurvePublicKey = null;
        if (result.Port == PortNum.NodeInfo && result.User is { PublicKey.Length: 32 } user)
        {
            bool contradictsStored = !string.IsNullOrEmpty(knownKeyHex)
                && !string.Equals(knownKeyHex, Convert.ToHexString(user.PublicKey), StringComparison.OrdinalIgnoreCase);
            if (!contradictsStored) senderCurvePublicKey = user.PublicKey;
        }
        senderCurvePublicKey ??= TryParseHex(knownKeyHex);
        if (senderCurvePublicKey.Length != 32) return;

        if (!MeshCrypto.XeddsaVerify(header.From, header.PacketId, (uint)result.Port,
                                     result.AppPayload, result.DataField10, senderCurvePublicKey))
            return;

        if (_nodeStore.Get(header.From)?.HasXeddsaSigned != true)
            Log($"  {header.FromId}: XEdDSA signature verified — marking as a verified signer.");

        _nodeStore.SetXeddsaSigned(header.From, true);
        MarkNodeDirty(header.From);
    }

    private static byte[] TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        try { return Convert.FromHexString(hex.Trim()); }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// The sender is repeating a packet we already handled. All the business
    /// logic already ran, but if it wanted an ack, the repeat says our ack never
    /// got there — so ack it again. Without this a single lost ack strands the
    /// sender: it retries three times, we drop every retry as a duplicate, and
    /// its message settles as failed even though we read it.
    /// </summary>
    /// <summary>A decode the message store could not take. The packet is left
    /// alone on air, but it reached us and was readable, so it is reported like
    /// any other decode — with the store failure named, since the line would
    /// otherwise look identical to a packet that was filed normally.</summary>
    /// <summary>
    /// Firmware <c>MeshService::handleFromRadio</c>: hearing a node we hold no
    /// NodeInfo for, send it ours and ask for its own. This is how a mesh fills
    /// in names without anyone pressing anything — a node that only listens
    /// stays anonymous to every peer that came up after it.
    /// </summary>
    /// <remarks>
    /// A telemetry reply is exempt: it is already the answer to a request of
    /// ours, and firmware explicitly declines to chase it with a NodeInfo.
    /// Whether we actually transmit is the owner's call — the role and airtime
    /// gates live with the transmitter.
    /// </remarks>
    private void PerhapsIntroduceOurselves(MeshHeader header, MeshDecodeResult result, byte hopsAway)
    {
        if (UnknownNodeHeard is null) return;
        if (header.From == 0 || header.From == MyNodeNum) return;
        if (result.Port == PortNum.Telemetry && result.RequestId != 0) return;

        var node = _nodeStore.Get(header.From);
        // "Has user" is a name, not a row: RecordSighting creates a record for
        // anything we hear, so a bare row means we still know nothing about it.
        if (!string.IsNullOrEmpty(node?.LongName) || !string.IsNullOrEmpty(node?.ShortName)) return;

        UnknownNodeHeard.Invoke(header.From, result.ChannelName, hopsAway);
    }

    public void OnDecodeNotStored(MeshHeader header, MeshDecodeResult result,
                                  long rxEpoch, float? snrDb, float? packetRssiDbm, byte hopsAway)
    {
        var summary = BuildDecodedPortSummary(header, result, NodeDisplayName(header.From));
        Log($"{summary} (not stored)");
        DecodedPacketForFeed?.Invoke(header, result, rxEpoch, snrDb, packetRssiDbm, hopsAway, summary);
    }

    public void OnDuplicateDecoded(MeshHeader header, MeshDecodeResult result)
    {
        if (!header.WantAck) return;

        // Only a copy that arrived straight from the original sender is a
        // retransmission worth answering: it means our first ack never got
        // there. A copy that reached us through a repeater is the mesh
        // flooding one transmission, and acking each arrival adds our own
        // traffic to a storm the sender never asked for.
        //
        // Firmware draws exactly this line in NextHopRouter::shouldFilterReceived:
        //   bool isRepeated = getHopsAway(*p) == 0;
        // and does nothing but perhapsCancelDupe() otherwise. HopsUsed is the
        // port of getHopsAway, Unknown included — firmware defaults that to -1,
        // which fails the same == 0 test, so an undeterminable hop count also
        // means no ack.
        if (ReplyHops.HopsUsed(header, result.HasDataBitfield) != 0) return;

        AckRequested?.Invoke(BuildAckRequest(header, result, duplicate: true));
    }

    private static AckRequest BuildAckRequest(MeshHeader header, MeshDecodeResult result, bool duplicate)
        => new(header,
               result.ChannelName,
               string.Equals(result.ChannelName, "PKC", StringComparison.Ordinal),
               result.Port is PortNum.TextMessage or PortNum.TextMessageCompressed,
               duplicate,
               result.HasDataBitfield);

    /// <summary>
    /// A packet addressed to us that no key we hold could open. The addressing
    /// is in the plaintext header, so we know it was meant for us and we know it
    /// wanted an acknowledgement — we just cannot read it. Firmware answers
    /// anyway, and so must we: silence is what makes the sender retransmit and
    /// every repeater reflood, which is the exact cost the ack path exists to
    /// avoid. The reply is a NAK naming why, which also lets the sender's client
    /// react (a PKI_UNKNOWN_PUBKEY is answered with their NodeInfo).
    /// </summary>
    public void OnUndecodedPacket(MeshHeader header)
    {
        if (!header.WantAck) return;
        if (MyNodeNum == 0 || header.IsBroadcast || header.To != MyNodeNum) return;

        // Channel hash 0 marks a PKI frame. If we have never learned the
        // sender's public key we could not have decrypted it whatever we did,
        // and saying so is more use to them than a flat "no channel".
        bool pkiShaped = header.ChannelHash == 0x00;
        bool knowTheirKey = !string.IsNullOrEmpty(PublicKeyHexFor(header.From));
        uint reason = pkiShaped && !knowTheirKey
            ? RoutingError.PkiUnknownPubkey
            : RoutingError.NoChannel;

        AckRequested?.Invoke(new AckRequest(
            header, ChannelName: null, Pkc: false, TextMessage: false,
            // Undecodable packets are deduped separately and this fires for
            // repeats too, but a NAK is cheap and already hop-limited; treating
            // it as first-sight keeps the reply reaching as far as the request
            // came from, which is what firmware sends.
            Duplicate: false, HasBitfield: false, ErrorReason: reason));
    }

    /// <summary>One-line log summary of a decoded packet.</summary>
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
            PortNum.Position when result.Position is { Latitude: double lat, Longitude: double lon }
                => $"{prefix}: lat={lat:F5} lon={lon:F5}{size}",
            // No coordinates on the wire. The firmware stamps precision_bits onto
            // every position it originates, so a request is 3 bytes rather than 0.
            PortNum.Position when result.Position is not null
                => $"{prefix}: {(result.WantResponse ? "position request" : "no position")}{size}",
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

    private void HandleTextMessage(MeshHeader header, MessageRecord record, MeshDecodeResult result, byte hopsAway)
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
            if (!isReaction && !IsNodeIgnored(header.From))
                IncomingDirectMessage?.Invoke(AlertBell.IsIn(result.Text));
        }
        else if (ResolveChannelTab(result.ChannelName) is { } chanTab2)
        {
            MarkTabNeedsAttention(chanTab2);
            // Ring on channel traffic unless the channel is muted, the sender is
            // ignored, or that node is individually muted. Matches MeshRF.App,
            // which also rings for channel reactions (unlike the DM path above).
            if (!chanTab2.MuteRtttl && !IsNodeIgnored(header.From) && !IsNodeRtttlMuted(header.From))
                IncomingChannelMessage?.Invoke(AlertBell.IsIn(result.Text));
        }

        // Last, so a script can never delay the message appearing or the alert
        // sounding. An ignored sender is ignored here too: muting somebody
        // should not leave the app still answering them automatically.
        if (ScriptEventObserved is { } observer && !IsNodeIgnored(header.From))
        {
            observer(BuildScriptEvent(
                isReaction ? ScriptEventKind.Reaction : ScriptEventKind.Text,
                header, record, result, isDirectToUs, hopsAway,
                emoji: isReaction ? ResolveReactionGlyph(result.Text, result.Emoji) : string.Empty));
        }
    }

    private bool IsNodeRtttlMuted(uint nodeNum) => _nodeStore.Get(nodeNum)?.MuteRtttl == true;

    // Whether a node was last seen inside a given geofence, keyed by waypoint
    // and node. Only a change of state is an event, so the previous answer has
    // to be remembered; without it every position report inside a fence would
    // alert again.
    private readonly Dictionary<(long WaypointId, uint NodeNum), bool> _geofenceInsideState = new();

    /// <summary>
    /// Raises enter/exit alerts for a node's new position. Called for received
    /// positions and for our own, so a fence around home reports us arriving
    /// the same way it reports anyone else.
    /// </summary>
    /// <remarks>
    /// The first position seen for a (fence, node) pair records state without
    /// alerting: with no prior reading there is no crossing, and treating an
    /// unknown as "outside" would fire a spurious enter for every node already
    /// sitting inside a fence when the app starts.
    /// </remarks>
    public void EvaluateGeofenceCrossing(
        uint nodeNum, double lat, double lon,
        float? snrDb = null, float? rssiDbm = null, byte? hopsAway = null)
    {
        if (nodeNum == 0 || Waypoints.Count == 0) return;

        foreach (var wp in Waypoints)
        {
            if (!wp.HasGeofence || wp.IsExpired) continue;
            // A script watching the fence is the second reason to track it. The
            // waypoint's own notify flags govern the chime and the channel note
            // and nothing else, so automation can be hung on a fence without
            // also turning its alerts on for everyone.
            bool watched = GeofenceWatched?.Invoke(wp.DisplayName) == true;
            if (!wp.NotifyOnEnter && !wp.NotifyOnExit && !watched) continue;
            if (wp.NotifyFavoritesOnly && _nodeStore.Get(nodeNum)?.Favorite != true) continue;

            var key = (wp.Id, nodeNum);
            bool hadPrior = _geofenceInsideState.TryGetValue(key, out bool wasInside);

            // Leaving takes a margin, arriving does not: a node sitting on the
            // boundary reports positions either side of it on GPS noise alone,
            // and each of those would otherwise be a crossing — a chime, and
            // now possibly a transmission.
            bool inside = Geofence.Contains(
                wp, lat, lon, hadPrior && wasInside ? Geofence.ExitMarginMetres : 0);

            _geofenceInsideState[key] = inside;
            if (!hadPrior || inside == wasInside) continue;

            if (inside && wp.NotifyOnEnter) RaiseGeofenceAlert(wp, nodeNum, entered: true);
            else if (!inside && wp.NotifyOnExit) RaiseGeofenceAlert(wp, nodeNum, entered: false);

            if (watched)
                RaiseGeofenceCrossing(wp, nodeNum, entered: inside, lat, lon, snrDb, rssiDbm, hopsAway);
        }
    }

    /// <summary>
    /// Whether an armed script watches the named fence, so the detector tracks
    /// it even when the waypoint asks for no alert of its own. Left null,
    /// nothing is watched.
    /// </summary>
    public Func<string, bool>? GeofenceWatched { get; set; }

    /// <summary>
    /// Hands a crossing to the script engine.
    /// </summary>
    /// <remarks>
    /// <para>Carries the crossing position rather than the node table's, so a
    /// script asking an API about where somebody is asks about where they were
    /// when they crossed.</para>
    /// <para>Somebody else's crossing came out of their position packet, so it
    /// carries that packet's signal too. Ours came from a transmission that is
    /// never decoded back and carries none, which is what makes
    /// snr_above:/hops_below: fail closed on our own crossings rather than
    /// pass on a default.</para>
    /// </remarks>
    private void RaiseGeofenceCrossing(
        WaypointRecord wp, uint nodeNum, bool entered, double lat, double lon,
        float? snrDb, float? rssiDbm, byte? hopsAway)
    {
        if (ScriptEventObserved is not { } observer || IsNodeIgnored(nodeNum)) return;

        var node = _nodeStore.Get(nodeNum);
        observer(new ScriptEvent
        {
            Kind = ScriptEventKind.Geofence,
            GeofenceName = wp.DisplayName,
            GeofenceEntered = entered,
            FromNode = nodeNum,
            FromShort = node?.ShortName ?? string.Empty,
            FromLong = string.IsNullOrEmpty(node?.LongName) ? NodeDisplayName(nodeNum) : node!.LongName,
            FromLatitude = lat,
            FromLongitude = lon,
            Channel = wp.Channel,
            IsPrimaryChannel = !string.IsNullOrEmpty(wp.Channel) &&
                               FindChannelByName(wp.Channel) is { Index: 0 },
            FromPacket = hopsAway is not null,
            SnrDb = snrDb,
            RssiDbm = rssiDbm,
            Hops = hopsAway ?? 0,
            SenderIsFavorite = node?.Favorite == true,
            SenderHasKey = !string.IsNullOrEmpty(node?.PublicKey),
            Self = ScriptSelfProvider?.Invoke() ?? ScriptSelf.Unknown,
            At = DateTimeOffset.Now,
        });
    }

    /// <summary>Posts a crossing to the waypoint's channel, persists it so it
    /// survives a restart, and rings unless the channel or the node is
    /// muted — the same rules ordinary channel traffic follows.</summary>
    private void RaiseGeofenceAlert(WaypointRecord wp, uint nodeNum, bool entered)
    {
        string text = $"{NodeDisplayName(nodeNum)} {(entered ? "entered" : "exited")} geofence \"{wp.DisplayName}\"";
        Log($"  geofence: {text}");

        // Stored before the room is resolved: the crossing happened whether or
        // not there is a tab open to show it in, and a return here used to take
        // the record with it.
        PersistChannelNote(wp.Channel, text);

        if (ResolveChannelTab(wp.Channel) is not { } chanTab) return;

        chanTab.Messages.Add(new ChannelMessage
        {
            FromId = GeofenceNoteLabel,
            Text = text,
        });
        while (chanTab.Messages.Count > MaxMessagesPerTab) chanTab.Messages.RemoveAt(0);
        MarkTabNeedsAttention(chanTab);

        if (!chanTab.MuteRtttl && !IsNodeIgnored(nodeNum) && !IsNodeRtttlMuted(nodeNum))
            GeofenceCrossed?.Invoke();
    }

    /// <summary>Stores an app-generated, channel-scoped note (a geofence alert)
    /// so it survives a reload. Filed on the note port with the channel in the
    /// channel column, the same way DM-scoped notes are.</summary>
    private void PersistChannelNote(string channelName, string text)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return;
        try
        {
            _messageStore.Add(new MessageRecord
            {
                PacketId = (uint)Random.Shared.NextInt64(1, uint.MaxValue),
                FromNode = 0,
                ToNode = MyNodeNum,
                Channel = channelName,
                PortNum = MessageStore.ConversationNotePort,
                Text = text,
                Decrypted = true,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Delivery = (int)MessageDelivery.None,
            });
        }
        catch (Exception ex) { Log($"geofence note store failed: {ex.Message}"); }
    }

    /// <summary>
    /// Files a position this node has just sent, the way receiving one files
    /// it. A transmission is never decoded back, so without this our own track
    /// is the only one the history never records.
    /// </summary>
    public void RecordSelfPosition(double latitude, double longitude, int? altitudeM)
    {
        if (MyNodeNum == 0) return;

        var now = DateTimeOffset.UtcNow;
        var previous = _nodeStore.Get(MyNodeNum);
        bool moved = previous?.Latitude != latitude || previous?.Longitude != longitude;

        _nodeStore.Upsert(new NodeRecord
        {
            NodeNum = MyNodeNum,
            LastHeardEpoch = now.ToUnixTimeSeconds(),
            Latitude = latitude,
            Longitude = longitude,
            AltitudeM = altitudeM,
        });
        // Same "only when it moved" rule the receive path uses: position is
        // re-sent on a timer, and storing every repeat would bury real movement.
        if (moved)
        {
            long id = _nodeStore.AddLocationHistory(MyNodeNum, now.UtcDateTime, latitude, longitude, altitudeM);
            LocationHistoryRecorded?.Invoke(MyNodeNum, new NodeLocationHistoryRecord(
                id, MyNodeNum, now.UtcDateTime, latitude, longitude, altitudeM));
        }

        EvaluateGeofenceCrossing(MyNodeNum, latitude, longitude);
        MarkNodeDirty(MyNodeNum);
    }

    /// <summary>Battery level we last reported, so a reading that momentarily
    /// cannot be taken falls back to it rather than looking like a flat
    /// battery. Null before the first report.</summary>
    public byte? SelfBatteryLevel => _nodeStore.Get(MyNodeNum)?.BatteryPct;

    /// <summary>Voltage we last reported, for the same reason.</summary>
    public float? SelfVoltageV => _nodeStore.Get(MyNodeNum)?.VoltageV;

    /// <summary>Files telemetry this node has just sent, as receiving it would.
    /// Shares RecordTelemetryHistory's duplicate suppression.</summary>
    public void RecordSelfTelemetry(MeshTelemetry telemetry)
    {
        if (MyNodeNum == 0) return;
        RecordTelemetryHistory(MyNodeNum, telemetry, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        MarkNodeDirty(MyNodeNum);
    }

    /// <summary>
    /// Files a waypoint this node has just sent, the way receiving one files it.
    /// </summary>
    /// <remarks>
    /// A transmission is never decoded back — the router drops a frame from our
    /// own node before it reaches the waypoint handler — so anything sent has
    /// to be recorded here or it exists everywhere except on the map that sent
    /// it. Replacing a matching id rather than always adding is what makes a
    /// resend an update, including the past-dated one that retires a marker:
    /// that greys out here exactly as it does for everyone who received it.
    /// </remarks>
    public void RecordOutgoingWaypoint(WaypointRecord record)
    {
        _waypointStore.Upsert(record);

        for (int i = 0; i < Waypoints.Count; i++)
        {
            if (Waypoints[i].FromNode != record.FromNode || Waypoints[i].WaypointId != record.WaypointId) continue;
            Waypoints[i] = record;
            return;
        }
        Waypoints.Add(record);
    }

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
        var existingWpIndex = -1;
        for (int i = 0; i < Waypoints.Count; i++)
        {
            if (Waypoints[i].FromNode == header.From && Waypoints[i].WaypointId == waypointId)
            {
                existingWpIndex = i;
                break;
            }
        }
        // A past-dated expiry is how the mesh retires a marker. For one we
        // never held — or deleted already — there is nothing to retire, and
        // filing it would resurrect the marker as a greyed-out row.
        if (existingWpIndex < 0 && waypointRecord.IsExpired) return;
        _waypointStore.Upsert(waypointRecord);
        if (existingWpIndex >= 0) Waypoints[existingWpIndex] = waypointRecord;
        else Waypoints.Add(waypointRecord);
    }

    /// <summary>
    /// How far an answer to <paramref name="header"/> may travel.
    /// </summary>
    /// <remarks>
    /// A reply sent at the full configured limit is rebroadcast by every
    /// repeater in range however close the asker turned out to be, so it gets
    /// only the hops the request needed plus a margin. 3 is firmware's default,
    /// for a host with no owner wired up to say better.
    /// </remarks>
    private byte ReplyHopLimit(MeshHeader header, MeshDecodeResult result) =>
        ResponseHopLimitProvider?.Invoke(header, result.HasDataBitfield) ?? 3;

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
                    (uint)Random.Shared.NextInt64(1, uint.MaxValue), header.PacketId, route: null, snrTowards: null,
                    hopLimit: ReplyHopLimit(header, result));
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
            Delivery = RestoredDelivery(reaction, outgoing),
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
            Delivery = RestoredDelivery(reply, outgoing),
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
