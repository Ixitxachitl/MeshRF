// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>
/// Ground elevation looked up from a set of already-fetched Terrarium tiles.
///
/// Terrarium is an ordinary Web-Mercator 256px tile pyramid whose pixels carry
/// height instead of colour, so the tile arithmetic here is the same as the
/// basemap's and only the decode differs. Sampling is bilinear across tile
/// seams: a profile line drawn from nearest-neighbour lookups steps in visible
/// terraces, and those steps read as terrain features that are not there.
///
/// The grid owns no fetching. It is handed the tiles it needs, which keeps the
/// elevation maths testable without a network and lets the caller decide how
/// tiles are cached — see <see cref="TerrainTiles"/>.
/// </summary>
public sealed class TerrainGrid
{
    public const int TileSize = 256;

    /// <summary>Deepest zoom the Terrarium pyramid publishes.</summary>
    public const int MaxZoom = 15;

    /// <summary>Height carried by a Terrarium pixel that was never filled in.
    /// The dataset uses it for open ocean, where it is genuinely sea level, so
    /// it is a real reading rather than a hole — but a whole tile of it means
    /// the tile has no land data.</summary>
    private const float SeaLevel = 0f;

    private readonly int _zoom;
    private readonly IReadOnlyDictionary<(int X, int Y), float[]> _tiles;

    public TerrainGrid(int zoom, IReadOnlyDictionary<(int X, int Y), float[]> tiles)
    {
        _zoom = zoom;
        _tiles = tiles;
    }

    public int Zoom => _zoom;

    /// <summary>Ground metres above the ellipsoid, or null when the tile
    /// covering the point was not fetched or failed to load. A null is a hole
    /// in the data, not a zero: callers must not average it in.</summary>
    public double? ElevationAt(double lat, double lon)
    {
        double scale = TileSize * (1 << _zoom);
        double gx = (lon + 180.0) / 360.0 * scale;
        double gy = MercatorY(lat) * scale;

        // Pixel values sit at pixel centres, so the sample lands half a pixel
        // in from the coordinate. Without the shift every reading is biased
        // half a pixel north-west, which at zoom 13 is about ten metres.
        double fx = gx - 0.5, fy = gy - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;

        double? e00 = PixelAt(x0, y0);
        double? e10 = PixelAt(x0 + 1, y0);
        double? e01 = PixelAt(x0, y0 + 1);
        double? e11 = PixelAt(x0 + 1, y0 + 1);
        if (e00 is null || e10 is null || e01 is null || e11 is null) return null;

        double top = e00.Value + (e10.Value - e00.Value) * tx;
        double bottom = e01.Value + (e11.Value - e01.Value) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>One pixel of the pyramid by global pixel coordinate, resolving
    /// which tile holds it.</summary>
    private double? PixelAt(int gx, int gy)
    {
        int span = 1 << _zoom;
        int tileX = (int)Math.Floor(gx / (double)TileSize);
        int tileY = (int)Math.Floor(gy / (double)TileSize);

        // Longitude wraps, latitude does not: a sample past the top or bottom
        // of the pyramid is off the map rather than round the other side.
        tileX = ((tileX % span) + span) % span;
        if (tileY < 0 || tileY >= span) return null;

        if (!_tiles.TryGetValue((tileX, tileY), out var tile)) return null;

        int px = ((gx % TileSize) + TileSize) % TileSize;
        int py = gy - tileY * TileSize;
        return tile[py * TileSize + px];
    }

    /// <summary>Which tile a position falls in at a zoom.</summary>
    public static (int X, int Y) TileFor(double lat, double lon, int zoom)
    {
        int span = 1 << zoom;
        int x = (int)Math.Floor((lon + 180.0) / 360.0 * span);
        int y = (int)Math.Floor(MercatorY(lat) * span);
        return (((x % span) + span) % span, Math.Clamp(y, 0, span - 1));
    }

    /// <summary>Ground metres per pixel at a zoom and latitude.</summary>
    public static double MetresPerPixel(int zoom, double lat) =>
        156543.03392804097 * Math.Cos(lat * Math.PI / 180.0) / (1 << zoom);

    /// <summary>The shallowest zoom whose pixels are at least as fine as the
    /// spacing the profile samples at, capped at what the source publishes.
    ///
    /// Going deeper than the samples costs tiles without adding terrain, so a
    /// long link is read at a shallow zoom and a short one at a deep zoom. A
    /// link short enough to out-resolve the source settles for the deepest
    /// zoom there is: past that the detail would be interpolation, not
    /// ground.</summary>
    public static int ZoomForSpacing(double metresPerSample, double lat)
    {
        for (int zoom = 7; zoom < MaxZoom; zoom++)
            if (MetresPerPixel(zoom, lat) <= metresPerSample) return zoom;
        return MaxZoom;
    }

    /// <summary>Turns a Terrarium tile into elevations. Each pixel packs metres
    /// as <c>R*256 + G + B/256</c> biased by 32768, so the byte triple is a
    /// fixed-point height rather than a colour.</summary>
    public static float[] DecodeTerrarium(PngImage png)
    {
        if (png.Width != TileSize || png.Height != TileSize)
            throw new InvalidDataException(
                $"terrain tile is {png.Width}x{png.Height}, expected {TileSize}x{TileSize}");

        var elevations = new float[TileSize * TileSize];
        for (int i = 0; i < elevations.Length; i++)
        {
            int j = i * 3;
            elevations[i] = (png.Rgb[j] * 256f + png.Rgb[j + 1] + png.Rgb[j + 2] / 256f) - 32768f;
        }
        return elevations;
    }

    /// <summary>Whether a decoded tile is entirely sea level, which is how the
    /// dataset renders a tile it has no land data for.</summary>
    public static bool IsAllSeaLevel(float[] tile)
    {
        foreach (var e in tile) if (e != SeaLevel) return false;
        return true;
    }

    private static double MercatorY(double lat)
    {
        double clamped = Math.Clamp(lat, -85.05112878, 85.05112878);
        double rad = clamped * Math.PI / 180.0;
        return (1.0 - Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad)) / Math.PI) / 2.0;
    }
}
