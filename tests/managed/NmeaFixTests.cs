// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Location;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// What a GGA sentence actually carries. The altitude is above the geoid, and
/// the number two fields along says where the geoid is — the only thing that
/// lets us name a height above the ellipsoid without a model of one.
/// </summary>
public class NmeaFixTests
{
    // Mount Diablo, from a VK-162: 18.893 m above the geoid, which sits 25.669 m
    // below the ellipsoid here.
    private const string Gga =
        "$GPGGA,172814.0,3723.46587704,N,12202.26957864,W,2,6,1.2,18.893,M,-25.669,M,2.0,0031*4F";

    private static (bool Ok, double Lat, double Lon, int? Alt, int? Separation) Parse(string line)
    {
        bool ok = UsbSerialGpsService.TryParseNmeaFix(line, out var lat, out var lon, out var alt, out var sep);
        return (ok, lat, lon, alt, sep);
    }

    [Fact]
    public void GgaCarriesAltitudeAndSeparation()
    {
        var fix = Parse(Gga);

        Assert.True(fix.Ok);
        Assert.Equal(37.391, fix.Lat, 3);
        Assert.Equal(-122.038, fix.Lon, 3);
        Assert.Equal(19, fix.Alt);
        Assert.Equal(-26, fix.Separation);
    }

    // Height above the ellipsoid is the two of them added, and here that is
    // nearly thirty metres — the size of the error the datum flag used to make
    // by relabelling one as the other.
    [Fact]
    public void TheTwoDifferByTheHeightOfTheGeoid()
    {
        var fix = Parse(Gga);
        var (hae, isMsl) = AltitudeDatum.ForTransmit(fix.Alt, fix.Separation, wantsHae: true);

        Assert.Equal(-7, hae);
        Assert.False(isMsl);
    }

    // Plenty of receivers leave the field empty, and some sentences stop before
    // it. Either way there is no separation, not a separation of zero.
    [Theory]
    [InlineData("$GPGGA,172814.0,3723.46587704,N,12202.26957864,W,2,6,1.2,18.893,M,,M,2.0,0031*4F")]
    [InlineData("$GPGGA,172814.0,3723.46587704,N,12202.26957864,W,2,6,1.2,18.893,M")]
    public void AMissingSeparationIsAbsentRatherThanZero(string line)
    {
        var fix = Parse(line);

        Assert.True(fix.Ok);
        Assert.Equal(19, fix.Alt);
        Assert.Null(fix.Separation);
    }

    // A sentence with no fix in it must not be read as one at the equator.
    [Fact]
    public void AGgaWithoutAFixIsRefused() =>
        Assert.False(Parse("$GPGGA,172814.0,3723.46587704,N,12202.26957864,W,0,0,,,M,,M,,*4F").Ok);

    // RMC has position but no height, which is absent rather than sea level.
    [Fact]
    public void RmcCarriesNoAltitude()
    {
        var fix = Parse("$GPRMC,172814.0,A,3723.46587704,N,12202.26957864,W,0.15,0.0,180325,,,A*6A");

        Assert.True(fix.Ok);
        Assert.Null(fix.Alt);
        Assert.Null(fix.Separation);
    }
}
