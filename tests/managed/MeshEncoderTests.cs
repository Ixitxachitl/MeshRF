// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

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

    /// <summary>Firmware acks a direct text message reliably — the ack itself
    /// carries want_ack so it gets retried (ReliableRouter::
    /// shouldSuccessAckWithWantAck). Every other ack, and every repeat of one,
    /// goes out plain. The flag has to survive onto the wire either way.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoutingAckCarriesRequestedWantAck(bool wantAck)
    {
        const uint us = 0x11111111u;
        const uint peer = 0x22222222u;
        const uint ackId = 0x33333333u;
        const uint dmId = 0x44444444u;
        var channel = DefaultChannel();

        var frame = MeshEncoder.EncodeRouting(channel, us, peer, ackId, dmId,
            errorReason: 0, hopLimit: 2, wantAck: wantAck);

        Assert.True(MeshHeader.TryParse(frame, out var h));
        Assert.Equal(wantAck, h.WantAck);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(PortNum.Routing, result!.Port);
        Assert.Equal(dmId, result.RequestId);
        Assert.Equal(0, result.RoutingError);
        Assert.Equal(peer, result.Header.To);
    }

    /// <summary>A packet addressed to us that we cannot decrypt still gets an
    /// answer — a NAK naming why, on the primary channel, since the channel the
    /// request used is exactly what we could not work out.</summary>
    [Theory]
    [InlineData(RoutingError.NoChannel)]
    [InlineData(RoutingError.PkiUnknownPubkey)]
    public void RoutingNakCarriesItsErrorReason(uint reason)
    {
        var channel = DefaultChannel();
        var frame = MeshEncoder.EncodeRouting(channel, 0x11111111u, 0x22222222u,
            packetId: 0x33333333u, requestId: 0x44444444u, errorReason: reason, hopLimit: 3);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(PortNum.Routing, result!.Port);
        Assert.Equal((int)reason, result.RoutingError);
        Assert.Equal(0x44444444u, result.RequestId);
        // A NAK is never sent reliably: it is already the end of the exchange.
        Assert.True(MeshHeader.TryParse(frame, out var h));
        Assert.False(h.WantAck);
    }

    /// <summary>
    /// Data.bitfield presence has to survive decode, because it is what tells a
    /// hop_start of 0 ("this sender wanted zero hops") apart from a sender too
    /// old to populate the field at all.
    /// </summary>
    [Fact]
    public void BitfieldPresenceSurvivesDecode()
    {
        var channel = DefaultChannel();

        // EncodeTextMessage writes the bitfield (it carries ok_to_mqtt).
        var withField = MeshEncoder.EncodeTextMessage(channel, 0x1u, 0x2u, "hi", okToMqtt: true);
        var decodedWith = MeshDecoder.Decode(withField, new[] { channel });
        Assert.NotNull(decodedWith);
        Assert.True(decodedWith!.HasDataBitfield);

        // A routing ack carries no bitfield at all.
        var withoutField = MeshEncoder.EncodeRouting(channel, 0x1u, 0x2u, 0x3u, 0x4u);
        var decodedWithout = MeshDecoder.Decode(withoutField, new[] { channel });
        Assert.NotNull(decodedWithout);
        Assert.False(decodedWithout!.HasDataBitfield);
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

    [Fact]
    public void WaypointGeofenceRadiusRoundTrips()
    {
        var channel = DefaultChannel();
        var frame = MeshEncoder.EncodeWaypoint(channel, from: 0x1u, packetId: 0x2u,
            waypointId: 0x42u, latitude: 47.6062, longitude: -122.3321,
            name: "Camp", geofenceRadiusM: 250,
            notifyOnEnter: true, notifyOnExit: true, notifyFavoritesOnly: true);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        Assert.Equal(PortNum.Waypoint, result!.Port);
        var wp = result.Waypoint;
        Assert.NotNull(wp);
        Assert.Equal(250u, wp!.GeofenceRadius);
        Assert.True(wp.NotifyOnEnter);
        Assert.True(wp.NotifyOnExit);
        Assert.True(wp.NotifyFavoritesOnly);
        Assert.True(wp.HasGeofence);
        Assert.Null(wp.BoundingBox);
    }

    [Fact]
    public void WaypointBoundingBoxRoundTrips()
    {
        var channel = DefaultChannel();
        var frame = MeshEncoder.EncodeWaypoint(channel, from: 0x1u, packetId: 0x2u,
            waypointId: 0x43u, latitude: 47.6062, longitude: -122.3321,
            bboxWest: -122.35, bboxSouth: 47.60, bboxEast: -122.30, bboxNorth: 47.62);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        Assert.NotNull(result);
        var wp = result!.Waypoint;
        Assert.NotNull(wp);
        Assert.NotNull(wp!.BoundingBox);
        Assert.Equal(-122.35, wp.BoundingBox!.West, 5);
        Assert.Equal(47.60, wp.BoundingBox.South, 5);
        Assert.Equal(-122.30, wp.BoundingBox.East, 5);
        Assert.Equal(47.62, wp.BoundingBox.North, 5);
        Assert.True(wp.HasGeofence);
    }

    [Fact]
    public void WaypointWithoutGeofenceHasZeroRadiusAndNullBox()
    {
        var channel = DefaultChannel();
        var frame = MeshEncoder.EncodeWaypoint(channel, from: 0x1u, packetId: 0x2u,
            waypointId: 0x44u, latitude: 1.0, longitude: 2.0);

        var result = MeshDecoder.Decode(frame, new[] { channel });
        var wp = result!.Waypoint;
        Assert.NotNull(wp);
        Assert.Equal(0u, wp!.GeofenceRadius);
        Assert.Null(wp.BoundingBox);
        Assert.False(wp.HasGeofence);
    }
}
