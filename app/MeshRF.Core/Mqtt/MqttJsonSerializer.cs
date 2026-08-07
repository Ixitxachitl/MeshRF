// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshRF.Mesh;

namespace MeshRF.Mqtt;

/// <summary>
/// Builds and parses the optional human-readable JSON representation of mesh
/// packets published to/from firmware's <c>json_enabled</c> MQTT topic
/// ("&lt;root&gt;/2/json/..."), independent of and parallel to the protobuf
/// crypt topic. Mirrors firmware's <c>MeshPacketSerializer::JsonSerialize</c>
/// (publish) and <c>MQTT::onReceiveJson</c> (downlink command parsing).
/// </summary>
public static class MqttJsonSerializer
{
    /// <summary>A validated "sendtext"/"sendposition" downlink command,
    /// mirroring firmware's onReceiveJson. Only the fields that type actually
    /// uses are populated.</summary>
    public sealed record DownlinkCommand(
        string Type,
        string? Text,
        int? LatitudeI,
        int? LongitudeI,
        int? Altitude,
        uint? Channel,
        uint? To,
        byte? HopLimit);

    /// <summary>
    /// Firmware MeshPacketSerializer::JsonSerialize: envelope fields plus a
    /// per-port "payload" object using firmware's exact field names. Only
    /// called for a successfully-decoded packet (mirrors
    /// <c>which_payload_variant == decoded_tag</c>); ports MeshRF doesn't
    /// have a JSON mapping for still get the envelope with an empty "type"
    /// and no payload, exactly like firmware's <c>default: break;</c>.
    /// </summary>
    public static string Serialize(MeshDecodeResult result, MeshHeader header, string senderNodeId,
                                   uint channelIndex, uint rxTimeEpoch, int? rssi, float? snrDb)
    {
        var (type, payload) = BuildPayload(result);

        var obj = new JsonObject
        {
            ["id"] = header.PacketId,
            ["timestamp"] = rxTimeEpoch,
            ["to"] = header.To,
            ["from"] = header.From,
            ["channel"] = channelIndex,
            ["type"] = type,
            ["sender"] = senderNodeId,
        };
        if (rssi is int r && r != 0) obj["rssi"] = r;
        if (snrDb is float s && s != 0) obj["snr"] = s;

        int hopsAway = header.HopStart >= header.HopLimit ? header.HopStart - header.HopLimit : 0;
        obj["hops_away"] = hopsAway;
        obj["hop_start"] = header.HopStart;

        if (payload is not null) obj["payload"] = payload;

        return obj.ToJsonString();
    }

    private static (string Type, JsonNode? Payload) BuildPayload(MeshDecodeResult result)
    {
        switch (result.Port)
        {
            case PortNum.TextMessage when result.Text is not null:
            {
                // Firmware tries to parse the text itself as JSON first —
                // if the sender's text message payload happens to be valid
                // JSON, that becomes the payload object directly instead of
                // being wrapped under "text".
                try
                {
                    var parsed = JsonNode.Parse(result.Text);
                    if (parsed is not null) return ("text", parsed);
                }
                catch (JsonException) { /* not JSON — fall through */ }
                return ("text", new JsonObject { ["text"] = result.Text });
            }

            case PortNum.DetectionSensor:
                return ("detection", new JsonObject { ["text"] = Encoding.UTF8.GetString(result.AppPayload) });

            case PortNum.NodeInfo when result.User is not null:
                return ("nodeinfo", new JsonObject
                {
                    ["id"] = result.User.Id,
                    ["longname"] = result.User.LongName,
                    ["shortname"] = result.User.ShortName,
                    ["hardware"] = result.User.HwModel,
                    ["role"] = int.TryParse(result.User.Role, out var roleNum) ? roleNum : 0,
                });

            case PortNum.Position when result.Position is not null:
            {
                var p = result.Position;
                var payload = new JsonObject
                {
                    ["latitude_i"] = (int)Math.Round(p.Latitude / 1e-7),
                    ["longitude_i"] = (int)Math.Round(p.Longitude / 1e-7),
                };
                if (p.AltitudeM is int alt && alt != 0) payload["altitude"] = alt;
                return ("position", payload);
            }

            case PortNum.Waypoint when result.Waypoint is not null:
            {
                var w = result.Waypoint;
                return ("waypoint", new JsonObject
                {
                    ["id"] = w.Id,
                    ["name"] = w.Name,
                    ["description"] = w.Description,
                    ["expire"] = w.ExpireEpoch,
                    ["locked_to"] = w.LockedTo,
                    ["latitude_i"] = (int)Math.Round(w.Latitude / 1e-7),
                    ["longitude_i"] = (int)Math.Round(w.Longitude / 1e-7),
                });
            }

            case PortNum.Telemetry when result.Telemetry is not null:
                return BuildTelemetryPayload(result.Telemetry);

            default:
                return (string.Empty, null);
        }
    }

