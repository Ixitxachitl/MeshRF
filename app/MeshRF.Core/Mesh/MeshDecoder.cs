// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Google.Protobuf;
using MeshRF.Channels;

namespace MeshRF.Mesh;

/// <summary>Decoded contents of a Meshtastic <c>Data</c> sub-message.</summary>
public sealed class MeshDecodeResult
{
    public MeshHeader Header { get; init; }
    public string ChannelName { get; init; } = string.Empty;
    public PortNum Port { get; init; }

    /// <summary>Plaintext for TEXT_MESSAGE_APP; null otherwise.</summary>
    public string? Text { get; init; }

    /// <summary>Parsed User (NODEINFO_APP); null otherwise.</summary>
    public MeshUser? User { get; init; }

    /// <summary>Parsed Position (POSITION_APP); null otherwise.</summary>
    public MeshPosition? Position { get; init; }

    /// <summary>Parsed Waypoint (WAYPOINT_APP); null otherwise.</summary>
    public MeshWaypoint? Waypoint { get; init; }

    /// <summary>Parsed Telemetry (TELEMETRY_APP); null otherwise.</summary>
    public MeshTelemetry? Telemetry { get; init; }

    /// <summary>Data.want_response (field 3): the sender wants a reply (e.g. a
    /// directed NodeInfo request asking us to send ours back).</summary>
    public bool WantResponse { get; init; }

    /// <summary>Data.request_id (field 6): the packet id this message responds
    /// to. For a ROUTING ack/nak it identifies the original packet.</summary>
    public uint RequestId { get; init; }

    /// <summary>Data.reply_id (field 7): packet id this message is reacting or replying to.</summary>
    public uint ReplyId { get; init; }

    /// <summary>Data.emoji (field 8): Unicode code point for emoji reactions.</summary>
    public uint Emoji { get; init; }

    /// <summary>Data.bitfield (field 9) bit 0 (ok_to_mqtt): the sender permits
    /// gateways to uplink this packet to public MQTT.</summary>
    public bool OkToMqtt { get; init; }

    /// <summary>Data.dest (field 4): destination nodenum when populated.</summary>
    public uint DataDest { get; init; }

    /// <summary>Data.source (field 5): original sender nodenum when populated.</summary>
    public uint DataSource { get; init; }

    /// <summary>Data.bitfield raw numeric value (field 9).</summary>
    public uint DataBitfield { get; init; }

    /// <summary>
    /// Data.xeddsa_signature (field 10, bytes), used by newer Meshtastic
    /// builds for payload authentication metadata.
    /// Empty when absent.
    /// </summary>
    public byte[] DataField10 { get; init; } = Array.Empty<byte>();

    /// <summary>Full Data protobuf JSON (generated class), including fields not
    /// mapped to the strongly-typed properties above.</summary>
    public string? DataProtoJson { get; init; }

    /// <summary>Full application payload protobuf JSON when this port has a
    /// known protobuf schema and parsing succeeds.</summary>
    public string? AppProtoJson { get; init; }

    /// <summary>For a ROUTING_APP packet, the Routing.error_reason value: 0 = ACK
    /// (NONE), non-zero = NAK reason. -1 when this isn't a routing packet.</summary>
    public int RoutingError { get; init; } = -1;

    /// <summary>Parsed RouteDiscovery (TRACEROUTE_APP); null otherwise.</summary>
    public MeshRouteDiscovery? RouteDiscovery { get; init; }

    /// <summary>Parsed NeighborInfo (NEIGHBORINFO_APP); null otherwise.</summary>
    public MeshNeighborInfo? NeighborInfo { get; init; }

    /// <summary>Parsed StoreForward (STORE_FORWARD_APP); null otherwise.</summary>
    public MeshStoreForward? StoreForward { get; init; }

    /// <summary>Raw decrypted application payload (the Data.payload bytes).</summary>
    public byte[] AppPayload { get; init; } = Array.Empty<byte>();
}

/// <summary>Subset of the Meshtastic <c>User</c> protobuf.</summary>
public sealed class MeshUser
{
    public string Id { get; init; } = string.Empty;
    public string LongName { get; init; } = string.Empty;
    public string ShortName { get; init; } = string.Empty;
    public int HwModel { get; init; }
    public string Role { get; init; } = string.Empty;

    /// <summary>32-byte X25519 public key (field 8), empty if not advertised.</summary>
    public byte[] PublicKey { get; init; } = Array.Empty<byte>();
}

/// <summary>Subset of the Meshtastic <c>Position</c> protobuf.</summary>
public sealed class MeshPosition
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int? AltitudeM { get; init; }
}

