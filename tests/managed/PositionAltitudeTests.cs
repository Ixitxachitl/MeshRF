// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Channels;
using MeshRF.Location;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Firmware's ALTITUDE_MSL position flag decides which field carries our
/// altitude: <c>altitude</c> (3, above mean sea level) or <c>altitude_hae</c>
/// (9, above the ellipsoid). The TAK roles clear the flag because CoTs use HAE,
/// so a MeshRF TAK node that always sent field 3 would be mislabelling its
/// height to every TAK client watching.
/// </summary>
public class PositionAltitudeTests
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

    private static byte[] Encode(int altitude, bool msl) =>
        MeshEncoder.EncodePosition(DefaultChannel(), from: 5, packetId: 6,
                                   latitude: Lat, longitude: Lon,
                                   altitudeM: altitude, altitudeIsMsl: msl);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AltitudeRoundTripsEitherWay(bool msl)
    {
        var decoded = MeshDecoder.Decode(Encode(1234, msl), new[] { DefaultChannel() });
        Assert.NotNull(decoded?.Position);
        Assert.Equal(1234, decoded!.Position!.AltitudeM);
    }

    // Zig-zag: without it a negative HAE would encode as ten bytes of varint,
    // and read back as a huge positive number.
    [Theory]
    [InlineData(-412)]
    [InlineData(-1)]
    [InlineData(0)]
    public void NegativeHaeAltitudeSurvives(int altitude)
    {
        var decoded = MeshDecoder.Decode(Encode(altitude, msl: false), new[] { DefaultChannel() });
        Assert.NotNull(decoded?.Position);
        Assert.Equal(altitude, decoded!.Position!.AltitudeM);
    }

    // The two are genuinely different bytes on the wire — a receiver keying off
    // the field number has to be able to tell them apart.
    [Fact]
    public void MslAndHaeAreDifferentFrames() =>
        Assert.NotEqual(Encode(1234, msl: true), Encode(1234, msl: false));

    [Fact]
    public void MslIsTheDefault() =>
        Assert.Equal(
            MeshEncoder.EncodePosition(DefaultChannel(), 5, 6, Lat, Lon, altitudeM: 100),
            Encode(100, msl: true));

    [Fact]
    public void NoAltitudeStillEncodes()
    {
        var frame = MeshEncoder.EncodePosition(DefaultChannel(), 5, 6, Lat, Lon,
                                               altitudeM: null, altitudeIsMsl: false);
        var decoded = MeshDecoder.Decode(frame, new[] { DefaultChannel() });
        Assert.NotNull(decoded?.Position);
        Assert.Null(decoded!.Position!.AltitudeM);
    }

    // ---- Choosing the datum ----

    // Everything MeshRF holds is orthometric, so HAE is a conversion and not a
    // label. The receiver already worked out the separation for us.
    [Fact]
    public void HaeIsTheReadingPlusTheSeparation()
    {
        var (altitude, isMsl) = AltitudeDatum.ForTransmit(1500, geoidSeparationM: -27, wantsHae: true);
        Assert.Equal(1473, altitude);
        Assert.False(isMsl);
    }

    // Without a separation the conversion cannot be made, and a number in the
    // HAE field would be wrong by the height of the geoid. The datum we can
    // prove goes out instead.
    [Fact]
    public void NoSeparationMeansTheReadingStaysMsl()
    {
        var (altitude, isMsl) = AltitudeDatum.ForTransmit(1500, geoidSeparationM: null, wantsHae: true);
        Assert.Equal(1500, altitude);
        Assert.True(isMsl);
    }

    [Fact]
    public void WithoutARoleAskingForHaeTheSeparationIsLeftAlone()
    {
        var (altitude, isMsl) = AltitudeDatum.ForTransmit(1500, geoidSeparationM: -27, wantsHae: false);
        Assert.Equal(1500, altitude);
        Assert.True(isMsl);
    }

    [Fact]
    public void NoAltitudeAtAllSendsNone() =>
        Assert.Equal((null, true), AltitudeDatum.ForTransmit(null, geoidSeparationM: -27, wantsHae: true));

    // A TAK node is the one that asks for this, and it is exactly the case the
    // conversion has to be right for.
    [Fact]
    public void ATakRoleIsWhatAsksForHae()
    {
        Assert.False(RoleDefaults.For("Tak").PositionAltitudeMsl);
        Assert.False(RoleDefaults.For("TakTracker").PositionAltitudeMsl);
        Assert.Null(RoleDefaults.For("Client").PositionAltitudeMsl);
    }
}
