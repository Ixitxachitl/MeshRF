// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// <see cref="TextMessageLimits"/> states in arithmetic what
/// <see cref="MeshEncoder"/> does in bytes, so every case here encodes a
/// message of exactly the stated length and measures the frame. A field added
/// to the Data submessage without a matching term in the limit fails these.
/// </summary>
public class TextMessageLimitsTests
{
    private static ChannelConfig DefaultChannel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 }, // firmware default-key sentinel
        Role = ChannelRole.Primary,
    };

    private const uint From = 0x4FA54F59u;
    private const uint PacketId = 0xB9497226u;

    private static string Ascii(int n) => new string('x', n);

    [Fact]
    public void BroadcastGets232Bytes()
    {
        // 255 - 16 header = 239 for the Data submessage; portnum (2) and the
        // always-written bitfield (2) leave 235, of which the payload field
        // spends a tag byte and a two-byte length varint. ok_to_mqtt sets a bit
        // in a field that is written either way, so it costs nothing.
        Assert.Equal(232, TextMessageLimits.MaxTextBytes());
    }

    [Fact]
    public void TheProtobufCeilingIsOneByteOutOfReach()
    {
        // 233 is what nanopb accepts in Data.payload, and the frame stops one
        // byte short of it — 233 bytes of text encodes to 240, and firmware
        // answers Routing_Error_TOO_LARGE.
        Assert.Equal(TextMessageLimits.PayloadFieldBytes - 1, TextMessageLimits.MaxTextBytes());

        var overLimit = MeshEncoder.EncodeTextMessage(
            DefaultChannel(), From, PacketId, Ascii(TextMessageLimits.PayloadFieldBytes));
        Assert.Equal(240, overLimit.Length - MeshHeader.Size);
    }

    [Fact]
    public void PkcCosts12Bytes()
    {
        // The AES-CCM tag and the extra nonce appended after the sealed
        // payload — firmware MESHTASTIC_PKC_OVERHEAD.
        Assert.Equal(220, TextMessageLimits.MaxTextBytes(pkc: true));
        Assert.Equal(
            TextMessageLimits.MaxTextBytes() - TextMessageLimits.PkcOverhead,
            TextMessageLimits.MaxTextBytes(pkc: true));
    }

    [Fact]
    public void ReplyAndReactionEachCostFive()
    {
        int plain = TextMessageLimits.MaxTextBytes();
        Assert.Equal(plain - 5, TextMessageLimits.MaxTextBytes(reply: true));
        Assert.Equal(plain - 10, TextMessageLimits.MaxTextBytes(reply: true, reaction: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheLimitIsExactlyWhatTheEncoderFits(bool okToMqtt, bool reply)
    {
        int max = TextMessageLimits.MaxTextBytes(reply: reply);
        uint replyId = reply ? 0x1234ABCDu : 0u;

        var atLimit = MeshEncoder.EncodeTextMessage(
            DefaultChannel(), From, PacketId, Ascii(max),
            okToMqtt: okToMqtt, replyId: replyId);
        Assert.Equal(TextMessageLimits.FrameBytes, atLimit.Length);
        Assert.True(max <= TextMessageLimits.PayloadFieldBytes);

        var overLimit = MeshEncoder.EncodeTextMessage(
            DefaultChannel(), From, PacketId, Ascii(max + 1),
            okToMqtt: okToMqtt, replyId: replyId);
        Assert.True(overLimit.Length > TextMessageLimits.FrameBytes);
    }

    [Fact]
    public void ThePkcLimitIsExactlyWhatTheEncoderFits()
    {
        var myPriv = Curve25519.GeneratePrivateKey();
        var peerPub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        const uint to = 0x11223344u;

        int max = TextMessageLimits.MaxTextBytes(pkc: true);

        var atLimit = MeshEncoder.EncodePkcTextMessage(
            From, to, PacketId, Ascii(max), myPriv, peerPub, okToMqtt: true);
        Assert.Equal(TextMessageLimits.FrameBytes, atLimit.Length);
        Assert.True(max <= TextMessageLimits.PayloadFieldBytes);

        var overLimit = MeshEncoder.EncodePkcTextMessage(
            From, to, PacketId, Ascii(max + 1), myPriv, peerPub, okToMqtt: true);
        Assert.True(overLimit.Length > TextMessageLimits.FrameBytes);
    }

    [Fact]
    public void SigningNeverPushesAFrameOverTheLimit()
    {
        // XEdDSA adds 66 bytes to a signed broadcast, and MeshEncoder drops the
        // signature rather than the message when they do not both fit
        // (firmware signedDataFits). So a long message goes out unsigned, not
        // oversize — the limit above is a limit on text, not on signing.
        var priv = Curve25519.GeneratePrivateKey();
        var (xPriv, xPub) = MeshCrypto.DeriveXeddsaKeys(priv);

        for (int n = 150; n <= TextMessageLimits.MaxTextBytes(); n++)
        {
            var frame = MeshEncoder.EncodeTextMessage(
                DefaultChannel(), From, PacketId, Ascii(n),
                xeddsaPrivateKey: xPriv, xeddsaPublicKey: xPub);
            Assert.True(frame.Length <= TextMessageLimits.FrameBytes,
                        $"{n} bytes of text signed to a {frame.Length}-byte frame");
        }
    }

    [Fact]
    public void CountsBytesNotCharacters()
    {
        // The count a writer cannot do by eye: this is what the compose bar
        // shows, and why a message of few characters can still not fit.
        Assert.Equal(4, TextMessageLimits.ByteCount("🔔"));
        Assert.Equal(2, TextMessageLimits.ByteCount("é"));
        Assert.Equal(0, TextMessageLimits.ByteCount(null));
        Assert.True(TextMessageLimits.Fits(Ascii(TextMessageLimits.MaxTextBytes())));
        Assert.False(TextMessageLimits.Fits(Ascii(TextMessageLimits.MaxTextBytes() + 1)));
    }
}
