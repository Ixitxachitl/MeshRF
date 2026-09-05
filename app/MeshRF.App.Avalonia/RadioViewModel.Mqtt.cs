// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Mqtt;
using ServiceEnvelope = Meshtastic.Protobufs.ServiceEnvelope;
using ProtoMeshPacket = Meshtastic.Protobufs.MeshPacket;
using ProtoData = Meshtastic.Protobufs.Data;
using ProtoPortNum = Meshtastic.Protobufs.PortNum;
using ProtoMapReport = Meshtastic.Protobufs.MapReport;
using ProtoRole = Meshtastic.Protobufs.Config.Types.DeviceConfig.Types.Role;
using ProtoRegionCode = Meshtastic.Protobufs.Config.Types.LoRaConfig.Types.RegionCode;
using ProtoModemPreset = Meshtastic.Protobufs.Config.Types.LoRaConfig.Types.ModemPreset;
using ProtoHardwareModel = Meshtastic.Protobufs.HardwareModel;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// MQTT uplink/downlink bridge. The connection itself is
/// <see cref="MqttBridge"/> and every gating decision is
/// <see cref="MqttPolicy"/> — both live in MeshRF.Core, so this file is only
/// the wiring: settings, the uplink hook off the RX path, downlink injection
/// back into it, and the periodic map report.
/// </summary>
public partial class RadioViewModel
{
    private readonly MqttBridge _mqttBridge = new();

    // Cheap, thread-safe dedup for downlink, checked on the MQTT worker thread
    // before anything is posted to the UI thread. A busy broker echoes the same
    // packet from several gateways, and without this every copy would take a
    // dispatcher round-trip just to be dropped by the router's own dedup.
    private readonly object _recentMqttDownlinkLock = new();
    private readonly Queue<ulong> _recentMqttDownlinkOrder = new();
    private readonly HashSet<ulong> _recentMqttDownlinkKeys = [];
    private const int RecentMqttDownlinkLimit = 512;

    // Due immediately, matching MeshRF.App: a map report goes out on the first
    // tick after startup rather than only once a full interval has elapsed.
    private DateTime _nextMapReportUtc = DateTime.MinValue;

    // -- Bridge settings ----------------------------------------------------

    [ObservableProperty] private bool _mqttEnabled;
    [ObservableProperty] private string _mqttAddress = string.Empty;
    [ObservableProperty] private string _mqttUsername = string.Empty;
    [ObservableProperty] private string _mqttPassword = string.Empty;
    [ObservableProperty] private bool _mqttEncryptionEnabled = true;
    [ObservableProperty] private bool _mqttJsonEnabled;
    [ObservableProperty] private bool _mqttTlsEnabled;
    [ObservableProperty] private string _mqttRootTopic = string.Empty;
    [ObservableProperty] private bool _mqttMapReportingEnabled;
    [ObservableProperty] private int _mqttMapReportIntervalSeconds = MqttPolicy.DefaultMapPublishIntervalSeconds;
    [ObservableProperty] private byte _mqttMapReportPositionPrecision = MqttPolicy.DefaultMapPositionPrecision;

    /// <summary>Live connection state text for the settings window.</summary>
    [ObservableProperty] private string _mqttStatus = "Disabled";

    /// <summary>Whether the password box shows the real value or dots.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMqttPasswordHidden))]
    private bool _isMqttPasswordRevealed;

    public bool IsMqttPasswordHidden => !IsMqttPasswordRevealed;

    [RelayCommand]
    private void ToggleRevealMqttPassword() => IsMqttPasswordRevealed = !IsMqttPasswordRevealed;

    /// <summary>Map-report location precision choices, shown with the radius
    /// each one fuzzes to in the user's unit system.</summary>
    public IReadOnlyList<PositionPrecisionOption> MqttMapReportPrecisionOptions =>
        DisplayUnits.BuildPositionPrecisionOptions(CurrentUnitSystem)
            .Where(o => o.Bits is >= MqttPolicy.MinMapPositionPrecision
                             and <= MqttPolicy.MaxMapPositionPrecision)
            .ToList();

