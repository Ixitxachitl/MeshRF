// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshtasticRF.Channels;

namespace MeshtasticRF.Mesh;

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

    /// <summary>Parsed Telemetry (TELEMETRY_APP); null otherwise.</summary>
    public MeshTelemetry? Telemetry { get; init; }

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
}

/// <summary>Subset of the Meshtastic <c>Position</c> protobuf.</summary>
public sealed class MeshPosition
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int? AltitudeM { get; init; }
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
/// Turns a decoded LoRa frame (16-byte header + encrypted payload) into a
/// structured <see cref="MeshDecodeResult"/> by trying each known channel's
/// PSK, AES-CTR decrypting, and parsing the inner protobufs.
/// </summary>
public static class MeshDecoder
{
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

        // Prefer channels whose 1-byte hash hint matches, then fall back to the
        // rest (the hint can collide, and some senders send hash 0).
        var ordered = channels
            .OrderByDescending(c => c.Hash == header.ChannelHash)
            .ToList();

        foreach (var ch in ordered)
        {
            var key = ch.EffectiveKey;
            if (key.Length != 16 && key.Length != 32) continue;

            byte[] plain;
            try { plain = MeshCrypto.Ctr(cipher, key, header.From, header.PacketId); }
            catch { continue; }

            if (TryParseData(plain, out var port, out var appPayload) &&
                IsPlausible(port, appPayload))
            {
                return Build(header, ch.Name, port, appPayload);
            }
        }
        return null;
    }

    /// <summary>
    /// Diagnostic helper for when <see cref="Decode"/> fails: brute-force every
    /// single-byte "default key family" PSK (the quick-channel keys, e.g.
    /// <c>AQ==</c>, <c>TA==</c>) and the plain default key. Returns the PSK
    /// index (1..255) that produces a plausible decode, or null if none do.
    /// An index of 1 is the well-known default key.
    /// </summary>
    public static int? DiscoverDefaultKeyIndex(ReadOnlySpan<byte> frame)
    {
        if (!MeshHeader.TryParse(frame, out var header)) return null;
        if (frame.Length <= MeshHeader.Size) return null;

        var cipher = frame.Slice(MeshHeader.Size).ToArray();
        var key = (byte[])ChannelConfig.DefaultPsk.Clone();

        for (int index = 1; index <= 255; index++)
        {
            key[^1] = (byte)(ChannelConfig.DefaultPsk[^1] + index - 1);
            byte[] plain;
            try { plain = MeshCrypto.Ctr(cipher, key, header.From, header.PacketId); }
            catch { continue; }

            if (TryParseData(plain, out var port, out var appPayload) &&
                IsPlausible(port, appPayload))
            {
                return index;
            }
        }
        return null;
    }

    // -- Data protobuf: field 1 = portnum (varint), field 2 = payload (bytes) --
    private static bool TryParseData(byte[] data, out PortNum port, out byte[] payload)
    {
        port = PortNum.Unknown;
        payload = Array.Empty<byte>();
        var rdr = new ProtoReader(data);
        bool sawPort = false;
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Varint:
                    port = (PortNum)rdr.ReadVarint();
                    sawPort = true;
                    break;
                case 2 when wt == ProtoReader.WireType.Len:
                    payload = rdr.ReadLengthDelimited().ToArray();
                    break;
                default:
                    rdr.SkipField(wt);
                    break;
            }
        }
        // A valid Data message that consumed the whole buffer cleanly.
        return sawPort && rdr.End;
    }

    // Reject obviously-wrong decrypts (wrong key -> garbage portnum / payload).
    private static bool IsPlausible(PortNum port, byte[] payload)
    {
        if (!Enum.IsDefined(typeof(PortNum), port)) return false;
        if (port == PortNum.TextMessage)
            return payload.Length > 0 && IsValidUtf8(payload);
        return true;
    }

    private static MeshDecodeResult Build(MeshHeader header, string channel,
                                          PortNum port, byte[] payload)
    {
        string? text = null;
        MeshUser? user = null;
        MeshPosition? pos = null;
        MeshTelemetry? telem = null;

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
            case PortNum.Telemetry:
                telem = ParseTelemetry(payload);
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
            Telemetry = telem,
            AppPayload = payload,
        };
    }

    // User: 1=id(string) 2=long_name(string) 3=short_name(string) 5=hw_model(varint)
    private static MeshUser ParseUser(byte[] data)
    {
        string id = "", ln = "", sn = "";
        int hw = 0;
        var rdr = new ProtoReader(data);
        while (rdr.TryReadTag(out int field, out var wt))
        {
            switch (field)
            {
                case 1 when wt == ProtoReader.WireType.Len: id = rdr.ReadString(); break;
                case 2 when wt == ProtoReader.WireType.Len: ln = rdr.ReadString(); break;
                case 3 when wt == ProtoReader.WireType.Len: sn = rdr.ReadString(); break;
                case 5 when wt == ProtoReader.WireType.Varint: hw = (int)rdr.ReadVarint(); break;
                default: rdr.SkipField(wt); break;
            }
        }
        return new MeshUser { Id = id, LongName = ln, ShortName = sn, HwModel = hw };
    }

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
