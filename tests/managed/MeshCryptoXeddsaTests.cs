// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class MeshCryptoXeddsaTests
{
    private const uint From = 0x12345678, PacketId = 42, To = 0xFFFFFFFF;

    // Every covered envelope field nonzero, so each tamper case flips something that was signed.
    private static readonly XeddsaEnvelope Envelope = new(
        Portnum: 1, RequestId: 0xCAFE0001, ReplyId: 0xCAFE0002, Emoji: 0xCAFE0003,
        Bitfield: 0x01, WantResponse: true);

    private static byte[] Payload => System.Text.Encoding.UTF8.GetBytes("hello mesh");

    [Fact]
    public void SignThenVerify_RoundTrips()
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(curvePriv);

        var sig = MeshCrypto.XeddsaSign(From, PacketId, To, Envelope, Payload, edPriv, edPub);

        Assert.Equal(64, sig.Length);
        Assert.True(MeshCrypto.XeddsaVerify(From, PacketId, To, Envelope, Payload, sig, curvePub));
    }

    public static TheoryData<string> Tampers => new()
    {
        "from", "id", "to", "portnum", "request_id", "reply_id", "emoji",
        "bitfield", "bitfield_stripped", "want_response", "payload",
    };

    [Theory]
    [MemberData(nameof(Tampers))]
    public void Verify_FailsWhenAnyCoveredFieldDiffers(string field)
    {
        var curvePriv = Curve25519.GeneratePrivateKey();
        var curvePub = Curve25519.GetPublicKey(curvePriv);
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(curvePriv);
        var sig = MeshCrypto.XeddsaSign(From, PacketId, To, Envelope, Payload, edPriv, edPub);

        uint from = From, id = PacketId, to = To;
        var env = Envelope;
        var payload = Payload;
        switch (field)
        {
            case "from": from++; break;
            case "id": id++; break;
            case "to": to = 0xAABBCCDD; break;
            case "portnum": env = env with { Portnum = 2 }; break;
            case "request_id": env = env with { RequestId = 0 }; break;
            case "reply_id": env = env with { ReplyId = 0xCAFE0009 }; break;
            case "emoji": env = env with { Emoji = 0 }; break;
            case "bitfield": env = env with { Bitfield = 0x00 }; break;
            case "bitfield_stripped": env = env with { Bitfield = null }; break;
            case "want_response": env = env with { WantResponse = false }; break;
            case "payload": payload = System.Text.Encoding.UTF8.GetBytes("hello MESH"); break;
        }

        Assert.False(MeshCrypto.XeddsaVerify(from, id, to, env, payload, sig, curvePub));
    }

    [Fact]
    public void SigningBuffer_MatchesFirmwareLayout()
    {
        var buf = MeshCrypto.BuildSigningBuffer(0x04030201, 0x08070605, 0x0C0B0A09,
            new XeddsaEnvelope(Portnum: 0x10, RequestId: 0x14131211, ReplyId: 0x18171615,
                               Emoji: 0x1C1B1A19, Bitfield: 0x201F1E1D, WantResponse: true),
            new byte[] { 0xAA, 0xBB });

        Assert.Equal(new byte[]
        {
            0x01,                   // version
            0x01, 0x02, 0x03, 0x04, // from
            0x05, 0x06, 0x07, 0x08, // id
            0x09, 0x0A, 0x0B, 0x0C, // to
            0x10, 0x00, 0x00, 0x00, // portnum
            0x11, 0x12, 0x13, 0x14, // request_id
            0x15, 0x16, 0x17, 0x18, // reply_id
            0x19, 0x1A, 0x1B, 0x1C, // emoji
            0x1D, 0x1E, 0x1F, 0x20, // bitfield
            0x03,                   // flags: want_response | has_bitfield
            0xAA, 0xBB,             // payload
        }, buf);
    }

    [Fact]
    public void SigningBuffer_AbsentBitfieldSignsAsZeroWithPresenceClear()
    {
        var buf = MeshCrypto.BuildSigningBuffer(1, 2, 3, new XeddsaEnvelope(Portnum: 4), ReadOnlySpan<byte>.Empty);

        Assert.Equal(MeshCrypto.XeddsaSignedHeaderLength, buf.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, buf[29..33]);
        Assert.Equal(0x00, buf[33]);
    }

    [Fact]
    public void Verify_FailsForWrongSenderKey()
    {
        var (edPriv, edPub) = MeshCrypto.DeriveXeddsaKeys(Curve25519.GeneratePrivateKey());
        var otherCurvePub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        var sig = MeshCrypto.XeddsaSign(From, PacketId, To, Envelope, Payload, edPriv, edPub);

        Assert.False(MeshCrypto.XeddsaVerify(From, PacketId, To, Envelope, Payload, sig, otherCurvePub));
    }

    [Fact]
    public void Verify_FailsForMissingOrMalformedSignature()
    {
        var curvePub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());

        Assert.False(MeshCrypto.XeddsaVerify(From, PacketId, To, Envelope, Payload, null, curvePub));
        Assert.False(MeshCrypto.XeddsaVerify(From, PacketId, To, Envelope, Payload, Array.Empty<byte>(), curvePub));
        Assert.False(MeshCrypto.XeddsaVerify(From, PacketId, To, Envelope, Payload, new byte[10], curvePub));
    }
}
