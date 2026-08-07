// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mqtt;
using Xunit;

namespace MeshRF.Tests;

public class MqttPolicyTests
{
    [Theory]
    [InlineData(null, "", null)]
    [InlineData("", "", null)]
    [InlineData("mqtt.meshtastic.org", "mqtt.meshtastic.org", null)]
    [InlineData("MQTT.MESHTASTIC.ORG", "mqtt.meshtastic.org", null)]
    [InlineData("broker.example.com", "broker.example.com", null)]
    [InlineData("broker.example.com:8000", "broker.example.com", 8000)]
    [InlineData("192.168.1.5:1884", "192.168.1.5", 1884)]
    public void ParseHostAndPort_SplitsCorrectly(string? address, string expectedHost, int? expectedPort)
    {
        var (host, port) = MqttPolicy.ParseHostAndPort(address);
        Assert.Equal(expectedHost, host, ignoreCase: true);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("mqtt.meshtastic.org", true)]
    [InlineData("mqtt.meshtastic.org:1883", true)]
    [InlineData("MQTT.Meshtastic.Org", true)]
    [InlineData("broker.example.com", false)]
    public void IsDefaultServer_MatchesFirmwareRule(string? address, bool expected)
    {
        Assert.Equal(expected, MqttPolicy.IsDefaultServer(address));
    }

    [Fact]
    public void EffectiveHost_FallsBackToDefault()
    {
        Assert.Equal(MqttPolicy.DefaultAddress, MqttPolicy.EffectiveHost(null));
        Assert.Equal(MqttPolicy.DefaultAddress, MqttPolicy.EffectiveHost(""));
        Assert.Equal("broker.example.com", MqttPolicy.EffectiveHost("broker.example.com:1884"));
    }

    [Theory]
    [InlineData(null, false, 1883)]
    [InlineData(null, true, 8883)]
    [InlineData("broker.example.com", false, 1883)]
    [InlineData("broker.example.com", true, 8883)]
    [InlineData("broker.example.com:1884", true, 1884)] // explicit port wins over TLS default
    public void EffectivePort_UsesTlsDefaultUnlessExplicit(string? address, bool tls, int expected)
    {
        Assert.Equal(expected, MqttPolicy.EffectivePort(address, tls));
    }

    [Fact]
    public void EffectiveUsername_and_Password_FallBackToFirmwareDefaults()
    {
        Assert.Equal(MqttPolicy.DefaultUsername, MqttPolicy.EffectiveUsername(null));
        Assert.Equal(MqttPolicy.DefaultUsername, MqttPolicy.EffectiveUsername(""));
        Assert.Equal("custom", MqttPolicy.EffectiveUsername("custom"));

        Assert.Equal(MqttPolicy.DefaultPassword, MqttPolicy.EffectivePassword(null));
        Assert.Equal(MqttPolicy.DefaultPassword, MqttPolicy.EffectivePassword(""));
        Assert.Equal("hunter2", MqttPolicy.EffectivePassword("hunter2"));
    }

    [Fact]
    public void EffectiveRootTopic_FallsBackToMsh()
    {
        Assert.Equal("msh", MqttPolicy.EffectiveRootTopic(null));
        Assert.Equal("msh", MqttPolicy.EffectiveRootTopic(""));
        Assert.Equal("msh/US", MqttPolicy.EffectiveRootTopic("msh/US"));
        Assert.Equal("msh/US", MqttPolicy.EffectiveRootTopic("msh/US/")); // trailing slash trimmed
    }

    [Fact]
    public void TopicBuilders_MatchFirmwareLayout()
    {
        Assert.Equal("msh/2/e/", MqttPolicy.CryptTopicPrefix(null));
        Assert.Equal("msh/2/e/LongFast/!4fa54f59", MqttPolicy.UplinkTopic(null, "LongFast", "!4fa54f59"));
        Assert.Equal("msh/2/e/LongFast/+", MqttPolicy.DownlinkSubscribeTopic(null, "LongFast"));
        Assert.Equal("msh/2/e/PKI/+", MqttPolicy.PkiDownlinkSubscribeTopic(null));
        Assert.Equal("msh/US/2/e/PKI/+", MqttPolicy.PkiDownlinkSubscribeTopic("msh/US"));
        Assert.Equal("msh/2/map/", MqttPolicy.MapReportTopic(null));
        Assert.Equal("msh/US/2/map/", MqttPolicy.MapReportTopic("msh/US"));
        Assert.Equal("msh/2/json/", MqttPolicy.JsonTopicPrefix(null));
        Assert.Equal("msh/2/json/LongFast/!4fa54f59", MqttPolicy.JsonUplinkTopic(null, "LongFast", "!4fa54f59"));
        Assert.Equal("msh/2/json/mqtt/+", MqttPolicy.JsonDownlinkSubscribeTopic(null, "mqtt"));
    }

