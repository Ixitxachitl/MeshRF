// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Mqtt;
using Xunit;

namespace MeshRF.Tests;

public class MqttJsonSerializerTests
{
    private static readonly MeshHeader Header = new()
    {
        To = 0xFFFFFFFFu,
        From = 0x4fa54f59u,
        PacketId = 0x1234,
        Flags = 3, // hop_limit=3, hop_start=0
        ChannelHash = 0x8A,
    };

    // ---- Publish (Serialize) ----

    [Fact]
    public void Serialize_TextMessage_WrapsPlaintextUnderTextKey()
    {
        var result = new MeshDecodeResult { Port = PortNum.TextMessage, Text = "hello mesh" };
        var json = MqttJsonSerializer.Serialize(result, Header, "!aabbccdd",
            channelIndex: 0, rxTimeEpoch: 1000, rssi: -90, snrDb: 5.5f);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("text", root.GetProperty("type").GetString());
        Assert.Equal("hello mesh", root.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal(0x4fa54f59u, root.GetProperty("from").GetUInt32());
        Assert.Equal("!aabbccdd", root.GetProperty("sender").GetString());
        Assert.Equal(-90, root.GetProperty("rssi").GetInt32());
    }

    [Fact]
    public void Serialize_TextMessage_UsesEmbeddedJsonDirectlyWhenPayloadIsJson()
    {
        var result = new MeshDecodeResult { Port = PortNum.TextMessage, Text = "{\"custom\":42}" };
        var json = MqttJsonSerializer.Serialize(result, Header, "!aabbccdd",
            channelIndex: 0, rxTimeEpoch: 1000, rssi: null, snrDb: null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("payload").GetProperty("custom").GetInt32());
    }

    [Fact]
    public void Serialize_Position_ConvertsBackToFixedPointIntegers()
    {
        var result = new MeshDecodeResult
        {
            Port = PortNum.Position,
            Position = new MeshPosition { Latitude = 45.0, Longitude = -122.0, AltitudeM = 50 },
        };
        var json = MqttJsonSerializer.Serialize(result, Header, "!aabbccdd",
            channelIndex: 0, rxTimeEpoch: 1000, rssi: null, snrDb: null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal(450000000, payload.GetProperty("latitude_i").GetInt32());
        Assert.Equal(-1220000000, payload.GetProperty("longitude_i").GetInt32());
        Assert.Equal(50, payload.GetProperty("altitude").GetInt32());
    }

    [Fact]
    public void Serialize_UnmappedPort_EmitsEmptyTypeAndNoPayload()
    {
        var result = new MeshDecodeResult { Port = PortNum.Routing };
        var json = MqttJsonSerializer.Serialize(result, Header, "!aabbccdd",
            channelIndex: 0, rxTimeEpoch: 1000, rssi: null, snrDb: null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("payload", out _));
    }

    // ---- Downlink (TryParseDownlinkCommand) ----

    [Fact]
    public void TryParseDownlinkCommand_SendText_ParsesTextAndOverrides()
    {
        var json = """{"from":171,"type":"sendtext","payload":"hi","channel":1,"to":42,"hopLimit":5}""";
        var cmd = MqttJsonSerializer.TryParseDownlinkCommand(json, ourNodeNum: 171, ourNodeId: "!aabbccdd");

        Assert.NotNull(cmd);
        Assert.Equal("sendtext", cmd!.Type);
        Assert.Equal("hi", cmd.Text);
        Assert.Equal(1u, cmd.Channel);
        Assert.Equal(42u, cmd.To);
        Assert.Equal((byte)5, cmd.HopLimit);
    }

    [Fact]
    public void TryParseDownlinkCommand_SendPosition_ParsesNestedPayload()
    {
        var json = """{"from":171,"type":"sendposition","payload":{"latitude_i":450000000,"longitude_i":-1220000000,"altitude":50}}""";
        var cmd = MqttJsonSerializer.TryParseDownlinkCommand(json, ourNodeNum: 171, ourNodeId: "!aabbccdd");

        Assert.NotNull(cmd);
        Assert.Equal("sendposition", cmd!.Type);
        Assert.Equal(450000000, cmd.LatitudeI);
        Assert.Equal(-1220000000, cmd.LongitudeI);
        Assert.Equal(50, cmd.Altitude);
    }

    [Fact]
    public void TryParseDownlinkCommand_RejectsWhenFromIsNotOurOwnNode()
    {
        var json = """{"from":999,"type":"sendtext","payload":"hi"}""";
        Assert.Null(MqttJsonSerializer.TryParseDownlinkCommand(json, ourNodeNum: 171, ourNodeId: "!aabbccdd"));
    }

    [Fact]
    public void TryParseDownlinkCommand_RejectsOwnEchoedPublish()
    {
        var json = """{"from":171,"sender":"!aabbccdd","type":"sendtext","payload":"hi"}""";
        Assert.Null(MqttJsonSerializer.TryParseDownlinkCommand(json, ourNodeNum: 171, ourNodeId: "!aabbccdd"));
    }

    [Fact]
    public void TryParseDownlinkCommand_RejectsUnsupportedType()
    {
        var json = """{"from":171,"type":"reboot","payload":"now"}""";
        Assert.Null(MqttJsonSerializer.TryParseDownlinkCommand(json, ourNodeNum: 171, ourNodeId: "!aabbccdd"));
    }

    [Fact]
    public void TryParseDownlinkCommand_RejectsMalformedJson()
    {
        Assert.Null(MqttJsonSerializer.TryParseDownlinkCommand("not json", ourNodeNum: 171, ourNodeId: "!aabbccdd"));
    }
}
