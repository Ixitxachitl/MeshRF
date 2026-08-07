// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace MeshRF.Mqtt;

/// <summary>
/// Pure, testable logic for the MQTT uplink/downlink bridge: firmware-matching
/// default values, topic construction, and the uplink/downlink gating
/// decisions. Mirrors Meshtastic firmware's <c>src/mqtt/MQTT.cpp</c> and
/// <c>src/mesh/Default.h</c>. All I/O (the actual MQTT connection) lives in
/// MeshRF.App's <c>MqttBridge</c>; this class has none.
/// </summary>
public static class MqttPolicy
{
    // Firmware src/mesh/Default.h default_mqtt_*.
    public const string DefaultAddress = "mqtt.meshtastic.org";
    public const string DefaultUsername = "meshdev";
    public const string DefaultPassword = "large4cats";
    public const string DefaultRootTopic = "msh";
    public const bool DefaultEncryptionEnabled = true;
    public const bool DefaultTlsEnabled = false;

    // Firmware PubSubConfig::defaultPort / defaultPortTls.
    public const int DefaultPort = 1883;
    public const int DefaultTlsPort = 8883;

    /// <summary>Channel id used on the wire for PKC direct messages, which
    /// have no channel PSK / channel name to key the topic on.</summary>
    public const string PkiChannelId = "PKI";

    /// <summary>Meshtastic HOP_MAX — downlinked packets with a hop_limit or
    /// hop_start above this are rejected as malformed.</summary>
    public const int HopMax = 7;

    /// <summary>Split a "host" or "host:port" address into its parts. A
    /// missing/invalid port yields null (caller supplies the default).</summary>
    public static (string Host, int? Port) ParseHostAndPort(string? address)
    {
        var s = (address ?? string.Empty).Trim();
        int delim = s.LastIndexOf(':');
        if (delim <= 0) return (s, null);

        var hostPart = s[..delim];
        var portPart = s[(delim + 1)..];
        return int.TryParse(portPart, out var port) && port is > 0 and <= ushort.MaxValue
            ? (hostPart, port)
            : (s, null);
    }

