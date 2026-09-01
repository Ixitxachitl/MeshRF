// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Position.latitude_i and longitude_i are <c>optional</c> in the proto, so a
/// sender that has no fix — or that is only asking for ours — leaves them off
/// the wire entirely. Reading the absent fields as 0 turns a position request
/// into a report from the Gulf of Guinea, and storing it erases whatever we
/// knew of that node's real location.
/// </summary>
public class PositionAbsentCoordinateTests
{
    private static ChannelConfig DefaultChannel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    private static MeshDecodeResult Decode(byte[] frame)
    {
        var decoded = MeshDecoder.Decode(frame, new[] { DefaultChannel() });
        Assert.NotNull(decoded);
        return decoded!;
    }

    // The exact 3-byte payload seen off-air: Router::send stamps precision_bits
    // (field 23) onto every position the firmware originates, request included,
    // so a coordinate-less request is not a zero-length payload.
    [Fact]
    public void PrecisionOnlyPayloadHasNoCoordinates()
    {
        var pos = new ProtoWriter();
        pos.WriteVarintField(23, 32);
        Assert.Equal(new byte[] { 0xB8, 0x01, 0x20 }, pos.ToArray());

        var frame = MeshEncoder.Encode(DefaultChannel(), from: 5, to: 6, packetId: 7,
                                       PortNum.Position, pos.ToArray(), wantResponse: true);

        var position = Decode(frame).Position;
        Assert.NotNull(position);
        Assert.False(position!.HasLocation);
        Assert.Null(position.Latitude);
        Assert.Null(position.Longitude);
    }

    [Fact]
    public void OurOwnPositionRequestCarriesNoCoordinates()
    {
        var frame = MeshEncoder.EncodePositionRequest(DefaultChannel(), from: 5, to: 6, packetId: 7);

        var result = Decode(frame);
        Assert.True(result.WantResponse);
        Assert.NotNull(result.Position);
        Assert.False(result.Position!.HasLocation);
    }

    // A real report at the origin is still a real report — presence is what
    // distinguishes it, not the value.
    [Fact]
    public void ExplicitZeroCoordinatesAreALocation()
    {
        var pos = new ProtoWriter();
        pos.WriteFixed32Field(1, 0);
        pos.WriteFixed32Field(2, 0);

        var frame = MeshEncoder.Encode(DefaultChannel(), from: 5, to: 6, packetId: 7,
                                       PortNum.Position, pos.ToArray());

        var position = Decode(frame).Position;
        Assert.NotNull(position);
        Assert.True(position!.HasLocation);
        Assert.Equal(0, position.Latitude!.Value);
        Assert.Equal(0, position.Longitude!.Value);
    }
}
