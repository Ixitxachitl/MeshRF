// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>A position on the ground, in degrees.</summary>
public readonly record struct GeoPoint(double Lat, double Lon);

/// <summary>
/// Great-circle distance and interpolation on a spherical earth.
///
/// A sphere rather than an ellipsoid: over the tens of kilometres a LoRa link
/// spans the difference is a few metres, which is far inside the error of the
/// 30 m terrain model the results are sampled against.
/// </summary>
public static class Geodesy
{
    /// <summary>Mean earth radius, metres.</summary>
    public const double EarthRadiusM = 6_371_008.8;

    public static double DistanceM(GeoPoint a, GeoPoint b)
    {
        double lat1 = Rad(a.Lat), lat2 = Rad(b.Lat);
        double dLat = lat2 - lat1;
        double dLon = Rad(b.Lon - a.Lon);

        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    /// <summary>The point a fraction of the way along the great circle from
    /// <paramref name="a"/> to <paramref name="b"/>.
    ///
    /// Spherical interpolation rather than linear interpolation of the
    /// coordinates: the two agree over a short east-west link but diverge with
    /// distance and latitude, and the profile is sampled along exactly the path
    /// the radio sees.</summary>
    public static GeoPoint Interpolate(GeoPoint a, GeoPoint b, double fraction)
    {
        if (fraction <= 0) return a;
        if (fraction >= 1) return b;

        double lat1 = Rad(a.Lat), lon1 = Rad(a.Lon);
        double lat2 = Rad(b.Lat), lon2 = Rad(b.Lon);

        double d = DistanceM(a, b) / EarthRadiusM;
        if (d < 1e-12) return a;

        double sinD = Math.Sin(d);
        double p = Math.Sin((1 - fraction) * d) / sinD;
        double q = Math.Sin(fraction * d) / sinD;

        double x = p * Math.Cos(lat1) * Math.Cos(lon1) + q * Math.Cos(lat2) * Math.Cos(lon2);
        double y = p * Math.Cos(lat1) * Math.Sin(lon1) + q * Math.Cos(lat2) * Math.Sin(lon2);
        double z = p * Math.Sin(lat1) + q * Math.Sin(lat2);

        return new GeoPoint(
            Deg(Math.Atan2(z, Math.Sqrt(x * x + y * y))),
            Deg(Math.Atan2(y, x)));
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;
    private static double Deg(double radians) => radians * 180.0 / Math.PI;
}
