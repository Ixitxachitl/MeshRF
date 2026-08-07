// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class MeshDecoderTests
{
    // The real captured frame header (first 16 bytes) from a live decode.
    private static readonly byte[] CapturedHeader =
    {
        0xFF, 0xFF, 0xFF, 0xFF, // to = broadcast
        0x59, 0x4F, 0xA5, 0x4F, // from = 0x4FA54F59 (LE)
        0x26, 0x72, 0x49, 0xB9, // id   = 0xB9497226 (LE)
        0x63,                   // flags
        0x1F,                   // channel hash
        0x00,                   // next hop
        0x59,                   // relay node
    };

    [Fact]
    public void HeaderParsesLittleEndianFields()
    {
        Assert.True(MeshHeader.TryParse(CapturedHeader, out var h));
        Assert.Equal(0xFFFFFFFFu, h.To);
        Assert.Equal(0x4FA54F59u, h.From);
        Assert.Equal(0xB9497226u, h.PacketId);
        Assert.True(h.IsBroadcast);
        Assert.Equal(3, h.HopLimit);
        Assert.Equal(3, h.HopStart);
        Assert.Equal(0x1F, h.ChannelHash);
        Assert.Equal("!4fa54f59", h.FromId);
        Assert.Equal("^all", h.ToId);
    }

    [Fact]
    public void CtrIsSymmetric()
    {
        var key = ChannelConfig.DefaultPsk;
        var plain = Encoding.UTF8.GetBytes("hello mesh, this spans >16 bytes!!");
        var cipher = MeshCrypto.Ctr(plain, key, 0x12345678u, 0xABCDEF01u);
        var round = MeshCrypto.Ctr(cipher, key, 0x12345678u, 0xABCDEF01u);
        Assert.Equal(plain, round);
        Assert.NotEqual(plain, cipher);
    }

    [Fact]
    public void Ctr_InvalidKeyLength_Throws()
    {
        var plain = Encoding.UTF8.GetBytes("hi");
        var badKey = new byte[15]; // must be 16 or 32
        Assert.Throws<ArgumentException>(() => MeshCrypto.Ctr(plain, badKey, 0, 0));
    }

    [Fact]
    public void DecodesEncryptedTextMessageOnDefaultChannel()
    {
        const uint from = 0x4FA54F59u;
        const uint id = 0xB9497226u;
        const string message = "Hello from the mesh!";

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Psk = new byte[] { 0x01 }, // firmware default-key sentinel
            Role = ChannelRole.Primary,
        };

        // Build a Data protobuf: field 1 (portnum)=TEXT_MESSAGE_APP, field 2 (payload)=text.
        var text = Encoding.UTF8.GetBytes(message);
        var data = new List<byte> { 0x08, 0x01, 0x12, (byte)text.Length };
        data.AddRange(text);

        // Encrypt with the channel's effective key (CTR is symmetric).
        var cipher = MeshCrypto.Ctr(data.ToArray(), ChannelConfig.DefaultPsk, from, id);

        // Assemble the on-air frame: 16-byte header + ciphertext.
        var frame = new byte[MeshHeader.Size + cipher.Length];
        // to = broadcast
        frame[0] = frame[1] = frame[2] = frame[3] = 0xFF;
        BitConverter.GetBytes(from).CopyTo(frame, 4);
        BitConverter.GetBytes(id).CopyTo(frame, 8);
        frame[12] = 0x03;            // flags
        frame[13] = channel.Hash;    // matching channel hash hint
        cipher.CopyTo(frame, MeshHeader.Size);

        var result = MeshDecoder.Decode(frame, new[] { channel });

        Assert.NotNull(result);
        Assert.Equal(PortNum.TextMessage, result!.Port);
        Assert.Equal(message, result.Text);
        Assert.Equal(from, result.Header.From);
        Assert.Equal("LongFast", result.ChannelName);
    }

    [Fact]
    public void WrongKeyDoesNotDecode()
    {
        const uint from = 0x11223344u;
        const uint id = 0x55667788u;

        var text = Encoding.UTF8.GetBytes("secret");
        var data = new List<byte> { 0x08, 0x01, 0x12, (byte)text.Length };
        data.AddRange(text);
        var cipher = MeshCrypto.Ctr(data.ToArray(), ChannelConfig.DefaultPsk, from, id);

        var frame = new byte[MeshHeader.Size + cipher.Length];
        BitConverter.GetBytes(from).CopyTo(frame, 4);
        BitConverter.GetBytes(id).CopyTo(frame, 8);
        cipher.CopyTo(frame, MeshHeader.Size);

        // A channel with a different random key must not produce a valid decode.
        var wrong = new ChannelConfig
        {
            Index = 0,
            Name = "Other",
            Psk = ChannelConfig.NewRandomPsk(),
        };
        var result = MeshDecoder.Decode(frame, new[] { wrong });
        Assert.Null(result);
    }

    [Fact]
    public void DecodesEnvironmentTelemetry()
    {
        const uint from = 0x4FA54F59u;
        const uint id = 0xB9497226u;

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };

        // EnvironmentMetrics: 1=temperature 2=relative_humidity 3=barometric_pressure
        static byte[] F(int field, float v)
        {
            var b = new byte[5];
            b[0] = (byte)((field << 3) | 5); // wire type 5 (I32)
            BitConverter.GetBytes(v).CopyTo(b, 1);
            return b;
        }
        var env = new List<byte>();
        env.AddRange(F(1, 21.5f));   // temperature °C
        env.AddRange(F(2, 48.0f));   // humidity %
        env.AddRange(F(3, 1013.2f)); // pressure hPa

        // Telemetry: field 3 = environment_metrics (len-delimited).
        var telem = new List<byte> { (3 << 3) | 2, (byte)env.Count };
        telem.AddRange(env);

        // Data: field 1 (portnum)=TELEMETRY_APP(67), field 2 (payload).
        var data = new List<byte> { 0x08, 67, 0x12, (byte)telem.Count };
        data.AddRange(telem);

        var cipher = MeshCrypto.Ctr(data.ToArray(), ChannelConfig.DefaultPsk, from, id);
        var frame = new byte[MeshHeader.Size + cipher.Length];
        frame[0] = frame[1] = frame[2] = frame[3] = 0xFF;
        BitConverter.GetBytes(from).CopyTo(frame, 4);
        BitConverter.GetBytes(id).CopyTo(frame, 8);
        frame[12] = 0x03;
        frame[13] = channel.Hash;
        cipher.CopyTo(frame, MeshHeader.Size);

        var result = MeshDecoder.Decode(frame, new[] { channel });

        Assert.NotNull(result);
        Assert.Equal(PortNum.Telemetry, result!.Port);
        Assert.NotNull(result.Telemetry);
        Assert.True(result.Telemetry!.HasEnvironmentMetrics);
        Assert.Equal(21.5f, result.Telemetry.TemperatureC);
        Assert.Equal(48.0f, result.Telemetry.RelativeHumidityPct);
        Assert.Equal(1013.2f, result.Telemetry.BarometricPressureHpa);
    }

    [Fact]
    public void DecodesDeviceTelemetry()
    {
        const uint from = 0x4FA54F59u;
        const uint id = 0xB9497226u;

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };

        // DeviceMetrics: 1=battery_level(varint) 2=voltage(float) 5=uptime(varint)
        var dev = new List<byte> { 0x08, 92 };       // battery_level = 92
        dev.Add((2 << 3) | 5);                        // voltage, wire type I32
        dev.AddRange(BitConverter.GetBytes(4.05f));
        dev.Add((5 << 3) | 0);                        // uptime_seconds, varint
        dev.Add(0xC8); dev.Add(0x02);                 // varint 328

        var telem = new List<byte> { (2 << 3) | 2, (byte)dev.Count };
        telem.AddRange(dev);

        var data = new List<byte> { 0x08, 67, 0x12, (byte)telem.Count };
        data.AddRange(telem);

        var cipher = MeshCrypto.Ctr(data.ToArray(), ChannelConfig.DefaultPsk, from, id);
        var frame = new byte[MeshHeader.Size + cipher.Length];
        frame[0] = frame[1] = frame[2] = frame[3] = 0xFF;
        BitConverter.GetBytes(from).CopyTo(frame, 4);
        BitConverter.GetBytes(id).CopyTo(frame, 8);
        frame[12] = 0x03;
        frame[13] = channel.Hash;
        cipher.CopyTo(frame, MeshHeader.Size);

        var result = MeshDecoder.Decode(frame, new[] { channel });

        Assert.NotNull(result);
        Assert.Equal(PortNum.Telemetry, result!.Port);
        Assert.NotNull(result.Telemetry);
        Assert.True(result.Telemetry!.HasDeviceMetrics);
        Assert.Equal((byte)92, result.Telemetry.BatteryLevel);
        Assert.Equal(4.05f, result.Telemetry.Voltage);
        Assert.Equal(328u, result.Telemetry.UptimeSeconds);
    }

    [Fact]
    public void DecodesDataField10MetadataBytes()
    {
        const uint from = 0xF1B87EB8u;
        const uint id = 0xD636B378u;

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "MediumFast",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };

        // Telemetry payload (27 B) from the captured packet.
        byte[] appPayload = Convert.FromHexString("0D0803446A1214085B150E2D82401D00000000255CFC4F3C28F90A");

        // Example field 10 blob (64 B) from captured long duplicate.
        byte[] field10 = Convert.FromHexString("6D0870E179D0291051182175C4483EE06CFCF68782B5A9B2F8EE61BC0D4C8021D256BB405F7346181D2BCC7725F838F67622D3629CC1BF5326CB43FFE181220E");

        var data = new List<byte>
        {
            0x08, 67,                     // field 1: portnum=Telemetry
            0x12, (byte)appPayload.Length // field 2: payload
        };
        data.AddRange(appPayload);
        data.Add(0x48); // field 9, varint
        data.Add(0x01); // ok_to_mqtt = true
        data.Add(0x52); // field 10, len
        data.Add((byte)field10.Length);
        data.AddRange(field10);

        var cipher = MeshCrypto.Ctr(data.ToArray(), ChannelConfig.DefaultPsk, from, id);
        var frame = new byte[MeshHeader.Size + cipher.Length];
        frame[0] = frame[1] = frame[2] = frame[3] = 0xFF;
        BitConverter.GetBytes(from).CopyTo(frame, 4);
        BitConverter.GetBytes(id).CopyTo(frame, 8);
        frame[12] = 0xE7;         // hopStart=7, hopLimit=7
        frame[13] = channel.Hash; // 0x1F for MediumFast/default key
        cipher.CopyTo(frame, MeshHeader.Size);

        var result = MeshDecoder.Decode(frame, new[] { channel });

        Assert.NotNull(result);
        Assert.Equal(PortNum.Telemetry, result!.Port);
        Assert.Equal(appPayload, result.AppPayload);
        Assert.True(result.OkToMqtt);
        Assert.Equal(field10, result.DataField10);
    }

    [Fact]
    public void DecodesNodeInfoRoleRouter()
    {
        const uint from = 0x1A2B3C4Du;
        const uint id   = 0x00000001u;

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };

        var frame = MeshEncoder.EncodeNodeInfo(channel, from, id,
            longName: "Test Router", shortName: "TR",
            hwModel: 43, role: 2 /* ROUTER */);

        var result = MeshDecoder.Decode(frame, new[] { channel });

        Assert.NotNull(result);
        Assert.Equal(PortNum.NodeInfo, result!.Port);
        Assert.NotNull(result.User);
        Assert.Equal("Router", result.User!.Role);
        Assert.Equal("Test Router", result.User.LongName);
    }

    // ---- Malformed / truncated input (untrusted radio data boundary) ----

    [Fact]
    public void Decode_EmptyFrame_ReturnsNull()
    {
        var channel = new ChannelConfig { Index = 0, Psk = new byte[] { 0x01 } };
        var result = MeshDecoder.Decode(ReadOnlySpan<byte>.Empty, new[] { channel });
        Assert.Null(result);
    }

    [Fact]
    public void Decode_TooShortForHeader_ReturnsNull()
    {
        var channel = new ChannelConfig { Index = 0, Psk = new byte[] { 0x01 } };
        var frame = new byte[10]; // MeshHeader.Size is 16
        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.Null(result);
    }

    [Fact]
    public void Decode_HeaderOnlyNoCiphertext_ReturnsNull()
    {
        var channel = new ChannelConfig { Index = 0, Psk = new byte[] { 0x01 } };
        var frame = new byte[MeshHeader.Size]; // exactly 16 bytes, nothing to decrypt
        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.Null(result);
    }

    [Fact]
    public void Decode_RandomGarbageCiphertext_NeverThrows()
    {
        // Fuzz-lite regression guard: feed random bytes (not a valid encrypted
        // protobuf) behind a header whose channel-hash hint matches a real
        // channel, so the full decrypt+parse path runs. This is the
        // untrusted-radio-input boundary (MeshHeader.TryParse, ProtoReader,
        // IsPlausible) — it must reject cleanly (null) and never throw,
        // regardless of what garbage bytes happen to decrypt to.
        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };

        var rng = new Random(12345);
        for (int trial = 0; trial < 50; trial++)
        {
            var frame = new byte[MeshHeader.Size + rng.Next(1, 64)];
            rng.NextBytes(frame);
            frame[13] = channel.Hash; // force the channel-hash hint to match

            var ex = Record.Exception(() => MeshDecoder.Decode(frame, new[] { channel }));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void DecodesNodeInfoRoleAbsentIsEmpty()
    {
        // role=0 → encoder omits field 7 → MeshDecoder returns "" (absent from wire).
        // The UI layer (MainViewModel) promotes "" → "Client" on NodeInfo receive,
        // but the decoder itself stays honest about what was on the wire.
        const uint from = 0x1A2B3C4Du;
        const uint id   = 0x00000002u;

        var channel = new ChannelConfig
        {
            Index = 0,
            Name = "LongFast",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Primary,
        };

        var frame = MeshEncoder.EncodeNodeInfo(channel, from, id,
            longName: "Test Client", shortName: "TC",
            hwModel: 43, role: 0 /* CLIENT — field 7 omitted */);

        var result = MeshDecoder.Decode(frame, new[] { channel });

        Assert.NotNull(result);
        Assert.Equal(PortNum.NodeInfo, result!.Port);
        Assert.NotNull(result.User);
        Assert.Equal(string.Empty, result.User!.Role);
    }
}
