// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshtasticRF.Channels;
using MeshtasticRF.Mesh;
using Xunit;

namespace MeshtasticRF.Tests;

/// <summary>
/// Round-trip tests for <see cref="MeshEncoder"/>: a frame it builds must
/// decode back to the same contents via <see cref="MeshDecoder"/>, and the
/// header fields must match. This proves the transmit frame builder is the
/// exact inverse of the receive decoder.
/// </summary>
public class MeshEncoderTests
{
    private static ChannelConfig DefaultChannel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 }, // firmware default-key sentinel
        Role = ChannelRole.Primary,
    };

    [Fact]
    public void TextMessageRoundTrips()
    {
        const uint from = 0x4FA54F59u;
        const uint id = 0xB9497226u;
        const string message = "Hello from the mesh!";
        var channel = DefaultChannel();

        var frame = MeshEncoder.EncodeTextMessage(channel, from, id, message);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(PortNum.TextMessage, result!.Port);
        Assert.Equal(message, result.Text);
        Assert.Equal(from, result.Header.From);
        Assert.Equal(0xFFFFFFFFu, result.Header.To);
        Assert.True(result.Header.IsBroadcast);
        Assert.Equal(id, result.Header.PacketId);
        Assert.Equal("LongFast", result.ChannelName);
    }

    [Fact]
    public void HeaderFieldsArePacked()
    {
        var channel = DefaultChannel();
        var frame = MeshEncoder.Encode(channel, from: 0x11223344u, to: 0x55667788u,
            packetId: 0xAABBCCDDu, PortNum.TextMessage,
            Encoding.UTF8.GetBytes("hi"), hopLimit: 5, wantAck: true);

        Assert.True(MeshHeader.TryParse(frame, out var h));
        Assert.Equal(0x55667788u, h.To);
        Assert.Equal(0x11223344u, h.From);
        Assert.Equal(0xAABBCCDDu, h.PacketId);
        Assert.Equal(5, h.HopLimit);
        Assert.Equal(5, h.HopStart);
        Assert.True(h.WantAck);
        Assert.Equal(channel.Hash, h.ChannelHash);
        Assert.Equal(0x44, frame[15]); // relay_node = from low byte
    }

    [Fact]
    public void EncryptedPayloadIsNotPlaintext()
    {
        var channel = DefaultChannel();
        var text = Encoding.UTF8.GetBytes("secret payload over sixteen bytes long");
        var frame = MeshEncoder.Encode(channel, 0x1u, 0xFFFFFFFFu, 0x2u,
            PortNum.TextMessage, text);

        // The ciphertext (after the 16-byte header) must not contain the
        // plaintext bytes verbatim.
        var cipher = frame.AsSpan(MeshHeader.Size);
        Assert.False(cipher.IndexOf(text.AsSpan()) >= 0);
    }

    [Fact]
    public void RawPayloadRoundTrips()
    {
        var channel = DefaultChannel();
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var frame = MeshEncoder.Encode(channel, 0xABCDu, 0xFFFFFFFFu, 0x1234u,
            PortNum.PrivateApp, payload);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(PortNum.PrivateApp, result!.Port);
        Assert.Equal(payload, result.AppPayload);
    }
}
