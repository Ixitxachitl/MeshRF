// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class PositionPrecisionTests
{
    private const double Lat = 39.19053;
    private const double Lon = -120.76974;

    private static ChannelConfig DefaultChannel() => new()
    {
        Index = 0,
        Name = "LongFast",
        Psk = new byte[] { 0x01 },
        Role = ChannelRole.Primary,
    };

    [Fact]
    public void FullPrecisionPassesThrough()
    {
        var (lat, lon) = MeshEncoder.ApplyPositionPrecision(Lat, Lon, 32);
        Assert.Equal(Lat, lat, 6);
        Assert.Equal(Lon, lon, 6);
    }

    [Fact]
    public void CoarsePrecisionCollapsesSmallMovesIntoOneCell()
    {
        // Smart broadcast compares transmitted coordinates, so a move smaller
        // than the channel's cell has to come out as no move at all — putting
        // an identical pair of numbers on the air is airtime for nothing.
        const byte precision = 16; // Cells a few hundred metres across.
        var a = MeshEncoder.ApplyPositionPrecision(Lat, Lon, precision);
        var b = MeshEncoder.ApplyPositionPrecision(Lat + 0.0001, Lon, precision); // ~11 m north
        Assert.Equal(a, b);
    }

    [Fact]
    public void CoarsePrecisionStillSeparatesRealMoves()
    {
        const byte precision = 16;
        var a = MeshEncoder.ApplyPositionPrecision(Lat, Lon, precision);
        var b = MeshEncoder.ApplyPositionPrecision(Lat + 0.02, Lon, precision); // ~2.2 km north
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ApplyPositionPrecisionMatchesWhatIsActuallyTransmitted()
    {
        // The whole point of the helper: smart broadcast has to measure
        // movement in the same terms the encoder puts on the wire, so the two
        // must not be able to drift apart.
        var channel = DefaultChannel();
        foreach (byte precision in new byte[] { 12, 16, 19, 32 })
        {
            var frame = MeshEncoder.EncodePosition(channel, from: 5, packetId: 6,
                                                   latitude: Lat, longitude: Lon,
                                                   precisionBits: precision);
            var decoded = MeshDecoder.Decode(frame, new[] { channel });
            Assert.NotNull(decoded);
            Assert.NotNull(decoded!.Position);

            var (lat, lon) = MeshEncoder.ApplyPositionPrecision(Lat, Lon, precision);
            Assert.True(decoded.Position!.HasLocation);
            Assert.Equal(lat, decoded.Position.Latitude!.Value, 6);
            Assert.Equal(lon, decoded.Position.Longitude!.Value, 6);
        }
    }
}