/// <summary>Subset of the Meshtastic <c>Waypoint</c> protobuf.</summary>
public sealed class MeshWaypoint
{
    public uint Id { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public uint ExpireEpoch { get; init; }
    public uint LockedTo { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public uint? Icon { get; init; }
}

/// <summary>
/// Subset of the Meshtastic <c>Telemetry</c> protobuf, covering both the
/// <c>DeviceMetrics</c> and <c>EnvironmentMetrics</c> variants.
/// </summary>
public sealed class MeshTelemetry
{
    // Device metrics.
    public byte? BatteryLevel { get; init; }
    public float? Voltage { get; init; }
    public float? ChannelUtilization { get; init; }
    public float? AirUtilTx { get; init; }
    public uint? UptimeSeconds { get; init; }

    // Environment metrics.
    public float? TemperatureC { get; init; }
    public float? RelativeHumidityPct { get; init; }
    public float? BarometricPressureHpa { get; init; }
    public float? GasResistanceMohm { get; init; }
    public int? Iaq { get; init; }

    public bool HasDeviceMetrics =>
        BatteryLevel.HasValue || Voltage.HasValue ||
        ChannelUtilization.HasValue || AirUtilTx.HasValue || UptimeSeconds.HasValue;

    public bool HasEnvironmentMetrics =>
        TemperatureC.HasValue || RelativeHumidityPct.HasValue ||
        BarometricPressureHpa.HasValue || GasResistanceMohm.HasValue || Iaq.HasValue;
}

/// <summary>
/// Subset of the Meshtastic <c>RouteDiscovery</c> protobuf carried by
/// TRACEROUTE_APP: the list of node numbers a packet hopped through towards the
/// destination (and back), with the per-hop SNR (stored scaled by 4 in the
/// firmware, e.g. a value of 18 means 4.5 dB).
/// </summary>
public sealed class MeshRouteDiscovery
{
    /// <summary>Intermediate node numbers towards the destination (field 1).</summary>
    public IReadOnlyList<uint> Route { get; init; } = Array.Empty<uint>();

    /// <summary>Per-hop SNR towards the destination, scaled by 4 (field 2).</summary>
    public IReadOnlyList<int> SnrTowards { get; init; } = Array.Empty<int>();

    /// <summary>Intermediate node numbers on the way back (field 3).</summary>
    public IReadOnlyList<uint> RouteBack { get; init; } = Array.Empty<uint>();

    /// <summary>Per-hop SNR on the way back, scaled by 4 (field 4).</summary>
    public IReadOnlyList<int> SnrBack { get; init; } = Array.Empty<int>();
}

/// <summary>A single entry in a <see cref="MeshNeighborInfo"/> neighbors list.</summary>
public sealed class MeshNeighborEntry
{
    public uint NodeId { get; init; }
    /// <summary>SNR of the last received packet from this neighbor (dB, float).</summary>
    public float Snr { get; init; }
}

/// <summary>Parsed NeighborInfo (NEIGHBORINFO_APP): the sender's view of its direct
/// neighbours, each with an SNR measurement.</summary>
public sealed class MeshNeighborInfo
{
    /// <summary>Node number of the node reporting its neighbors (field 1).</summary>
    public uint NodeId { get; init; }
    /// <summary>Last node that relayed this info onwards (field 2).</summary>
    public uint LastSentById { get; init; }
    /// <summary>Broadcast interval of the reporting node in seconds (field 3).</summary>
    public uint BroadcastIntervalSecs { get; init; }
    /// <summary>Neighbor list (field 4, repeated sub-message).</summary>
    public IReadOnlyList<MeshNeighborEntry> Neighbors { get; init; } = Array.Empty<MeshNeighborEntry>();
}

/// <summary>Store &amp; Forward request/response type (field 1 of StoreAndForward).</summary>
public enum StoreForwardType
{
    Unset = 0,
    RouterError = 1,
    RouterHeartbeat = 2,
    RouterPing = 3,
    RouterPong = 4,
    RouterBusy = 5,
    RouterHistory = 6,
    RouterStats = 7,
    RouterTextDirect = 8,
    RouterTextBroadcast = 9,
    ClientError = 64,
    ClientHistory = 65,
    ClientStats = 66,
    ClientPing = 67,
    ClientPong = 68,
    ClientAbort = 106,
}

/// <summary>Store &amp; Forward statistics (field 2 sub-message).</summary>
public sealed class StoreForwardStats
{
    public uint MessagesTotal { get; init; }
    public uint MessagesSaved { get; init; }
    public uint MessagesMax { get; init; }
    public uint UpTimeSeconds { get; init; }
    public uint Requests { get; init; }
    public uint RequestsHistory { get; init; }
    public bool HeartbeatEnabled { get; init; }
    public uint ReturnMax { get; init; }
    public uint ReturnWindowMinutes { get; init; }
}

/// <summary>Store &amp; Forward heartbeat (field 4 sub-message).</summary>
public sealed class StoreForwardHeartbeat
{
    public uint PeriodSeconds { get; init; }
    public bool IsSecondary { get; init; }
}

/// <summary>Parsed StoreAndForward (STORE_FORWARD_APP).</summary>
public sealed class MeshStoreForward
{
    public StoreForwardType Type { get; init; }
    public StoreForwardStats? Stats { get; init; }
    public StoreForwardHeartbeat? Heartbeat { get; init; }
    /// <summary>History message count (field 3.1) when Type is RouterHistory.</summary>
    public uint? HistoryMessages { get; init; }
    /// <summary>History window in minutes (field 3.2).</summary>
    public uint? HistoryWindow { get; init; }
    /// <summary>Text payload for RouterTextDirect/RouterTextBroadcast.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// Turns a decoded LoRa frame (16-byte header + encrypted payload) into a
/// structured <see cref="MeshDecodeResult"/> by trying each known channel's
/// PSK, AES-CTR decrypting, and parsing the inner protobufs.
/// </summary>
public static class MeshDecoder
{
    private static readonly JsonFormatter ProtoJson = new(new JsonFormatter.Settings(formatDefaultValues: false));

    /// <summary>
    /// Attempt to decode <paramref name="frame"/> using the supplied channels.
    /// Returns null if the frame is malformed or no channel key produces a
    /// plausible protobuf.
    /// </summary>
    public static MeshDecodeResult? Decode(ReadOnlySpan<byte> frame,
                                           IReadOnlyList<ChannelConfig> channels)
    {
        if (!MeshHeader.TryParse(frame, out var header)) return null;
        if (frame.Length <= MeshHeader.Size) return null; // header only, nothing to decrypt

        var cipher = frame.Slice(MeshHeader.Size).ToArray();

        // Only try channels whose computed hash matches the packet's channel-hash
        // hint byte.  This mirrors firmware perhapsDecode (Router.cpp) which rejects
        // any channel whose hash doesn't match rather than falling back to all
        // channels.  Trying non-matching channels can produce false-positive decodes
        // because a wrong-key AES-CTR result can accidentally pass the IsPlausible
        // check (PortNum enum value in range + rdr.End clean).
        // Hash collisions (two channels with the same computed byte) are handled
        // naturally: both candidates are tried in index order.
        var ordered = channels
            .Where(c => c.Hash == header.ChannelHash)
            .ToList();

        foreach (var ch in ordered)
        {
            var key = ch.EffectiveKey;
            if (key.Length != 16 && key.Length != 32) continue;

            byte[] plain;
            try { plain = MeshCrypto.Ctr(cipher, key, header.From, header.PacketId); }
            catch { continue; }

            if (TryParseData(plain, out var port, out var appPayload,
                             out var wantResp, out var reqId, out var replyId,
                             out var emoji, out var okMqtt,
                             out var dataDest, out var dataSource,
                             out var dataBitfield,
                             out var dataField10,
                             out var dataProtoJson,
                             out var appProtoJson) &&
                IsPlausible(port, appPayload, replyId, emoji))
            {
                return Build(header, ch.Name, port, appPayload, wantResp,
                             reqId, replyId, emoji, okMqtt,
                             dataDest, dataSource, dataBitfield,
                             dataField10, dataProtoJson, appProtoJson);
            }
        }
        return null;
    }

    /// <summary>
    /// Attempt a PKC (public-key) decrypt of a direct message addressed to us.
    /// Modern Meshtastic firmware seals DMs with X25519 + AES-CCM instead of the
    /// channel PSK; such frames carry a channel-hash byte of 0x00. Returns null
    /// if the frame isn't a verifiable PKC packet for this key pair.
    /// </summary>
    /// <param name="frame">Raw on-air frame (16-byte header + sealed payload).</param>
    /// <param name="myPrivateKey">Our 32-byte X25519 private key.</param>
    /// <param name="senderPublicKey">The sender's 32-byte X25519 public key.</param>
    public static MeshDecodeResult? DecodePkc(ReadOnlySpan<byte> frame,
                                              byte[] myPrivateKey,
                                              byte[] senderPublicKey)
    {
        if (myPrivateKey is null || myPrivateKey.Length != 32) return null;
        if (senderPublicKey is null || senderPublicKey.Length != 32) return null;
        if (!MeshHeader.TryParse(frame, out var header)) return null;
        if (frame.Length <= MeshHeader.Size + MeshCrypto.PkcOverhead) return null;

        var sealedPayload = frame.Slice(MeshHeader.Size).ToArray();

        byte[]? plain;
        try
        {
            plain = MeshCrypto.PkcDecrypt(sealedPayload, myPrivateKey, senderPublicKey,
                                          header.From, header.PacketId);
        }
        catch { return null; }

        if (plain is null) return null; // auth tag mismatch

        if (TryParseData(plain, out var port, out var appPayload,
                         out var wantResp, out var reqId, out var replyId,
                         out var emoji, out var okMqtt,
                         out var dataDest, out var dataSource,
                         out var dataBitfield,
                         out var dataField10,
                         out var dataProtoJson,
                         out var appProtoJson) &&
            IsPlausible(port, appPayload, replyId, emoji))
        {
            return Build(header, "PKC", port, appPayload, wantResp,
                         reqId, replyId, emoji, okMqtt,
                         dataDest, dataSource, dataBitfield,
                         dataField10, dataProtoJson, appProtoJson);
        }
        return null;
    }

    // Parse Meshtastic Data with generated protobuf classes so new upstream
    // fields are decoded automatically as the schema evolves.
    private static bool TryParseData(byte[] data, out PortNum port, out byte[] payload,
                                     out bool wantResponse, out uint requestId,
                                     out uint replyId, out uint emoji,
                                     out bool okToMqtt,
                                     out uint dataDest,
                                     out uint dataSource,
                                     out uint dataBitfield,
                                     out byte[] dataField10,
                                     out string? dataProtoJson,
                                     out string? appProtoJson)
    {
        port = PortNum.Unknown;
        payload = Array.Empty<byte>();
        wantResponse = false;
        requestId = 0;
        replyId = 0;
        emoji = 0;
        okToMqtt = false;
        dataDest = 0;
        dataSource = 0;
        dataBitfield = 0;
        dataField10 = Array.Empty<byte>();
        dataProtoJson = null;
        appProtoJson = null;

        Meshtastic.Protobufs.Data parsed;
        try
        {
            parsed = Meshtastic.Protobufs.Data.Parser.ParseFrom(data);
        }
        catch
        {
            return false;
        }

        port = (PortNum)(int)parsed.Portnum;
        payload = parsed.Payload.ToByteArray();
        wantResponse = parsed.WantResponse;
        requestId = parsed.RequestId;
        replyId = parsed.ReplyId;
        emoji = parsed.Emoji;
        dataDest = parsed.Dest;
        dataSource = parsed.Source;
        dataBitfield = parsed.HasBitfield ? parsed.Bitfield : 0u;
        okToMqtt = (dataBitfield & 0x01) != 0;
        wantResponse = wantResponse || (dataBitfield & 0x02) != 0;
        dataField10 = parsed.XeddsaSignature.ToByteArray();

        try { dataProtoJson = ProtoJson.Format(parsed); } catch { dataProtoJson = null; }
        try { appProtoJson = TryFormatAppPayloadProtoJson(port, payload); } catch { appProtoJson = null; }

        return true;
    }

    private static string? TryFormatAppPayloadProtoJson(PortNum port, byte[] payload)
    {
        if (payload.Length == 0) return null;

        IMessage? msg = port switch
        {
            PortNum.NodeInfo => Meshtastic.Protobufs.User.Parser.ParseFrom(payload),
            PortNum.Position => Meshtastic.Protobufs.Position.Parser.ParseFrom(payload),
            PortNum.Waypoint => Meshtastic.Protobufs.Waypoint.Parser.ParseFrom(payload),
            PortNum.Telemetry => Meshtastic.Protobufs.Telemetry.Parser.ParseFrom(payload),
            PortNum.Routing => Meshtastic.Protobufs.Routing.Parser.ParseFrom(payload),
            PortNum.Traceroute => Meshtastic.Protobufs.RouteDiscovery.Parser.ParseFrom(payload),
            PortNum.NeighborInfo => Meshtastic.Protobufs.NeighborInfo.Parser.ParseFrom(payload),
            PortNum.StoreForward => Meshtastic.Protobufs.StoreAndForward.Parser.ParseFrom(payload),
            PortNum.Admin => Meshtastic.Protobufs.AdminMessage.Parser.ParseFrom(payload),
            PortNum.KeyVerification => Meshtastic.Protobufs.KeyVerification.Parser.ParseFrom(payload),
            PortNum.MapReport => Meshtastic.Protobufs.MapReport.Parser.ParseFrom(payload),
            PortNum.AtakPlugin => Meshtastic.Protobufs.TAKPacket.Parser.ParseFrom(payload),
            PortNum.RemoteHardware => Meshtastic.Protobufs.HardwareMessage.Parser.ParseFrom(payload),
            PortNum.PaxCounter => Meshtastic.Protobufs.Paxcount.Parser.ParseFrom(payload),
            PortNum.Audio => Meshtastic.Protobufs.Compressed.Parser.ParseFrom(payload),
            _ => null,
        };

        return msg is null ? null : ProtoJson.Format(msg);
    }

    // Reject obviously-wrong decrypts (wrong key -> garbage portnum / payload).
    private static bool IsPlausible(PortNum port, byte[] payload,
                                    uint replyId = 0, uint emoji = 0)
    {
        if (!Enum.IsDefined(typeof(PortNum), port)) return false;
        if (port == PortNum.TextMessage)
        {
            if (payload.Length == 0)
                return replyId != 0 && emoji != 0; // per-message reaction packet
            return IsValidUtf8(payload);
        }
        return true;
    }

    private static MeshDecodeResult Build(MeshHeader header, string channel,
                                          PortNum port, byte[] payload,
                                          bool wantResponse = false, uint requestId = 0,
                                          uint replyId = 0, uint emoji = 0,
                                          bool okToMqtt = false,
                                          uint dataDest = 0,
                                          uint dataSource = 0,
                                          uint dataBitfield = 0,
                                          byte[]? dataField10 = null,
                                          string? dataProtoJson = null,
                                          string? appProtoJson = null)
    {
        string? text = null;
        MeshUser? user = null;
        MeshPosition? pos = null;
        MeshWaypoint? waypoint = null;
        MeshTelemetry? telem = null;
        MeshRouteDiscovery? route = null;
        MeshNeighborInfo? neighborInfo = null;
        MeshStoreForward? storeForward = null;
        int routingError = -1;

        switch (port)
        {
            case PortNum.TextMessage:
                text = Encoding.UTF8.GetString(payload);
                break;
            case PortNum.NodeInfo:
                user = ParseUser(payload);
                break;
            case PortNum.Position:
                pos = ParsePosition(payload);
                break;
            case PortNum.Waypoint:
                waypoint = ParseWaypoint(payload);
                break;
            case PortNum.Telemetry:
                telem = ParseTelemetry(payload);
                break;
            case PortNum.Routing:
                routingError = ParseRoutingError(payload);
                break;
            case PortNum.Traceroute:
                route = ParseRouteDiscovery(payload);
                break;
            case PortNum.NeighborInfo:
                neighborInfo = ParseNeighborInfo(payload);
                break;
            case PortNum.StoreForward:
                storeForward = ParseStoreForward(payload);
                break;
        }

        return new MeshDecodeResult
        {
            Header = header,
            ChannelName = channel,
            Port = port,
            Text = text,
            User = user,
            Position = pos,
            Waypoint = waypoint,
            Telemetry = telem,
            RouteDiscovery = route,
            NeighborInfo = neighborInfo,
            StoreForward = storeForward,
            WantResponse = wantResponse,
            RequestId = requestId,
            ReplyId = replyId,
            Emoji = emoji,
            OkToMqtt = okToMqtt,
            DataDest = dataDest,
            DataSource = dataSource,
            DataBitfield = dataBitfield,
            DataField10 = dataField10 ?? Array.Empty<byte>(),
            DataProtoJson = dataProtoJson,
            AppProtoJson = appProtoJson,
            RoutingError = routingError,
            AppPayload = payload,
        };
    }

    // RouteDiscovery (TRACEROUTE_APP): 1 = route (repeated fixed32),
    // 2 = snr_towards (repeated int32), 3 = route_back (repeated fixed32),
    // 4 = snr_back (repeated int32). Repeated scalars may arrive packed (a
    // single length-delimited field) or unpacked (one field each); handle both.
    private static MeshRouteDiscovery ParseRouteDiscovery(byte[] data)
    {
        var route = new List<uint>();
        var snrTowards = new List<int>();
        var routeBack = new List<uint>();
        var snrBack = new List<int>();
        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Len:
                    ReadPackedFixed32(rdr.ReadLengthDelimited(), route); break;
                case 1 when wt == ProtoReader.WireType.I32:
                    route.Add(rdr.ReadFixed32()); break;
                case 2 when wt == ProtoReader.WireType.Len:
                    ReadPackedVarintInt32(rdr.ReadLengthDelimited(), snrTowards); break;
                case 2 when wt == ProtoReader.WireType.Varint:
                    snrTowards.Add((int)(long)rdr.ReadVarint()); break;
                case 3 when wt == ProtoReader.WireType.Len:
                    ReadPackedFixed32(rdr.ReadLengthDelimited(), routeBack); break;
                case 3 when wt == ProtoReader.WireType.I32:
                    routeBack.Add(rdr.ReadFixed32()); break;
                case 4 when wt == ProtoReader.WireType.Len:
                    ReadPackedVarintInt32(rdr.ReadLengthDelimited(), snrBack); break;
                case 4 when wt == ProtoReader.WireType.Varint:
                    snrBack.Add((int)(long)rdr.ReadVarint()); break;
                default:
                    rdr.SkipField(wt); break;
            }
        }
        return new MeshRouteDiscovery
        {
            Route = route,
            SnrTowards = snrTowards,
            RouteBack = routeBack,
            SnrBack = snrBack,
        };
    }

    private static void ReadPackedFixed32(ReadOnlySpan<byte> span, List<uint> dst)
    {
        var r = new ProtoReader(span);
        while (!r.End) dst.Add(r.ReadFixed32());
    }

    private static void ReadPackedVarintInt32(ReadOnlySpan<byte> span, List<int> dst)
    {
        var r = new ProtoReader(span);
        while (!r.End) dst.Add((int)(long)r.ReadVarint());
    }

    // Routing: oneof variant { ... error_reason = 3 (varint) }. 0 = NONE (ACK),
    // non-zero = NAK reason. Returns 0 when no error_reason is present (an ACK
    // can serialise as an empty Routing message).
    private static int ParseRoutingError(byte[] data)
    {
        var rdr = new ProtoReader(data);
        int err = 0;
        while (rdr.TryReadTag(out int field, out var wt))
        {
            if (field == 3 && wt == ProtoReader.WireType.Varint)
                err = (int)rdr.ReadVarint();
            else
                rdr.SkipField(wt);
        }
        return err;
    }

    // User: 1=id(string) 2=long_name(string) 3=short_name(string) 5=hw_model(varint)
    //       7=role(varint) 8=public_key(bytes)
    private static MeshUser ParseUser(byte[] data)
    {
        string id = "", ln = "", sn = "";
        int hw = 0;
        int role = -1;
        byte[] pub = Array.Empty<byte>();
        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Len: id = rdr.ReadString(); break;
                case 2 when wt == ProtoReader.WireType.Len: ln = rdr.ReadString(); break;
                case 3 when wt == ProtoReader.WireType.Len: sn = rdr.ReadString(); break;
                case 5 when wt == ProtoReader.WireType.Varint: hw = (int)rdr.ReadVarint(); break;
                case 7 when wt == ProtoReader.WireType.Varint: role = (int)rdr.ReadVarint(); break;
                case 8 when wt == ProtoReader.WireType.Len: pub = rdr.ReadLengthDelimited().ToArray(); break;
                default: rdr.SkipField(wt); break;
            }
        }
        return new MeshUser
        {
            Id = id,
            LongName = ln,
            ShortName = sn,
            HwModel = hw,
            Role = RoleName(role),
            PublicKey = pub,
        };
    }

    private static string RoleName(int role) => role switch
    {
        // -1 means field 7 was absent from the wire — role unknown, keep blank.
        // 0 = CLIENT (explicit). Protobuf omits default values, so absent ≠ Client.
        0 => "Client",
        1 => "ClientMute",
        2 => "Router",
        3 => "RouterClient",
        4 => "Repeater",
        5 => "Tracker",
        6 => "Sensor",
        7 => "TAK",
        8 => "ClientHidden",
        9 => "LostAndFound",
        10 => "TakTracker",
        11 => "RouterLate",
        12 => "ClientBase",
        _ => string.Empty,
    };

    // Position: 1=latitude_i(sfixed32) 2=longitude_i(sfixed32) 3=altitude(varint)
    private static MeshPosition ParsePosition(byte[] data)
    {
        double lat = 0, lon = 0;
        int? alt = null;
        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.I32:
                    lat = (int)rdr.ReadFixed32() * 1e-7; break;
                case 2 when wt == ProtoReader.WireType.I32:
                    lon = (int)rdr.ReadFixed32() * 1e-7; break;
                case 3 when wt == ProtoReader.WireType.Varint:
                    alt = (int)(long)rdr.ReadVarint(); break;
                default: rdr.SkipField(wt); break;
            }
        }
        return new MeshPosition { Latitude = lat, Longitude = lon, AltitudeM = alt };
    }

    // Waypoint: 1=id(varint) 2=latitude_i(sfixed32) 3=longitude_i(sfixed32)
    //           4=expire(varint) 5=locked_to(varint) 6=name(string)
    //           7=description(string) 8=icon(fixed32)
    private static MeshWaypoint ParseWaypoint(byte[] data)
    {
        uint id = 0;
        double lat = 0, lon = 0;
        uint expire = 0;
        uint lockedTo = 0;
        string name = string.Empty;
        string description = string.Empty;
        uint? icon = null;

        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Varint:
                    id = (uint)rdr.ReadVarint(); break;
                case 2 when wt == ProtoReader.WireType.I32:
                    lat = (int)rdr.ReadFixed32() * 1e-7; break;
                case 3 when wt == ProtoReader.WireType.I32:
                    lon = (int)rdr.ReadFixed32() * 1e-7; break;
                case 4 when wt == ProtoReader.WireType.Varint:
                    expire = (uint)rdr.ReadVarint(); break;
                case 5 when wt == ProtoReader.WireType.Varint:
                    lockedTo = (uint)rdr.ReadVarint(); break;
                case 6 when wt == ProtoReader.WireType.Len:
                    name = rdr.ReadString(); break;
                case 7 when wt == ProtoReader.WireType.Len:
                    description = rdr.ReadString(); break;
                case 8 when wt == ProtoReader.WireType.I32:
                    icon = rdr.ReadFixed32(); break;
                default:
                    rdr.SkipField(wt); break;
            }
        }

        return new MeshWaypoint
        {
            Id = id,
            Latitude = lat,
            Longitude = lon,
            ExpireEpoch = expire,
            LockedTo = lockedTo,
            Name = name,
            Description = description,
            Icon = icon,
        };
    }

    // Telemetry: 1=time(varint) 2=device_metrics(msg) 3=environment_metrics(msg).
    // DeviceMetrics:      1=battery_level 2=voltage 3=channel_utilization
    //                     4=air_util_tx 5=uptime_seconds
    // EnvironmentMetrics: 1=temperature 2=relative_humidity 3=barometric_pressure
    //                     4=gas_resistance 7=iaq
    private static MeshTelemetry ParseTelemetry(byte[] data)
    {
        byte? batt = null; float? volt = null, chan = null, airx = null; uint? uptime = null;
        float? temp = null, hum = null, pres = null, gas = null; int? iaq = null;

        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 2 when wt == ProtoReader.WireType.Len: // device_metrics
                {
                    var sub = new ProtoReader(rdr.ReadLengthDelimited().ToArray());
                    while (sub.TryReadTag(out int f, out var swt))
                    {
                        switch (f)
                        {
                            case 1 when swt == ProtoReader.WireType.Varint:
                                batt = (byte)Math.Clamp((long)sub.ReadVarint(), 0, 255); break;
                            case 2 when swt == ProtoReader.WireType.I32: volt = sub.ReadFloat(); break;
                            case 3 when swt == ProtoReader.WireType.I32: chan = sub.ReadFloat(); break;
                            case 4 when swt == ProtoReader.WireType.I32: airx = sub.ReadFloat(); break;
                            case 5 when swt == ProtoReader.WireType.Varint:
                                uptime = (uint)sub.ReadVarint(); break;
                            default: sub.SkipField(swt); break;
                        }
                    }
                    break;
                }
                case 3 when wt == ProtoReader.WireType.Len: // environment_metrics
                {
                    var sub = new ProtoReader(rdr.ReadLengthDelimited().ToArray());
                    while (sub.TryReadTag(out int f, out var swt))
                    {
                        switch (f)
                        {
                            case 1 when swt == ProtoReader.WireType.I32: temp = sub.ReadFloat(); break;
                            case 2 when swt == ProtoReader.WireType.I32: hum = sub.ReadFloat(); break;
                            case 3 when swt == ProtoReader.WireType.I32: pres = sub.ReadFloat(); break;
                            case 4 when swt == ProtoReader.WireType.I32: gas = sub.ReadFloat(); break;
                            case 7 when swt == ProtoReader.WireType.Varint:
                                iaq = (int)sub.ReadVarint(); break;
                            default: sub.SkipField(swt); break;
                        }
                    }
                    break;
                }
                default: rdr.SkipField(wt); break;
            }
        }

        return new MeshTelemetry
        {
            BatteryLevel = batt,
            Voltage = volt,
            ChannelUtilization = chan,
            AirUtilTx = airx,
            UptimeSeconds = uptime,
            TemperatureC = temp,
            RelativeHumidityPct = hum,
            BarometricPressureHpa = pres,
            GasResistanceMohm = gas,
            Iaq = iaq,
        };
    }

    // NeighborInfo: 1=node_id(varint) 2=last_sent_by_id(varint)
    //               3=node_broadcast_interval_secs(varint)
    //               4=neighbors(repeated message: 1=node_id(varint) 2=snr(float/I32))
    private static MeshNeighborInfo ParseNeighborInfo(byte[] data)
    {
        uint nodeId = 0, lastSentById = 0, broadcastInterval = 0;
        var neighbors = new List<MeshNeighborEntry>();
        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Varint:
                    nodeId = (uint)rdr.ReadVarint(); break;
                case 2 when wt == ProtoReader.WireType.Varint:
                    lastSentById = (uint)rdr.ReadVarint(); break;
                case 3 when wt == ProtoReader.WireType.Varint:
                    broadcastInterval = (uint)rdr.ReadVarint(); break;
                case 4 when wt == ProtoReader.WireType.Len:
                {
                    var sub = new ProtoReader(rdr.ReadLengthDelimited().ToArray());
                    uint nId = 0; float snr = 0;
                    while (sub.TryReadTag(out int f, out var swt))
                    {
                        switch (f)
                        {
                            case 1 when swt == ProtoReader.WireType.Varint:
                                nId = (uint)sub.ReadVarint(); break;
                            case 2 when swt == ProtoReader.WireType.I32:
                                snr = sub.ReadFloat(); break;
                            default: sub.SkipField(swt); break;
                        }
                    }
                    neighbors.Add(new MeshNeighborEntry { NodeId = nId, Snr = snr });
                    break;
                }
                default: rdr.SkipField(wt); break;
            }
        }
        return new MeshNeighborInfo
        {
            NodeId = nodeId,
            LastSentById = lastSentById,
            BroadcastIntervalSecs = broadcastInterval,
            Neighbors = neighbors,
        };
    }

    private static MeshStoreForward ParseStoreForward(byte[] data)
    {
        var rr = StoreForwardType.Unset;
        StoreForwardStats? stats = null;
        StoreForwardHeartbeat? heartbeat = null;
        uint? historyMessages = null, historyWindow = null;
        string? text = null;

        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Varint:
                    rr = (StoreForwardType)rdr.ReadVarint();
                    break;
                case 2 when wt == ProtoReader.WireType.Len:
                    stats = ParseStoreForwardStats(rdr.ReadLengthDelimited().ToArray());
                    break;
                case 3 when wt == ProtoReader.WireType.Len:
                    // History sub-message
                    var histSub = new ProtoReader(rdr.ReadLengthDelimited().ToArray());
                    while (histSub.TryReadTag(out int hf, out var hwt))
                    {
                        switch (hf)
                        {
                            case 1 when hwt == ProtoReader.WireType.Varint:
                                historyMessages = (uint)histSub.ReadVarint(); break;
                            case 2 when hwt == ProtoReader.WireType.Varint:
                                historyWindow = (uint)histSub.ReadVarint(); break;
                            default: histSub.SkipField(hwt); break;
                        }
                    }
                    break;
                case 4 when wt == ProtoReader.WireType.Len:
                    heartbeat = ParseStoreForwardHeartbeat(rdr.ReadLengthDelimited().ToArray());
                    break;
                case 5 when wt == ProtoReader.WireType.Len:
                    text = Encoding.UTF8.GetString(rdr.ReadLengthDelimited().ToArray());
                    break;
                default:
                    rdr.SkipField(wt);
                    break;
            }
        }

        return new MeshStoreForward
        {
            Type = rr,
            Stats = stats,
            Heartbeat = heartbeat,
            HistoryMessages = historyMessages,
            HistoryWindow = historyWindow,
            Text = text,
        };
    }

    private static StoreForwardStats ParseStoreForwardStats(byte[] data)
    {
        uint messagesTotal = 0, messagesSaved = 0, messagesMax = 0;
        uint upTime = 0, requests = 0, requestsHistory = 0;
        bool heartbeatEnabled = false;
        uint returnMax = 0, returnWindow = 0;

        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Varint: messagesTotal = (uint)rdr.ReadVarint(); break;
                case 2 when wt == ProtoReader.WireType.Varint: messagesSaved = (uint)rdr.ReadVarint(); break;
                case 3 when wt == ProtoReader.WireType.Varint: messagesMax = (uint)rdr.ReadVarint(); break;
                case 4 when wt == ProtoReader.WireType.Varint: upTime = (uint)rdr.ReadVarint(); break;
                case 5 when wt == ProtoReader.WireType.Varint: requests = (uint)rdr.ReadVarint(); break;
                case 6 when wt == ProtoReader.WireType.Varint: requestsHistory = (uint)rdr.ReadVarint(); break;
                case 7 when wt == ProtoReader.WireType.Varint: heartbeatEnabled = rdr.ReadVarint() != 0; break;
                case 8 when wt == ProtoReader.WireType.Varint: returnMax = (uint)rdr.ReadVarint(); break;
                case 9 when wt == ProtoReader.WireType.Varint: returnWindow = (uint)rdr.ReadVarint(); break;
                default: rdr.SkipField(wt); break;
            }
        }

        return new StoreForwardStats
        {
            MessagesTotal = messagesTotal,
            MessagesSaved = messagesSaved,
            MessagesMax = messagesMax,
            UpTimeSeconds = upTime,
            Requests = requests,
            RequestsHistory = requestsHistory,
            HeartbeatEnabled = heartbeatEnabled,
            ReturnMax = returnMax,
            ReturnWindowMinutes = returnWindow,
        };
    }

    private static StoreForwardHeartbeat ParseStoreForwardHeartbeat(byte[] data)
    {
        uint period = 0;
        bool secondary = false;

        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Varint: period = (uint)rdr.ReadVarint(); break;
                case 2 when wt == ProtoReader.WireType.Varint: secondary = rdr.ReadVarint() != 0; break;
                default: rdr.SkipField(wt); break;
            }
        }

        return new StoreForwardHeartbeat
        {
            PeriodSeconds = period,
            IsSecondary = secondary,
        };
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            var dec = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                       throwOnInvalidBytes: true);
            dec.GetString(bytes);
            return true;
        }
        catch { return false; }
    }
}