    // -- Lifecycle ----------------------------------------------------------

    /// <summary>Subscribes to bridge events. Called from the constructor before
    /// settings load, so the first <see cref="RefreshMqttBridge"/> has somewhere
    /// to report to.</summary>
    private void InitMqtt()
    {
        _mqttBridge.StatusChanged += HandleMqttStatusChanged;
        _mqttBridge.EnvelopeReceived += HandleMqttEnvelopeReceived;
        _mqttBridge.JsonMessageReceived += HandleMqttJsonMessageReceived;
        _mqttBridge.Published += HandleMqttPublished;
    }

    /// <summary>One log line per outgoing message. Hooked on the bridge rather
    /// than at each call site so uplinks, the parallel JSON copies and map
    /// reports are all covered by one place.</summary>
    private void HandleMqttPublished(string topic, int payloadBytes) =>
        LogFromAnyThread($"  MQTT tx {topic} ({payloadBytes} bytes)");

    /// <summary>Reads the persisted bridge configuration. Empty stored values
    /// mean "firmware default", which <see cref="MqttPolicy"/> resolves — but
    /// the UI shows the resolved value so the fields aren't mysteriously
    /// blank.</summary>
    private void LoadMqttSettings(AppSettings s)
    {
        MqttAddress = string.IsNullOrEmpty(s.MqttAddress) ? MqttPolicy.DefaultAddress : s.MqttAddress;
        MqttUsername = string.IsNullOrEmpty(s.MqttUsername) ? MqttPolicy.DefaultUsername : s.MqttUsername;
        MqttPassword = string.IsNullOrEmpty(s.MqttPassword) ? MqttPolicy.DefaultPassword : s.MqttPassword;
        MqttEncryptionEnabled = s.MqttEncryptionEnabled;
        MqttJsonEnabled = s.MqttJsonEnabled;
        MqttTlsEnabled = s.MqttTlsEnabled;
        MqttRootTopic = string.IsNullOrEmpty(s.MqttRootTopic) ? MqttPolicy.DefaultRootTopic : s.MqttRootTopic;
        MqttMapReportIntervalSeconds = Math.Max(60, s.MqttMapReportIntervalSeconds);
        MqttMapReportPositionPrecision = (byte)MqttPolicy.CoerceMapPositionPrecision(s.MqttMapReportPositionPrecision);
        MqttMapReportingEnabled = s.MqttMapReportingEnabled;
        // Last: its change handler is what actually starts the bridge, and it
        // must see every other field already loaded.
        MqttEnabled = s.MqttEnabled;
    }

    private void SaveMqttSettings(AppSettings s)
    {
        s.MqttEnabled = MqttEnabled;
        s.MqttAddress = MqttAddress ?? string.Empty;
        s.MqttUsername = MqttUsername ?? string.Empty;
        s.MqttPassword = MqttPassword ?? string.Empty;
        s.MqttEncryptionEnabled = MqttEncryptionEnabled;
        s.MqttJsonEnabled = MqttJsonEnabled;
        s.MqttTlsEnabled = MqttTlsEnabled;
        s.MqttRootTopic = MqttRootTopic ?? string.Empty;
        s.MqttMapReportingEnabled = MqttMapReportingEnabled;
        s.MqttMapReportIntervalSeconds = Math.Max(60, MqttMapReportIntervalSeconds);
        s.MqttMapReportPositionPrecision = MqttPolicy.CoerceMapPositionPrecision(MqttMapReportPositionPrecision);
    }

    private void DisposeMqtt()
    {
        _mqttBridge.StatusChanged -= HandleMqttStatusChanged;
        _mqttBridge.EnvelopeReceived -= HandleMqttEnvelopeReceived;
        _mqttBridge.JsonMessageReceived -= HandleMqttJsonMessageReceived;
        _mqttBridge.Published -= HandleMqttPublished;
        _mqttBridge.Dispose();
    }

