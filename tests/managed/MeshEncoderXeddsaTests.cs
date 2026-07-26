// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Verifies <see cref="MeshEncoder"/> attaches (and a real Meshtastic 2.8+
/// receiver could verify) an XEdDSA signature on broadcasts, and correctly
/// leaves unicast/unsigned sends alone.
/// </summary>
public class MeshEncoderXeddsaTests
{
    private static ChannelConfig DefaultChannel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    private static (byte[] curvePriv, byte[] curvePub, byte[] edPriv, byte[] edPub) MakeIdentity()
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(curvePriv);
        return (curvePriv, curvePub, edPriv, edPub);
    }

    [Fact]
    public void BroadcastTextMessage_IsSignedAndVerifiable()
    {
        var (_, curvePub, edPriv, edPub) = MakeIdentity();
        var channel = DefaultChannel();
        const uint from = 0x4FA54F59u, id = 0xB9497226u;

        var frame = MeshEncoder.EncodeTextMessage(channel, from, id, "hello mesh",
            xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(64, result!.DataField10.Length);

        bool verified = MeshCrypto.XeddsaVerify(from, id, (uint)PortNum.TextMessage,
            result.AppPayload, result.DataField10, curvePub);
        Assert.True(verified);
    }

    [Fact]
    public void UnicastTextMessage_IsNeverSigned()
    {
        var (_, _, edPriv, edPub) = MakeIdentity();
        var channel = DefaultChannel();

        var frame = MeshEncoder.EncodeTextMessage(channel, from: 1, packetId: 2, "dm",
            to: 0xAABBCCDDu, // not broadcast
            xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Empty(result!.DataField10);
    }

    [Fact]
    public void BroadcastWithoutKeys_IsUnsigned()
    {
        var channel = DefaultChannel();
        var frame = MeshEncoder.EncodeTextMessage(channel, from: 1, packetId: 2, "no keys given");

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Empty(result!.DataField10);
    }

    [Fact]
    public void SignedFrame_FailsVerificationForWrongSenderKey()
    {
        var (_, _, edPriv, edPub) = MakeIdentity();
        var (_, otherCurvePub, _, _) = MakeIdentity();
        var channel = DefaultChannel();
        const uint from = 1, id = 2;

        var frame = MeshEncoder.EncodeTextMessage(channel, from, id, "hello mesh",
            xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);
        var result = MeshDecoder.Decode(frame, new[] { channel });

        bool verified = MeshCrypto.XeddsaVerify(from, id, (uint)PortNum.TextMessage,
            result!.AppPayload, result.DataField10, otherCurvePub);
        Assert.False(verified);
    }

    [Fact]
    public void BroadcastNodeInfo_IsSignedAndVerifiable()
    {
        var (_, curvePub, edPriv, edPub) = MakeIdentity();
        var channel = DefaultChannel();
        const uint from = 0x11223344u, id = 99;

        var frame = MeshEncoder.EncodeNodeInfo(channel, from, id, "Long Name", "SHRT",
            publicKey: curvePub, xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(64, result!.DataField10.Length);
        Assert.True(MeshCrypto.XeddsaVerify(from, id, (uint)PortNum.NodeInfo,
            result.AppPayload, result.DataField10, curvePub));
    }

    [Fact]
    public void BroadcastPosition_IsSignedAndVerifiable()
    {
        var (_, curvePub, edPriv, edPub) = MakeIdentity();
        var channel = DefaultChannel();
        const uint from = 5, id = 6;

        var frame = MeshEncoder.EncodePosition(channel, from, id, 47.6062, -122.3321,
            xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(64, result!.DataField10.Length);
        Assert.True(MeshCrypto.XeddsaVerify(from, id, (uint)PortNum.Position,
            result.AppPayload, result.DataField10, curvePub));
    }

    [Fact]
    public void OversizedBroadcast_SkipsSigningButStillSendsUnsigned()
    {
        var (_, _, edPriv, edPub) = MakeIdentity();
        var channel = DefaultChannel();

        // A message long enough that Data + a 64-byte signature would blow the
        // 255-byte LoRa payload budget (mirrors firmware's signedDataFits):
        // the encoder must fall back to an unsigned send rather than throw or
        // silently truncate.
        var longText = new string('x', 200);
        var frame = MeshEncoder.EncodeTextMessage(channel, from: 1, packetId: 2, longText,
            xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(longText, result!.Text);
        Assert.Empty(result.DataField10);
        Assert.True(frame.Length <= MeshHeader.Size + 255);
    }

    [Fact]
    public void SmallBroadcast_SignsAndFitsWithinLoraFrameBudget()
    {
        var (_, curvePub, edPriv, edPub) = MakeIdentity();
        var channel = DefaultChannel();
        const uint from = 1, id = 2;

        var frame = MeshEncoder.EncodeTextMessage(channel, from, id, "short message",
            xeddsaPrivateKey: edPriv, xeddsaPublicKey: edPub);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(64, result!.DataField10.Length);
        Assert.True(MeshCrypto.XeddsaVerify(from, id, (uint)PortNum.TextMessage,
            result.AppPayload, result.DataField10, curvePub));
        Assert.True(frame.Length <= MeshHeader.Size + 255);
    }
}
