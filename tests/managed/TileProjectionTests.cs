// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

public class TileProjectionTests
{
    private const int Extent = 4096;
    private const int Size = 256;
    private const int SourceMaxZoom = 14;

    // -- At or below the source ceiling -------------------------------------

    [Fact]
    public void AtTheCeilingATileMapsOntoItself()
    {
        var p = TileProjection.For(14, 2620, 6332, SourceMaxZoom, Extent, Size);

        Assert.False(p.IsOverzoomed);
        Assert.Equal(1, p.Magnification);
        Assert.Equal(14, p.SourceZoom);
        Assert.Equal(2620, p.SourceX);
        Assert.Equal(6332, p.SourceY);

        // The full extent spans the full output tile.
        Assert.Equal(0.0, p.MapX(0), 9);
        Assert.Equal(Size, p.MapX(Extent), 9);
        Assert.Equal(Size / 2.0, p.MapY(Extent / 2), 9);
    }

    [Fact]
    public void BelowTheCeilingTheRequestedTileIsTheSourceTile()
    {
        var p = TileProjection.For(9, 81, 197, SourceMaxZoom, Extent, Size);
        Assert.False(p.IsOverzoomed);
        Assert.Equal(9, p.SourceZoom);
        Assert.Equal(81, p.SourceX);
        Assert.Equal(197, p.SourceY);
        Assert.Equal(0.0, p.OffsetX, 9);
        Assert.Equal(0.0, p.OffsetY, 9);
    }

    // -- Above the ceiling --------------------------------------------------

    [Fact]
    public void OneZoomAboveTheCeilingUsesTheParentAndDoublesTheScale()
    {
        // z15 tile (5241, 12665) sits inside z14 tile (2620, 6332).
        var p = TileProjection.For(15, 5241, 12665, SourceMaxZoom, Extent, Size);

        Assert.True(p.IsOverzoomed);
        Assert.Equal(2, p.Magnification);
        Assert.Equal(14, p.SourceZoom);
        Assert.Equal(2620, p.SourceX);
        Assert.Equal(6332, p.SourceY);

        // It is the lower-right quadrant: slice (1, 1) of a 2x2.
        Assert.Equal(Size, p.OffsetX, 9);
        Assert.Equal(Size, p.OffsetY, 9);

        // The parent's midpoint is this tile's top-left corner.
        Assert.Equal(0.0, p.MapX(Extent / 2), 9);
        Assert.Equal(0.0, p.MapY(Extent / 2), 9);
        // The parent's far corner is this tile's far corner.
        Assert.Equal(Size, p.MapX(Extent), 9);
    }

    [Fact]
    public void EachOfTheFourChildrenPicksItsOwnQuadrant()
    {
        (int X, int Y, double OffX, double OffY)[] cases =
        [
            (5240, 12664, 0, 0),          // top-left
            (5241, 12664, Size, 0),       // top-right
            (5240, 12665, 0, Size),       // bottom-left
            (5241, 12665, Size, Size),    // bottom-right
        ];

        foreach (var (x, y, offX, offY) in cases)
        {
            var p = TileProjection.For(15, x, y, SourceMaxZoom, Extent, Size);
            Assert.Equal(2620, p.SourceX);
            Assert.Equal(6332, p.SourceY);
            Assert.Equal(offX, p.OffsetX, 9);
            Assert.Equal(offY, p.OffsetY, 9);
        }
    }

    [Fact]
    public void FiveZoomsAboveTheCeilingMagnifiesThirtyTwoFold()
    {
        // The deepest case the control reaches: z19 against a z14 source.
        var p = TileProjection.For(19, 83866, 202594, SourceMaxZoom, Extent, Size);

        Assert.Equal(32, p.Magnification);
        Assert.Equal(14, p.SourceZoom);
        Assert.Equal(83866 >> 5, p.SourceX);
        Assert.Equal(202594 >> 5, p.SourceY);

        // One tile-local unit now covers 32x the pixels it did at the ceiling.
        Assert.Equal(32.0 * Size / Extent, p.PixelsPerUnit, 9);
    }

    [Fact]
    public void ChildTilesTileTheParentWithoutGapsOrOverlap()
    {
        // Every z16 child of a z14 tile must map its own quadrant such that the
        // children laid side by side reproduce the parent exactly.
        const int parentX = 2620, parentY = 6332;
        for (int dx = 0; dx < 4; dx++)
        {
            for (int dy = 0; dy < 4; dy++)
            {
                int x = (parentX << 2) + dx, y = (parentY << 2) + dy;
                var p = TileProjection.For(16, x, y, SourceMaxZoom, Extent, Size);

                Assert.Equal(parentX, p.SourceX);
                Assert.Equal(parentY, p.SourceY);

                // The parent coordinate at this child's top-left maps to 0,
                // and at its bottom-right maps to the output edge.
                double unitsPerChild = Extent / 4.0;
                Assert.Equal(0.0, p.MapX((int)(dx * unitsPerChild)), 6);
                Assert.Equal(0.0, p.MapY((int)(dy * unitsPerChild)), 6);
                Assert.Equal(Size, p.MapX((int)((dx + 1) * unitsPerChild)), 6);
                Assert.Equal(Size, p.MapY((int)((dy + 1) * unitsPerChild)), 6);
            }
        }
    }

    // -- Culling ------------------------------------------------------------

    [Fact]
    public void PartsOutsideTheTileAreCulledAndPartsInsideAreKept()
    {
        var p = TileProjection.For(15, 5241, 12665, SourceMaxZoom, Extent, Size);

        // Inside the bottom-right quadrant.
        TilePoint[] inside = [new(3000, 3000), new(3500, 3500)];
        Assert.True(p.Intersects(inside, Size, 0));

        // In the top-left quadrant, which this tile does not cover.
        TilePoint[] outside = [new(100, 100), new(200, 200)];
        Assert.False(p.Intersects(outside, Size, 0));

        // Crossing the boundary still counts.
        TilePoint[] crossing = [new(1000, 3000), new(3000, 3000)];
        Assert.True(p.Intersects(crossing, Size, 0));
    }

    [Fact]
    public void TheMarginKeepsGeometryJustOutsideTheEdge()
    {
        var p = TileProjection.For(14, 2620, 6332, SourceMaxZoom, Extent, Size);

        // A tile buffer puts geometry at negative local coordinates.
        TilePoint[] justOutside = [new(-60, 2000), new(-40, 2000)];
        Assert.False(p.Intersects(justOutside, Size, 0));
        Assert.True(p.Intersects(justOutside, Size, 8));
    }
}
