// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>Maps a vector tile's own coordinates onto the pixels of one output
/// tile.
///
/// A vector source stops at some zoom — OpenFreeMap's planet tiles end at 14 —
/// but the map keeps zooming past it. Above that ceiling the parent tile is
/// reused and the slice of it covering the requested tile is drawn magnified,
/// which is why detail stops increasing above the ceiling while lines and text
/// stay sharp. At or below the ceiling the tile maps onto itself and
/// <see cref="IsOverzoomed"/> is false.</summary>
public readonly record struct TileProjection(
    int SourceZoom, int SourceX, int SourceY,
    double PixelsPerUnit, double OffsetX, double OffsetY, int Magnification)
{
    public bool IsOverzoomed => Magnification > 1;

    /// <summary>Works out which tile to fetch and how to place it.</summary>
    /// <param name="zoom">The zoom being displayed.</param>
    /// <param name="x">Requested tile column at that zoom.</param>
    /// <param name="y">Requested tile row at that zoom.</param>
    /// <param name="sourceMaxZoom">Deepest zoom the source publishes.</param>
    /// <param name="extent">Tile-local coordinate span, normally 4096.</param>
    /// <param name="outputSize">Edge length of the output tile in pixels.</param>
    public static TileProjection For(
        int zoom, int x, int y, int sourceMaxZoom, int extent, double outputSize)
    {
        int excess = Math.Max(0, zoom - sourceMaxZoom);
        int magnification = 1 << excess;

        int sourceX = x >> excess;
        int sourceY = y >> excess;

        // Which slice of the parent this tile covers.
        int sliceX = x - (sourceX << excess);
        int sliceY = y - (sourceY << excess);

        double pixelsPerUnit = magnification * outputSize / extent;
        return new TileProjection(
            SourceZoom: Math.Min(zoom, sourceMaxZoom),
            SourceX: sourceX,
            SourceY: sourceY,
            PixelsPerUnit: pixelsPerUnit,
            OffsetX: sliceX * outputSize,
            OffsetY: sliceY * outputSize,
            Magnification: magnification);
    }

    /// <summary>Which tile actually has to be fetched for a requested tile.
    /// Independent of extent and output size, so a caller can resolve the
    /// download before it knows either.</summary>
    public static (int Zoom, int X, int Y) SourceTile(int zoom, int x, int y, int sourceMaxZoom)
    {
        int excess = Math.Max(0, zoom - sourceMaxZoom);
        return (Math.Min(zoom, sourceMaxZoom), x >> excess, y >> excess);
    }

    public double MapX(int localX) => localX * PixelsPerUnit - OffsetX;

    public double MapY(int localY) => localY * PixelsPerUnit - OffsetY;

    /// <summary>Whether a part could contribute any ink, used to skip the
    /// geometry building for features outside this tile. Tiles carry a buffer
    /// of surrounding data, so most parts of an overzoomed tile fall
    /// away.</summary>
    public bool Intersects(IReadOnlyList<TilePoint> part, double outputSize, double margin)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in part)
        {
            double px = MapX(p.X), py = MapY(p.Y);
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }
        return maxX >= -margin && minX <= outputSize + margin
            && maxY >= -margin && minY <= outputSize + margin;
    }
}