    // Encryption affects only how we encode a publish, so it needs no
    // reconnect; everything else changes the connection or the subscriptions.
    partial void OnMqttEnabledChanged(bool value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttAddressChanged(string value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttUsernameChanged(string value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttPasswordChanged(string value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttEncryptionEnabledChanged(bool value) => SaveSettings();
    partial void OnMqttJsonEnabledChanged(bool value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttTlsEnabledChanged(bool value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttRootTopicChanged(string value) { SaveSettings(); RefreshMqttBridge(); }
    partial void OnMqttMapReportingEnabledChanged(bool value)
    {
        SaveSettings();
        // Report promptly on enable rather than an interval later.
        _nextMapReportUtc = value ? DateTime.UtcNow : DateTime.MaxValue;
    }
    // Deliberately does not reschedule: changing the interval shouldn't push
    // out a report that is already due.
    partial void OnMqttMapReportIntervalSecondsChanged(int value) => SaveSettings();
    partial void OnMqttMapReportPositionPrecisionChanged(byte value) => SaveSettings();

    /// <summary>
    /// Recomputes the bridge's connection and subscription set from the current
    /// settings and channel list. Safe to call often — the bridge no-ops when
    /// nothing meaningful changed, so an unrelated settings save doesn't force a
    /// reconnect.
    /// </summary>
    private void RefreshMqttBridge()
    {
        if (!_settingsLoaded) return;

        var channelConfigs = Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
        var downlinkNames = channelConfigs
            .Where(c => c.DownlinkEnabled && !string.IsNullOrEmpty(c.Name))
            .Select(c => c.Name)
            .ToList();

        _mqttBridge.ApplyOptions(new MqttBridgeOptions(
            Enabled: MqttEnabled,
            Address: MqttAddress,
            Username: MqttUsername,
            Password: MqttPassword,
            TlsEnabled: MqttTlsEnabled,
            RootTopic: MqttRootTopic,
            DownlinkChannelNames: downlinkNames,
            AnyDownlinkEnabled: channelConfigs.Any(c => c.DownlinkEnabled),
            JsonEnabled: MqttJsonEnabled));
    }

    // -- Downlink -----------------------------------------------------------

    private void HandleMqttStatusChanged(string status)
    {
        if (Dispatcher.UIThread.CheckAccess()) MqttStatus = status;
        else Dispatcher.UIThread.Post(() => MqttStatus = status);
    }

    private void HandleMqttEnvelopeReceived(ServiceEnvelope envelope)
    {
        var packet = envelope.Packet;
        if (packet is not null && IsRecentMqttDownlinkDuplicate(packet.From, packet.Id)) return;

        // Background priority: a burst of downlinked packets must not starve
        // the waterfall and message list of redraw time.
        if (Dispatcher.UIThread.CheckAccess()) ApplyMqttEnvelope(envelope);
        else Dispatcher.UIThread.Post(() => ApplyMqttEnvelope(envelope), DispatcherPriority.Background);
    }

    private bool IsRecentMqttDownlinkDuplicate(uint from, uint packetId)
    {
        ulong key = ((ulong)from << 32) ^ packetId;
        lock (_recentMqttDownlinkLock)
        {
            if (!_recentMqttDownlinkKeys.Add(key)) return true;
            _recentMqttDownlinkOrder.Enqueue(key);
            while (_recentMqttDownlinkOrder.Count > RecentMqttDownlinkLimit)
                _recentMqttDownlinkKeys.Remove(_recentMqttDownlinkOrder.Dequeue());
            return false;
        }
    }

    /// <summary>
    /// Checks a received envelope against downlink policy and, if accepted,
    /// rebuilds an on-air-shaped frame from it and feeds that through the same
    /// <see cref="MeshRxRouter.ProcessReceivedFrame"/> path real RX uses — so a
    /// downlinked packet gets identical dedup/relay/store/UI handling to one
    /// demodulated off the air. Both firmware wire forms are handled: the usual
    /// encrypted payload passes straight through, while the unencrypted
    /// "decoded" variant is re-encrypted with our own copy of the channel PSK
    /// so nothing downstream needs to know which form it arrived in. PKI
    /// downlinks have no channel PSK, so the decoded variant isn't PKI-eligible.
    /// </summary>
    private void ApplyMqttEnvelope(ServiceEnvelope envelope)
    {
        if (_rxHost.MyNodeNum == 0) return;
        var packet = envelope.Packet;
        if (packet is null || (!packet.HasEncrypted && packet.Decoded is null)) return;

        var channelConfigs = Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
        bool isPki = envelope.ChannelId == MqttPolicy.PkiChannelId;
        var matchedChannel = isPki
            ? null
            : channelConfigs.FirstOrDefault(c =>
                string.Equals(c.Name, envelope.ChannelId, StringComparison.OrdinalIgnoreCase));

        var ctx = new MqttPolicy.DownlinkContext(
            ChannelId: envelope.ChannelId,
            GatewayId: envelope.GatewayId,
            OurNodeId: $"!{_rxHost.MyNodeNum:x8}",
            MatchedLocalChannelDownlinkEnabled: matchedChannel?.DownlinkEnabled ?? false,
            AnyChannelDownlinkEnabled: channelConfigs.Any(c => c.DownlinkEnabled),
            PacketFrom: packet.From,
            OurNodeNum: _rxHost.MyNodeNum,
            HopLimit: (int)packet.HopLimit,
            HopStart: (int)packet.HopStart);

        if (!MqttPolicy.ShouldAcceptDownlink(ctx)) return;
        if (!isPki && matchedChannel is null) return;

        byte channelHash = isPki ? (byte)0x00 : matchedChannel!.Hash;

        byte[] payload;
        if (packet.HasEncrypted)
        {
            payload = packet.Encrypted.ToByteArray();
        }
        else
        {
            if (isPki || matchedChannel is null) return;
            var plain = packet.Decoded.ToByteArray();
            var key = matchedChannel.EffectiveKey;
            payload = (key.Length == 16 || key.Length == 32)
                ? MeshCrypto.Ctr(plain, key, packet.From, packet.Id)
                : plain;
        }

        var frame = new byte[MeshHeader.Size + payload.Length];
        BitConverter.GetBytes(packet.To).CopyTo(frame, 0);
        BitConverter.GetBytes(packet.From).CopyTo(frame, 4);
        BitConverter.GetBytes(packet.Id).CopyTo(frame, 8);
        byte flags = (byte)(packet.HopLimit & 0x07);
        if (packet.WantAck) flags |= 0x08;
        flags |= 0x10; // via_mqtt
        flags |= (byte)((packet.HopStart & 0x07) << 5);
        frame[12] = flags;
        frame[13] = channelHash;
        frame[14] = (byte)packet.NextHop;
        frame[15] = (byte)packet.RelayNode;
        payload.CopyTo(frame.AsSpan(MeshHeader.Size));

        if (!MeshHeader.TryParse(frame, out var header)) return;
        // No log line: the router already logs every packet it handles,
        // whatever its origin, and duplicating that here buries real RF
        // activity under MQTT volume.
        _rxRouter.ProcessReceivedFrame(frame, header, snrDb: null, packetRssiDbm: null);
    }

    private void HandleMqttJsonMessageReceived(string topic, string json)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplyMqttJsonEnvelope(topic, json);
        else Dispatcher.UIThread.Post(() => ApplyMqttJsonEnvelope(topic, json));
    }

    /// <summary>
    /// Firmware MQTT::onReceive's JSON branch: a JSON downlink is only honored
    /// on a channel literally named "mqtt" (with downlink enabled) and can only
    /// command our OWN node to send something. It's a remote-control mechanism,
    /// not general packet injection like the crypt topic — there is no channel
    /// PSK backing a JSON command's authenticity. The resulting frame is
    /// transmitted like any other self-originated send, so it also gets echoed
    /// to MQTT afterwards by the normal self-uplink path.
    /// </summary>
    private void ApplyMqttJsonEnvelope(string topic, string json)
    {
        if (!MqttEnabled || !MqttJsonEnabled || _rxHost.MyNodeNum == 0) return;
        if (!CanTransmit) return;

        var channelName = MqttPolicy.ChannelNameFromJsonTopic(MqttRootTopic, topic);
        if (!string.Equals(channelName, MqttPolicy.JsonCommandChannelName, StringComparison.OrdinalIgnoreCase))
            return;

        var commandChannel = Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).FirstOrDefault(c =>
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase) && c.DownlinkEnabled);
        if (commandChannel is null) return;

        var ourNodeId = $"!{_rxHost.MyNodeNum:x8}";
        var cmd = MqttJsonSerializer.TryParseDownlinkCommand(json, _rxHost.MyNodeNum, ourNodeId);
        if (cmd is null) return;

        var targetChannel = cmd.Channel is uint chIdx
            ? Tabs.OfType<ChannelTabViewModel>().FirstOrDefault(t => t.Config.Index == (int)chIdx)?.Config ?? commandChannel
            : commandChannel;

        byte[] payload;
        PortNum port;
        switch (cmd.Type)
        {
            case "sendtext" when cmd.Text is not null:
                port = PortNum.TextMessage;
                payload = Encoding.UTF8.GetBytes(cmd.Text);
                break;
            case "sendposition" when cmd.LatitudeI is not null && cmd.LongitudeI is not null:
            {
                port = PortNum.Position;
                var pos = new ProtoWriter();
                pos.WriteFixed32Field(1, (uint)cmd.LatitudeI.Value);
                pos.WriteFixed32Field(2, (uint)cmd.LongitudeI.Value);
                if (cmd.Altitude is int alt) pos.WriteVarintField(3, (ulong)(long)alt);
                payload = pos.ToArray();
                break;
            }
            default:
                return;
        }

        var frame = MeshEncoder.Encode(targetChannel, _rxHost.MyNodeNum, cmd.To ?? 0xFFFFFFFFu,
            NextPacketId(), port, payload,
            hopLimit: cmd.HopLimit ?? (byte)HopLimit, okToMqtt: OkToMqtt,
            xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey);

        _rxHost.Log($"  MQTT JSON command '{cmd.Type}' -> {targetChannel.Name}");
        TransmitBackground(frame);
    }

    // -- Uplink -------------------------------------------------------------

    /// <summary>
    /// Publishes a received packet to MQTT if eligible. Independent of
    /// relaying: firmware treats uplink and rebroadcast as two parallel
    /// side-effects of processing an incoming packet, not a chained decision.
    /// Called by <see cref="AvaloniaMeshRxHost"/> from the shared router.
    /// </summary>
    private void UplinkIfEligible(byte[] frame, MeshHeader header, MeshDecodeResult? result,
                                  bool isFromUs, float? snrDb, float? rssiDbm)
    {
        if (!MqttEnabled) return;
        if (header.ViaMqtt) return; // never re-publish what came from MQTT
        if (_rxHost.MyNodeNum == 0) return;

        var channelConfigs = Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
        bool anyChannelUplink = channelConfigs.Any(c => c.UplinkEnabled);
        if (!anyChannelUplink) return;

        bool isPki;
        ChannelConfig? matchedChannel = null;
        if (result is not null && !string.IsNullOrEmpty(result.ChannelName))
        {
            isPki = false;
            matchedChannel = channelConfigs.FirstOrDefault(c =>
                string.Equals(c.Name, result.ChannelName, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Either PKC-decoded or entirely undecodable. Firmware can only
            // meaningfully uplink the PKI case — an undecodable normal-channel
            // packet has no channel id to publish under. Match that: only the
            // PKI-shaped frame (hash 0x00, unicast) is eligible when undecoded.
            isPki = header.ChannelHash == 0x00 && !header.IsBroadcast;
            if (!isPki) return;
        }

        var ctx = new MqttPolicy.UplinkContext(
            ViaMqtt: header.ViaMqtt,
            AnyChannelUplinkEnabled: anyChannelUplink,
            ChannelUplinkEnabled: matchedChannel?.UplinkEnabled ?? false,
            IsPki: isPki,
            IsFromUs: isFromUs,
            IsDefaultServer: MqttPolicy.IsDefaultServer(MqttAddress),
            ServerIsPrivate: MqttPolicy.IsPrivateHost(MqttPolicy.EffectiveHost(MqttAddress)),
            HasOkToMqttBit: true,
            OkToMqtt: result?.OkToMqtt ?? false,
            IsRangeTestOrDetectionSensorPort: result is not null &&
                (result.Port == PortNum.RangeTest || result.Port == PortNum.DetectionSensor));

        if (!MqttPolicy.ShouldUplink(ctx)) return;

        var channelId = isPki ? MqttPolicy.PkiChannelId : (matchedChannel?.Name ?? string.Empty);
        if (string.IsNullOrEmpty(channelId)) return;

        var packet = new ProtoMeshPacket
        {
            From = header.From,
            To = header.To,
            Channel = (uint)(matchedChannel?.Index ?? 0),
            Id = header.PacketId,
            HopLimit = header.HopLimit,
            HopStart = header.HopStart,
            WantAck = header.WantAck,
        };
        if (MqttEncryptionEnabled)
        {
            packet.Encrypted = ByteString.CopyFrom(frame, MeshHeader.Size, frame.Length - MeshHeader.Size);
        }
        else
        {
            // Non-default mode: publish the decoded plaintext Data message
            // instead of the still-encrypted channel bytes, matching firmware
            // publishing mp_decoded when encryption_enabled is off. Only
            // possible if we actually decoded it, which is never true for PKI.
            if (isPki || result is null) return;
            packet.Decoded = new ProtoData
            {
                Portnum = (ProtoPortNum)(int)result.Port,
                Payload = ByteString.CopyFrom(result.AppPayload),
                WantResponse = result.WantResponse,
                RequestId = result.RequestId,
                ReplyId = result.ReplyId,
                Emoji = result.Emoji,
            };
        }

        var envelope = new ServiceEnvelope
        {
            Packet = packet,
            ChannelId = channelId,
            GatewayId = $"!{_rxHost.MyNodeNum:x8}",
        };

        _mqttBridge.Publish(
            MqttPolicy.UplinkTopic(MqttRootTopic, channelId, envelope.GatewayId),
            envelope.ToByteArray());

        // Parallel human-readable JSON publish (firmware json_enabled), only
        // possible when we actually decoded the packet.
        if (MqttJsonEnabled && result is not null)
        {
            var json = MqttJsonSerializer.Serialize(result, header, envelope.GatewayId,
                channelIndex: (uint)(matchedChannel?.Index ?? 0),
                rxTimeEpoch: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                rssi: rssiDbm.HasValue ? (int)Math.Round(rssiDbm.Value) : null,
                snrDb: snrDb);
            _mqttBridge.Publish(
                MqttPolicy.JsonUplinkTopic(MqttRootTopic, channelId, envelope.GatewayId),
                Encoding.UTF8.GetBytes(json));
        }
    }

    /// <summary>
    /// Firmware Router::send's isFromUs branch: a packet we just transmitted is
    /// offered to MQTT exactly like a received one, only with IsFromUs set so
    /// the public-server "DontMqttMeBro" opt-in check is skipped — a node always
    /// uplinks its own traffic. Decodes the frame we just built with our own
    /// channel PSKs rather than threading portnum context through every send
    /// call site; that's cheap and purely local.
    /// </summary>
    private void UplinkSelfOriginatedIfEligible(byte[] frame)
    {
        if (!MqttEnabled || _rxHost.MyNodeNum == 0) return;
        if (!MeshHeader.TryParse(frame, out var header)) return;
        if (header.From != _rxHost.MyNodeNum) return;

        var channelConfigs = Tabs.OfType<ChannelTabViewModel>().Select(t => t.Config).ToList();
        var result = MeshDecoder.Decode(frame, channelConfigs);
        UplinkIfEligible(frame, header, result, isFromUs: true, snrDb: null, rssiDbm: null);
    }

    // -- Map reporting ------------------------------------------------------

    /// <summary>Due-check driven by the poll timer. Deliberately not part of
    /// the auto-report tick, which requires a TX-capable device — a map report
    /// only ever goes to the broker, so a receive-only setup should still be
    /// able to publish one.</summary>
    private void TickMapReport()
    {
        if (!MqttEnabled || !MqttMapReportingEnabled) return;
        if (DateTime.UtcNow < _nextMapReportUtc) return;

        // Only reschedule once something actually went out. The startup report
        // is due immediately, but our node number and home location often
        // aren't resolved yet that early (a GPS fix can take a while) — pushing
        // the schedule out regardless would silently swallow it for a whole
        // interval. The early-outs in PerhapsReportToMap are cheap enough to
        // re-check each poll until they pass.
        if (PerhapsReportToMap())
            _nextMapReportUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, MqttMapReportIntervalSeconds));
    }

    /// <summary>
    /// Firmware MQTT::perhapsReportToMap(): publish an unencrypted, unrouted
    /// MapReport straight to the broker's map topic — never over the air.
    /// Location is fuzzed to <see cref="MqttMapReportPositionPrecision"/> bits
    /// exactly like a channel position broadcast, and the plaintext payload is
    /// wrapped in a ServiceEnvelope with an empty channel id, since a map report
    /// isn't scoped to any channel.
    /// </summary>
    /// <returns>True if a report was published; false when we don't yet know
    /// our node number or location, which is normal right after startup.</returns>
    private bool PerhapsReportToMap()
    {
        if (_rxHost.MyNodeNum == 0) return false;
        if (!TryGetHomeLocation(out double lat, out double lon)) return false;

        int precisionBits = MqttPolicy.CoerceMapPositionPrecision(MqttMapReportPositionPrecision);
        int latI = (int)Math.Round(lat / 1e-7);
        int lonI = (int)Math.Round(lon / 1e-7);
        if (precisionBits < 32)
        {
            latI = (int)((uint)latI & (uint.MaxValue << (32 - precisionBits)));
            lonI = (int)((uint)lonI & (uint.MaxValue << (32 - precisionBits)));
            latI += 1 << (31 - precisionBits);
            lonI += 1 << (31 - precisionBits);
        }

        // Firmware answers this field with Channels::hasDefaultChannel, which
        // asks about the whole list rather than the primary alone: the map is
        // being told whether this node is reachable on the shared channel at
        // all, not which channel it prefers.
        bool hasDefaultChannel = DefaultChannelMinimums.HasDefaultChannel(
            AllChannelConfigs(), SelectedPreset, !IsCustomLoraParams, OnDefaultFrequencySlot);

        // Firmware's NodeDB::getNumOnlineMeshNodes(localOnly: true): heard in the
        // last 2 hours, most recent sighting not via MQTT. Our own node is skipped
        // because firmware never counts it either — updateFrom() bails on packets
        // from self, so the self entry's last_heard stays 0. We have to say so
        // explicitly, since MeshRF does stamp last_heard on self when we transmit.
        var twoHoursAgoEpoch = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
        int numOnlineLocalNodes = _nodeStore.All()
            .Count(n => n.NodeNum != _rxHost.MyNodeNum
                        && n.SeenViaMqtt != true
                        && n.LastHeardEpoch >= twoHoursAgoEpoch);

        if (!Enum.TryParse<ProtoModemPreset>(SelectedPreset.ToString(), out var modemPreset))
            modemPreset = ProtoModemPreset.LongFast;

        var report = new ProtoMapReport
        {
            LongName = MyLongName ?? string.Empty,
            ShortName = MyShortName ?? string.Empty,
            Role = (ProtoRole)RoleEnumValue(MyRole),
            HwModel = (ProtoHardwareModel)HardwareModels.Id(MyHwModel),
            FirmwareVersion = MyFirmwareVersion ?? string.Empty,
            Region = ToProtoRegionCode(SelectedRegion),
            ModemPreset = modemPreset,
            HasDefaultChannel = hasDefaultChannel,
            LatitudeI = latI,
            LongitudeI = lonI,
            PositionPrecision = (uint)precisionBits,
            NumOnlineLocalNodes = (uint)numOnlineLocalNodes,
            HasOptedReportLocation = true,
        };
        if (HomeAltitudeMeters is int alt) report.Altitude = alt;

        var envelope = new ServiceEnvelope
        {
            Packet = new ProtoMeshPacket
            {
                From = _rxHost.MyNodeNum,
                Id = NextPacketId(),
                Decoded = new ProtoData
                {
                    Portnum = ProtoPortNum.MapReportApp,
                    Payload = report.ToByteString(),
                },
            },
            ChannelId = string.Empty,
            GatewayId = $"!{_rxHost.MyNodeNum:x8}",
        };

        _mqttBridge.Publish(MqttPolicy.MapReportTopic(MqttRootTopic), envelope.ToByteArray());
        _rxHost.Log($"MQTT map report sent ({numOnlineLocalNodes} local nodes online).");
        return true;
    }

    // Region's values are the protobuf's own, but protoc's member spelling
    // differs ("Us"/"Eu433" for "US"/"EU_433") and the protobuf carries region
    // codes MeshRF has no band for, so map by meaning rather than casting.
    private static ProtoRegionCode ToProtoRegionCode(Region region) => region switch
    {
        Region.US         => ProtoRegionCode.Us,
        Region.EU_433     => ProtoRegionCode.Eu433,
        Region.EU_868     => ProtoRegionCode.Eu868,
        Region.EU_866     => ProtoRegionCode.Eu866,
        Region.EU_N_868   => ProtoRegionCode.EuN868,
        Region.CN         => ProtoRegionCode.Cn,
        Region.JP         => ProtoRegionCode.Jp,
        Region.ANZ        => ProtoRegionCode.Anz,
        Region.ANZ_433    => ProtoRegionCode.Anz433,
        Region.KR         => ProtoRegionCode.Kr,
        Region.TW         => ProtoRegionCode.Tw,
        Region.RU         => ProtoRegionCode.Ru,
        Region.IN         => ProtoRegionCode.In,
        Region.NZ_865     => ProtoRegionCode.Nz865,
        Region.TH         => ProtoRegionCode.Th,
        Region.LORA_24    => ProtoRegionCode.Lora24,
        Region.UA_433     => ProtoRegionCode.Ua433,
        Region.MY_433     => ProtoRegionCode.My433,
        Region.MY_919     => ProtoRegionCode.My919,
        Region.SG_923     => ProtoRegionCode.Sg923,
        Region.PH_433     => ProtoRegionCode.Ph433,
        Region.PH_868     => ProtoRegionCode.Ph868,
        Region.PH_915     => ProtoRegionCode.Ph915,
        Region.KZ_433     => ProtoRegionCode.Kz433,
        Region.KZ_863     => ProtoRegionCode.Kz863,
        Region.NP_865     => ProtoRegionCode.Np865,
        Region.BR_902     => ProtoRegionCode.Br902,
        Region.ITU1_2M    => ProtoRegionCode.Itu12M,
        Region.ITU2_2M    => ProtoRegionCode.Itu22M,
        Region.ITU3_2M    => ProtoRegionCode.Itu32M,
        Region.ITU1_70CM  => ProtoRegionCode.Itu170Cm,
        Region.ITU2_70CM  => ProtoRegionCode.Itu270Cm,
        Region.ITU3_70CM  => ProtoRegionCode.Itu370Cm,
        Region.ITU2_125CM => ProtoRegionCode.Itu2125Cm,
        _                 => ProtoRegionCode.Unset,
    };
}