    /// <summary>Firmware isDefaultServer(): empty address, or exactly the
    /// public Meshtastic broker hostname (ignoring any ":port" suffix).</summary>
    public static bool IsDefaultServer(string? address)
    {
        var (host, _) = ParseHostAndPort(address);
        return host.Length == 0 || host.Equals(DefaultAddress, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Effective host to connect to: the configured address, or the
    /// default public broker if unset.</summary>
    public static string EffectiveHost(string? address)
    {
        var (host, _) = ParseHostAndPort(address);
        return host.Length == 0 ? DefaultAddress : host;
    }

    /// <summary>Effective TCP port: an explicit ":port" in the address wins;
    /// otherwise 8883 when TLS is enabled, else 1883.</summary>
    public static int EffectivePort(string? address, bool tlsEnabled)
    {
        var (_, port) = ParseHostAndPort(address);
        return port ?? (tlsEnabled ? DefaultTlsPort : DefaultPort);
    }

    /// <summary>Effective username: the configured value, or the firmware
    /// default when unset — independent of which server is configured
    /// (matches the ModuleConfig.MQTTConfig.username doc comment).</summary>
    public static string EffectiveUsername(string? username) =>
        string.IsNullOrEmpty(username) ? DefaultUsername : username;

    /// <summary>Effective password: the configured value, or the firmware
    /// default when unset.</summary>
    public static string EffectivePassword(string? password) =>
        string.IsNullOrEmpty(password) ? DefaultPassword : password;

    /// <summary>Effective root topic: the configured value, or "msh" when unset.</summary>
    public static string EffectiveRootTopic(string? rootTopic)
    {
        var r = (rootTopic ?? string.Empty).Trim().TrimEnd('/');
        return r.Length == 0 ? DefaultRootTopic : r;
    }

    /// <summary>Firmware's cryptTopic ("&lt;root&gt;/2/e/"), the prefix for both
    /// uplink publish topics and downlink subscribe topics.</summary>
    public static string CryptTopicPrefix(string? rootTopic) =>
        $"{EffectiveRootTopic(rootTopic)}/2/e/";

    /// <summary>Topic an uplinked packet is published to:
    /// "&lt;root&gt;/2/e/&lt;channelId&gt;/&lt;gatewayNodeId&gt;".</summary>
    public static string UplinkTopic(string? rootTopic, string channelId, string gatewayNodeId) =>
        $"{CryptTopicPrefix(rootTopic)}{channelId}/{gatewayNodeId}";

    /// <summary>Topic filter subscribed to for downlink on one channel:
    /// "&lt;root&gt;/2/e/&lt;channelId&gt;/+".</summary>
    public static string DownlinkSubscribeTopic(string? rootTopic, string channelId) =>
        $"{CryptTopicPrefix(rootTopic)}{channelId}/+";

    /// <summary>Topic filter subscribed to for PKC direct messages:
    /// "&lt;root&gt;/2/e/PKI/+".</summary>
    public static string PkiDownlinkSubscribeTopic(string? rootTopic) =>
        DownlinkSubscribeTopic(rootTopic, PkiChannelId);

    /// <summary>Firmware's jsonTopic ("&lt;root&gt;/2/json/"), the prefix for the
    /// optional human-readable JSON publish/subscribe topics (independent of
    /// and parallel to the protobuf crypt topic; gated by its own
    /// json_enabled config, not encryption_enabled).</summary>
    public static string JsonTopicPrefix(string? rootTopic) =>
        $"{EffectiveRootTopic(rootTopic)}/2/json/";

    /// <summary>Topic a JSON-serialized uplinked packet is published to:
    /// "&lt;root&gt;/2/json/&lt;channelId&gt;/&lt;gatewayNodeId&gt;".</summary>
    public static string JsonUplinkTopic(string? rootTopic, string channelId, string gatewayNodeId) =>
        $"{JsonTopicPrefix(rootTopic)}{channelId}/{gatewayNodeId}";

    /// <summary>Topic filter subscribed to for JSON downlink on one channel:
    /// "&lt;root&gt;/2/json/&lt;channelId&gt;/+".</summary>
    public static string JsonDownlinkSubscribeTopic(string? rootTopic, string channelId) =>
        $"{JsonTopicPrefix(rootTopic)}{channelId}/+";

    /// <summary>Firmware Channels::mqttChannel: JSON downlink commands
    /// ("sendtext"/"sendposition") are only accepted on a channel literally
    /// named "mqtt" (case-insensitive), regardless of which channel's JSON
    /// topic they arrived on — a deliberate narrow "remote control" channel
    /// convention, not a general per-channel downlink like the crypt topic.</summary>
    public const string JsonCommandChannelName = "mqtt";

    /// <summary>Extracts the channel name segment from a JSON topic (the path
    /// component right after the "/2/json/" prefix), mirroring firmware's
    /// <c>strtok(channelName, "/")</c> parse in <c>onReceive</c>. Returns
    /// empty if the topic doesn't start with the JSON prefix.</summary>
    public static string ChannelNameFromJsonTopic(string? rootTopic, string topic)
    {
        var prefix = JsonTopicPrefix(rootTopic);
        if (string.IsNullOrEmpty(topic) || !topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var rest = topic[prefix.Length..];
        var slash = rest.IndexOf('/');
        return slash >= 0 ? rest[..slash] : rest;
    }

    /// <summary>
    /// Firmware isValidJsonEnvelope: a JSON downlink command is only accepted
    /// if its "from" matches our own node (this is a "command my own node to
    /// send" mechanism, not a general injection path — unlike the crypt
    /// topic, there is no channel PSK backing a JSON command's authenticity)
    /// and it doesn't carry a "sender" tag matching our own node id (which
    /// would mean it's an echo of something we ourselves published as JSON).
    /// </summary>
    public static bool IsValidJsonDownlinkEnvelope(uint? fromNodeNum, string? senderNodeId, uint ourNodeNum, string ourNodeId) =>
        fromNodeNum == ourNodeNum &&
        (string.IsNullOrEmpty(senderNodeId) || !string.Equals(senderNodeId, ourNodeId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Topic periodic MapReport messages are published to:
    /// "&lt;root&gt;/2/map/" (firmware's mapTopic — no trailing channel/node
    /// segments; map reports are unauthenticated-location broadcasts, not
    /// per-channel).</summary>
    public static string MapReportTopic(string? rootTopic) =>
        $"{EffectiveRootTopic(rootTopic)}/2/map/";

    // Firmware src/mesh/Default.h / MQTT.h map-report defaults.
    public const int DefaultMapPublishIntervalSeconds = 60 * 60;
    public const int DefaultMapPositionPrecision = 14;
    public const int MinMapPositionPrecision = 12;
    public const int MaxMapPositionPrecision = 15;

    /// <summary>Firmware perhapsReportToMap(): clamps an out-of-range
    /// configured precision back to the default rather than rejecting it —
    /// values outside [12,15] are considered "obtusely large radius and
    /// privacy problematic ones" (too coarse) or nonsensical (too fine for
    /// the intended fuzzing purpose).</summary>
    public static int CoerceMapPositionPrecision(int precision) =>
        precision is >= MinMapPositionPrecision and <= MaxMapPositionPrecision
            ? precision
            : DefaultMapPositionPrecision;

    /// <summary>RFC1918/loopback/CGNAT ranges firmware treats as "private" —
    /// packets bound for a private-IP broker skip the public-server
    /// "DontMqttMeBro" opt-in check (mirrors firmware isPrivateIpAddress).</summary>
    public static bool IsPrivateHost(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var b = ip.GetAddressBytes();
        uint addr = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

        bool InRange(uint network, uint mask) => (addr & mask) == network;

        return InRange(192u << 24 | 168u << 16, 0xffff0000u) // 192.168.0.0/16
            || InRange(172u << 24 | 16u << 16, 0xfff00000u)  // 172.16.0.0/12
            || InRange(169u << 24 | 254u << 16, 0xffff0000u) // 169.254.0.0/16
            || InRange(10u << 24, 0xff000000u)                // 10.0.0.0/8
            || InRange(127u << 24 | 1u, 0xffffffffu)          // 127.0.0.1/32
            || InRange(100u << 24 | 64u << 16, 0xffc00000u);  // 100.64.0.0/10
    }

    /// <summary>Inputs to <see cref="ShouldUplink"/>, one per packet under
    /// consideration for publishing to MQTT.</summary>
    public readonly record struct UplinkContext(
        bool ViaMqtt,
        bool AnyChannelUplinkEnabled,
        bool ChannelUplinkEnabled,
        bool IsPki,
        bool IsFromUs,
        bool IsDefaultServer,
        bool ServerIsPrivate,
        bool HasOkToMqttBit,
        bool OkToMqtt,
        bool IsRangeTestOrDetectionSensorPort);

    /// <summary>
    /// Should this packet be published to MQTT at all? Mirrors firmware
    /// MQTT::onSend's gating (not the encrypted-vs-decoded payload choice,
    /// which the caller handles separately via encryption_enabled).
    /// </summary>
    public static bool ShouldUplink(in UplinkContext ctx)
    {
        // Never re-publish something that arrived via MQTT in the first place.
        if (ctx.ViaMqtt) return false;

        // No channel has uplink turned on anywhere — nothing to do.
        if (!ctx.AnyChannelUplinkEnabled) return false;

        // Firmware only applies the ok_to_mqtt / noisy-port checks when the
        // packet was actually decoded via a channel PSK (its "decoded_tag"
        // variant). A PKI direct message a gateway can't decrypt — or any
        // packet on a channel whose key it doesn't hold — skips straight to
        // the uplink-enabled check below; there's no bitfield to inspect.
        if (!ctx.IsPki)
        {
            // "DontMqttMeBro": on a public server, only uplink other nodes'
            // packets if they opted in via the ok_to_mqtt bitfield. Our own
            // packets always go regardless of the flag.
            if (!ctx.IsFromUs && !ctx.ServerIsPrivate && !(ctx.HasOkToMqttBit && ctx.OkToMqtt))
                return false;

            // Keep noisy telemetry ports off the shared public broker.
            if (ctx.IsDefaultServer && ctx.IsRangeTestOrDetectionSensorPort)
                return false;
        }

        // This specific channel must have uplink enabled, unless it's a PKI
        // (direct message) packet, which uplinks whenever ANY channel does.
        if (!(ctx.ChannelUplinkEnabled || ctx.IsPki))
            return false;

        return true;
    }

    /// <summary>Inputs to <see cref="ShouldAcceptDownlink"/>, one per envelope
    /// received from the broker.</summary>
    public readonly record struct DownlinkContext(
        string? ChannelId,
        string? GatewayId,
        string OurNodeId,
        bool MatchedLocalChannelDownlinkEnabled,
        bool AnyChannelDownlinkEnabled,
        uint PacketFrom,
        uint OurNodeNum,
        int HopLimit,
        int HopStart);

    /// <summary>
    /// Should a received ServiceEnvelope be accepted and injected into the
    /// local mesh? Mirrors firmware's onReceiveProto gating (loop prevention,
    /// per-channel downlink policy, malformed-hop rejection). Does not
    /// attempt decryption — that happens afterward via the normal channel/PKC
    /// decode path, exactly like a packet received over the air.
    /// </summary>
    public static bool ShouldAcceptDownlink(in DownlinkContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.ChannelId) || string.IsNullOrEmpty(ctx.GatewayId))
            return false;

        bool isPki = ctx.ChannelId == PkiChannelId;
        if (isPki)
        {
            if (!ctx.AnyChannelDownlinkEnabled) return false;
        }
        else if (!ctx.MatchedLocalChannelDownlinkEnabled)
        {
            return false;
        }

        // Don't reprocess a packet we ourselves published to MQTT.
        if (string.Equals(ctx.GatewayId, ctx.OurNodeId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ctx.PacketFrom != 0 && ctx.PacketFrom == ctx.OurNodeNum)
            return false;

        if (ctx.HopLimit is < 0 or > HopMax) return false;
        if (ctx.HopStart is < 0 or > HopMax) return false;

        return true;
    }
}