    [Theory]
    [InlineData(null, "msh/2/json/mqtt/+", "mqtt")]
    [InlineData(null, "msh/2/json/mqtt/!4fa54f59", "mqtt")]
    [InlineData(null, "msh/2/json/LongFast/+", "LongFast")]
    [InlineData("msh/US", "msh/US/2/json/mqtt/+", "mqtt")]
    [InlineData(null, "msh/2/e/mqtt/+", "")] // wrong prefix (crypt, not json)
    [InlineData(null, "", "")]
    public void ChannelNameFromJsonTopic_ParsesTopicSegment(string? rootTopic, string topic, string expected)
    {
        Assert.Equal(expected, MqttPolicy.ChannelNameFromJsonTopic(rootTopic, topic));
    }

    [Theory]
    [InlineData(0xABu, null, 0xABu, "!aabbccdd", true)]        // from matches us, no sender tag
    [InlineData(0xABu, "!11223344", 0xABu, "!aabbccdd", true)] // from matches us, sender is someone else
    [InlineData(0xCDu, null, 0xABu, "!aabbccdd", false)]       // from is a different node
    [InlineData(0xABu, "!aabbccdd", 0xABu, "!aabbccdd", false)] // sender is us (our own echoed publish)
    [InlineData(null, null, 0xABu, "!aabbccdd", false)]        // no "from" at all
    public void IsValidJsonDownlinkEnvelope_MatchesFirmwareRule(
        uint? fromNodeNum, string? senderNodeId, uint ourNodeNum, string ourNodeId, bool expected)
    {
        Assert.Equal(expected, MqttPolicy.IsValidJsonDownlinkEnvelope(fromNodeNum, senderNodeId, ourNodeNum, ourNodeId));
    }