    private static (string Type, JsonNode? Payload) BuildTelemetryPayload(MeshTelemetry t)
    {
        if (t.HasDeviceMetrics)
        {
            var payload = new JsonObject();
            if (t.BatteryLevel is byte bl) payload["battery_level"] = bl;
            if (t.Voltage is float v) payload["voltage"] = v;
            if (t.ChannelUtilization is float cu) payload["channel_utilization"] = cu;
            if (t.AirUtilTx is float au) payload["air_util_tx"] = au;
            if (t.UptimeSeconds is uint up) payload["uptime_seconds"] = up;
            return ("telemetry", payload);
        }
        if (t.HasEnvironmentMetrics)
        {
            var payload = new JsonObject();
            if (t.TemperatureC is float temp) payload["temperature"] = temp;
            if (t.RelativeHumidityPct is float rh) payload["relative_humidity"] = rh;
            if (t.BarometricPressureHpa is float bp) payload["barometric_pressure"] = bp;
            if (t.GasResistanceMohm is float gr) payload["gas_resistance"] = gr;
            if (t.Iaq is int iaq) payload["iaq"] = iaq;
            return ("telemetry", payload);
        }
        if (t.HasAirQualityMetrics)
        {
            var payload = new JsonObject();
            if (t.Pm10Standard is uint pm10) payload["pm10"] = pm10;
            if (t.Pm25Standard is uint pm25) payload["pm25"] = pm25;
            if (t.Pm100Standard is uint pm100) payload["pm100"] = pm100;
            return ("telemetry", payload);
        }
        if (t.HasPowerMetrics)
        {
            var payload = new JsonObject();
            if (t.Ch1VoltageV is float v1) payload["voltage_ch1"] = v1;
            if (t.Ch1CurrentMa is float c1) payload["current_ch1"] = c1;
            if (t.Ch2VoltageV is float v2) payload["voltage_ch2"] = v2;
            if (t.Ch2CurrentMa is float c2) payload["current_ch2"] = c2;
            if (t.Ch3VoltageV is float v3) payload["voltage_ch3"] = v3;
            if (t.Ch3CurrentMa is float c3) payload["current_ch3"] = c3;
            return ("telemetry", payload);
        }
        return ("telemetry", new JsonObject());
    }

    /// <summary>
    /// Firmware onReceiveJson (isValidJsonEnvelope + type dispatch, combined):
    /// validates that "from" equals <paramref name="ourNodeNum"/> and
    /// "sender" (if present) isn't <paramref name="ourNodeId"/> — see
    /// <see cref="MqttPolicy.IsValidJsonDownlinkEnvelope"/> — then parses one
    /// of the two commands firmware accepts on downlink. Returns null for
    /// anything else (unparseable JSON, failed validation, missing required
    /// fields, or an unsupported "type").
    /// </summary>
    public static DownlinkCommand? TryParseDownlinkCommand(string json, uint ourNodeNum, string ourNodeId)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is not JsonObject obj) return null;

        if (!obj.TryGetPropertyValue("from", out var fromNode) || fromNode is null || !TryGetUInt(fromNode, out var fromNum))
            return null;
        string? sender = obj.TryGetPropertyValue("sender", out var senderNode) && senderNode?.GetValueKind() == JsonValueKind.String
            ? senderNode.GetValue<string>() : null;
        if (!MqttPolicy.IsValidJsonDownlinkEnvelope(fromNum, sender, ourNodeNum, ourNodeId))
            return null;
        if (!obj.TryGetPropertyValue("type", out var typeNode) || typeNode?.GetValueKind() != JsonValueKind.String)
            return null;
        if (!obj.TryGetPropertyValue("payload", out var payloadNode) || payloadNode is null)
            return null;

        uint? channel = obj.TryGetPropertyValue("channel", out var chNode) && TryGetUInt(chNode, out var ch) ? ch : null;
        uint? to = obj.TryGetPropertyValue("to", out var toNode) && TryGetUInt(toNode, out var t) ? t : null;
        byte? hopLimit = obj.TryGetPropertyValue("hopLimit", out var hlNode) && TryGetUInt(hlNode, out var hl)
            ? (byte)Math.Clamp(hl, 0u, 7u) : null;

        var type = typeNode!.GetValue<string>();
        if (type == "sendtext" && payloadNode.GetValueKind() == JsonValueKind.String)
        {
            return new DownlinkCommand("sendtext", payloadNode.GetValue<string>(),
                null, null, null, channel, to, hopLimit);
        }
        if (type == "sendposition" && payloadNode is JsonObject posit)
        {
            int? lat = posit.TryGetPropertyValue("latitude_i", out var latNode) && TryGetInt(latNode, out var la) ? la : null;
            int? lon = posit.TryGetPropertyValue("longitude_i", out var lonNode) && TryGetInt(lonNode, out var lo) ? lo : null;
            int? alt = posit.TryGetPropertyValue("altitude", out var altNode) && TryGetInt(altNode, out var al) ? al : null;
            return new DownlinkCommand("sendposition", null, lat, lon, alt, channel, to, hopLimit);
        }

        return null;
    }

    private static bool TryGetUInt(JsonNode? node, out uint value)
    {
        value = 0;
        if (node?.GetValueKind() != JsonValueKind.Number) return false;
        try { value = node.GetValue<uint>(); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool TryGetInt(JsonNode? node, out int value)
    {
        value = 0;
        if (node?.GetValueKind() != JsonValueKind.Number) return false;
        try { value = node.GetValue<int>(); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }
}
