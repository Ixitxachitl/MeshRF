// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MeshRF.Map;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// A swept coverage field painted as a bitmap, with the geographic square it
/// covers so the map can place it at any zoom.
///
/// Painted once per sweep in Web-Mercator pixel space, which makes drawing it
/// afterwards a plain scale and translate — Mercator's scale is uniform, so a
/// bitmap made at one zoom is exactly correct at every other. The alternative,
/// tens of thousands of little polar quads rebuilt on every pan, costs far more
/// for a worse result.
/// </summary>
public sealed class CoverageHeatmap
{
    /// <summary>Widest the painted square gets. A sweep is a couple of hundred
    /// samples across at best, so a larger bitmap would be interpolating its
    /// own interpolation.</summary>
    private const int MaxPixels = 768;

    /// <summary>Margin at which the paint fades out entirely. Below this the
    /// odds of a packet are a percent or two, and shading it only spreads a
    /// wash over ground the station does not reach.</summary>
    private const double FloorDb = -12;

    public WriteableBitmap Bitmap { get; }

    /// <summary>The square of the world the bitmap covers.</summary>
    public double West { get; }
    public double East { get; }
    public double North { get; }
    public double South { get; }

    private CoverageHeatmap(
        WriteableBitmap bitmap, double west, double east, double north, double south)
    {
        Bitmap = bitmap;
        West = west;
        East = east;
        North = north;
        South = south;
    }

    /// <summary>
    /// Paints a field. <paramref name="fadeSpreadDb"/> is how quickly the odds
    /// change with margin — the same figure
    /// <see cref="LinkBudget.DecodeProbability"/> takes.
    /// </summary>
    public static CoverageHeatmap? Paint(CoverageField field, double fadeSpreadDb = 3.0)
    {
        double radius = field.RadiusM;
        if (radius <= 0) return null;

        // The square around the swept disc, in degrees.
        double dLat = radius / 111_320.0;
        double dLon = radius / (111_320.0 * Math.Max(0.01, Math.Cos(field.Centre.Lat * Math.PI / 180)));

        double west = field.Centre.Lon - dLon, east = field.Centre.Lon + dLon;
        double north = Math.Min(85, field.Centre.Lat + dLat);
        double south = Math.Max(-85, field.Centre.Lat - dLat);

        // One pixel per sample at the rim is as much as the field can justify.
        int size = Math.Clamp(field.Samples * 2, 64, MaxPixels);

        var bitmap = new WriteableBitmap(
            new PixelSize(size, size), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);

        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                for (int y = 0; y < size; y++)
                {
                    var row = (byte*)buffer.Address + y * buffer.RowBytes;
                    double lat = north + (south - north) * ((y + 0.5) / size);

                    for (int x = 0; x < size; x++)
                    {
                        double lon = west + (east - west) * ((x + 0.5) / size);
                        var here = new GeoPoint(lat, lon);

                        double distance = Geodesy.DistanceM(field.Centre, here);
                        double? margin = field.MarginAt(
                            HorizonPanorama.BearingDeg(field.Centre, here), distance);

                        var (r, g, b, a) = margin is double m ? Shade(m, fadeSpreadDb) : (0, 0, 0, 0);

                        int i = x * 4;
                        row[i] = (byte)r;
                        row[i + 1] = (byte)g;
                        row[i + 2] = (byte)b;
                        row[i + 3] = (byte)a;
                    }
                }
            }
        }

        return new CoverageHeatmap(bitmap, west, east, north, south);
    }

    /// <summary>
    /// Margin to colour: green where a packet is a near certainty, amber where
    /// it is a coin toss, red where it is nearly hopeless, and nothing at all
    /// below that.
    ///
    /// The opacity follows the odds rather than the decibels, so the edge of
    /// coverage fades the way it actually behaves — a soft boundary a few
    /// decibels wide, not a line.
    /// </summary>
    private static (int R, int G, int B, int A) Shade(double marginDb, double fadeSpreadDb)
    {
        if (marginDb <= FloorDb) return (0, 0, 0, 0);

        double odds = LinkBudget.DecodeProbability(marginDb, fadeSpreadDb);

        // Red → amber → green across the odds, which puts the colour change
        // right where the link stops being dependable.
        var (r, g, b) = odds < 0.5
            ? Mix((0xEF, 0x53, 0x50), (0xFF, 0xB7, 0x4D), odds / 0.5)
            : Mix((0xFF, 0xB7, 0x4D), (0x66, 0xBB, 0x6A), (odds - 0.5) / 0.5);

        // Never fully opaque: the basemap underneath is what makes the shape
        // mean anything.
        int alpha = (int)Math.Round(30 + 105 * odds);
        return (r, g, b, alpha);
    }

    private static (int R, int G, int B) Mix((int R, int G, int B) from, (int R, int G, int B) to, double t)
    {
        double f = Math.Clamp(t, 0, 1);
        return ((int)(from.R + (to.R - from.R) * f),
                (int)(from.G + (to.G - from.G) * f),
                (int)(from.B + (to.B - from.B) * f));
    }
}