    [Theory]
    [InlineData(14, 14)]
    [InlineData(12, 12)]
    [InlineData(15, 15)]
    [InlineData(11, 14)]
    [InlineData(16, 14)]
    [InlineData(0, 14)]
    [InlineData(32, 14)]
    public void CoerceMapPositionPrecision_ClampsOutOfRangeToDefault(int input, int expected)
    {
        Assert.Equal(expected, MqttPolicy.CoerceMapPositionPrecision(input));
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)] // just outside 172.16.0.0/12
    [InlineData("10.0.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.128.0.1", false)] // just outside 100.64.0.0/10
    [InlineData("8.8.8.8", false)]
    [InlineData("mqtt.meshtastic.org", false)] // not an IP literal at all
    public void IsPrivateHost_MatchesFirmwareCidrRanges(string host, bool expected)
    {
        Assert.Equal(expected, MqttPolicy.IsPrivateHost(host));
    }

    // ---- Uplink gating ----

    private static MqttPolicy.UplinkContext BaseUplinkCtx() => new(
        ViaMqtt: false,
        AnyChannelUplinkEnabled: true,
        ChannelUplinkEnabled: true,
        IsPki: false,
        IsFromUs: true,
        IsDefaultServer: true,
        ServerIsPrivate: false,
        HasOkToMqttBit: true,
        OkToMqtt: true,
        IsRangeTestOrDetectionSensorPort: false);

    [Fact]
    public void ShouldUplink_AllowsOurOwnPacketOnDefaultServerEvenWithoutOkToMqtt()
    {
        var ctx = BaseUplinkCtx() with { HasOkToMqttBit = false, OkToMqtt = false };
        Assert.True(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_RejectsPacketThatCameFromMqtt()
    {
        var ctx = BaseUplinkCtx() with { ViaMqtt = true };
        Assert.False(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_RejectsWhenNoChannelHasUplinkEnabled()
    {
        var ctx = BaseUplinkCtx() with { AnyChannelUplinkEnabled = false };
        Assert.False(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_RejectsOthersPacketOnPublicServerWithoutOkToMqtt()
    {
        var ctx = BaseUplinkCtx() with { IsFromUs = false, HasOkToMqttBit = false, OkToMqtt = false };
        Assert.False(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_AllowsOthersPacketOnPublicServerWithOkToMqtt()
    {
        var ctx = BaseUplinkCtx() with { IsFromUs = false, HasOkToMqttBit = true, OkToMqtt = true };
        Assert.True(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_AllowsOthersPacketOnPrivateServerWithoutOkToMqtt()
    {
        var ctx = BaseUplinkCtx() with { IsFromUs = false, ServerIsPrivate = true, HasOkToMqttBit = false, OkToMqtt = false };
        Assert.True(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_RejectsRangeTestOnDefaultServer()
    {
        var ctx = BaseUplinkCtx() with { IsRangeTestOrDetectionSensorPort = true };
        Assert.False(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_AllowsRangeTestOnCustomServer()
    {
        var ctx = BaseUplinkCtx() with { IsRangeTestOrDetectionSensorPort = true, IsDefaultServer = false };
        Assert.True(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_RejectsWhenChannelUplinkDisabledAndNotPki()
    {
        var ctx = BaseUplinkCtx() with { ChannelUplinkEnabled = false, IsPki = false };
        Assert.False(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_AllowsPkiEvenWithChannelUplinkDisabled()
    {
        var ctx = BaseUplinkCtx() with { ChannelUplinkEnabled = false, IsPki = true };
        Assert.True(MqttPolicy.ShouldUplink(ctx));
    }

    [Fact]
    public void ShouldUplink_PkiSkipsOkToMqttGate_UnlikeNormalChannelPackets()
    {
        // A PKI packet a gateway can't decrypt has no bitfield to inspect —
        // firmware only applies the ok_to_mqtt / DontMqttMeBro check to the
        // "decoded_tag" (channel-PSK-decoded) variant, never to PKI traffic.
        var pki = BaseUplinkCtx() with
        {
            IsPki = true,
            IsFromUs = false,
            ServerIsPrivate = false,
            HasOkToMqttBit = false,
            OkToMqtt = false,
        };
        Assert.True(MqttPolicy.ShouldUplink(pki));

        // Same inputs but NOT PKI: the gate applies and rejects it.
        var normal = pki with { IsPki = false, ChannelUplinkEnabled = true };
        Assert.False(MqttPolicy.ShouldUplink(normal));
    }

    [Fact]
    public void ShouldUplink_PkiSkipsNoisyPortSuppressionOnDefaultServer()
    {
        var ctx = BaseUplinkCtx() with { IsPki = true, IsRangeTestOrDetectionSensorPort = true };
        Assert.True(MqttPolicy.ShouldUplink(ctx));
    }

    // ---- Downlink gating ----

    private static MqttPolicy.DownlinkContext BaseDownlinkCtx() => new(
        ChannelId: "LongFast",
        GatewayId: "!aabbccdd",
        OurNodeId: "!11223344",
        MatchedLocalChannelDownlinkEnabled: true,
        AnyChannelDownlinkEnabled: true,
        PacketFrom: 0xAABBCCDD,
        OurNodeNum: 0x11223344,
        HopLimit: 3,
        HopStart: 3);

    [Fact]
    public void ShouldAcceptDownlink_AcceptsValidChannelEnvelope()
    {
        Assert.True(MqttPolicy.ShouldAcceptDownlink(BaseDownlinkCtx()));
    }

    [Fact]
    public void ShouldAcceptDownlink_RejectsMissingChannelOrGateway()
    {
        Assert.False(MqttPolicy.ShouldAcceptDownlink(BaseDownlinkCtx() with { ChannelId = null }));
        Assert.False(MqttPolicy.ShouldAcceptDownlink(BaseDownlinkCtx() with { GatewayId = "" }));
    }

    [Fact]
    public void ShouldAcceptDownlink_RejectsChannelWithoutLocalDownlinkEnabled()
    {
        Assert.False(MqttPolicy.ShouldAcceptDownlink(BaseDownlinkCtx() with { MatchedLocalChannelDownlinkEnabled = false }));
    }

    [Fact]
    public void ShouldAcceptDownlink_PkiRequiresAnyChannelDownlinkEnabled()
    {
        var pki = BaseDownlinkCtx() with { ChannelId = "PKI", MatchedLocalChannelDownlinkEnabled = false };
        Assert.False(MqttPolicy.ShouldAcceptDownlink(pki with { AnyChannelDownlinkEnabled = false }));
        Assert.True(MqttPolicy.ShouldAcceptDownlink(pki with { AnyChannelDownlinkEnabled = true }));
    }

    [Fact]
    public void ShouldAcceptDownlink_RejectsOurOwnUplinkedEcho()
    {
        var ctx = BaseDownlinkCtx() with { GatewayId = "!11223344" }; // matches OurNodeId
        Assert.False(MqttPolicy.ShouldAcceptDownlink(ctx));
    }

    [Fact]
    public void ShouldAcceptDownlink_RejectsPacketWeOriginated()
    {
        var ctx = BaseDownlinkCtx() with { PacketFrom = 0x11223344 }; // matches OurNodeNum
        Assert.False(MqttPolicy.ShouldAcceptDownlink(ctx));
    }

    [Theory]
    [InlineData(8, 3)]
    [InlineData(3, 8)]
    [InlineData(-1, 3)]
    public void ShouldAcceptDownlink_RejectsInvalidHopFields(int hopLimit, int hopStart)
    {
        var ctx = BaseDownlinkCtx() with { HopLimit = hopLimit, HopStart = hopStart };
        Assert.False(MqttPolicy.ShouldAcceptDownlink(ctx));
    }
}
