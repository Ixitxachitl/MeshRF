// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Turning Terrarium tiles into ground elevations, and the tile arithmetic that
/// decides which ones to fetch.
/// </summary>
public class TerrainGridTests
{
    /// <summary>A tile whose every pixel carries the same height.</summary>
    private static float[] Level(float metres)
    {
        var tile = new float[TerrainGrid.TileSize * TerrainGrid.TileSize];
        Array.Fill(tile, metres);
        return tile;
    }

    private static TerrainGrid GridOf(int zoom, params ((int X, int Y) Key, float[] Tile)[] tiles) =>
        new(zoom, tiles.ToDictionary(t => t.Key, t => t.Tile));

    [Fact]
    public void ATerrariumPixelPacksMetresAsFixedPoint()
    {
        // R*256 + G + B/256, biased by 32768.
        Assert.Equal(0f, Decode(128, 0, 0));
        Assert.Equal(10.5f, Decode(128, 10, 128));
        Assert.Equal(-1f, Decode(127, 255, 0));
        Assert.Equal(-32768f, Decode(0, 0, 0));
    }

    [Fact]
    public void AnOceanTileIsRecognisedAsHavingNoLandData()
    {
        Assert.True(TerrainGrid.IsAllSeaLevel(Level(0)));
        Assert.False(TerrainGrid.IsAllSeaLevel(Level(1)));
    }

    [Fact]
    public void ATileThatIsNotTheExpectedSizeIsRefused()
    {
        var wrongSize = SolidPng(64, 64, 128, 0, 0);
        Assert.Throws<InvalidDataException>(() => TerrainGrid.DecodeTerrarium(wrongSize));
    }

    [Fact]
    public void ElevationComesBackFromTheTileCoveringThePoint()
    {
        var grid = GridOf(0, (((int)0, (int)0), Level(742)));
        Assert.Equal(742, grid.ElevationAt(45.0, -93.0)!.Value, 3);
    }

    [Fact]
    public void APointWhoseTileWasNotFetchedIsAHoleRatherThanZero()
    {
        // The distinction that matters: zero is a real elevation at the coast,
        // so a missing tile has to be a null the caller can act on.
        var grid = GridOf(1, (((int)0, (int)0), Level(500)));
        Assert.Null(grid.ElevationAt(-45.0, 93.0));
    }

    [Fact]
    public void SamplingIsBilinearAcrossNeighbouringPixels()
    {
        // Two tiles at different levels, sampled either side of their shared
        // edge: the reading has to slide between them rather than step.
        var west = Level(0);
        var east = Level(100);
        var grid = GridOf(1, (((int)0, (int)0), west), (((int)1, (int)0), east));

        double? justWest = grid.ElevationAt(45.0, -0.4);
        double? justEast = grid.ElevationAt(45.0, 0.4);
        Assert.NotNull(justWest);
        Assert.NotNull(justEast);
        Assert.Equal(0, justWest!.Value, 3);
        Assert.Equal(100, justEast!.Value, 3);
    }

    [Fact]
    public void TheWholeWorldIsOneTileAtZoomZero()
    {
        Assert.Equal((0, 0), TerrainGrid.TileFor(51.5, -0.1, 0));
        Assert.Equal((0, 0), TerrainGrid.TileFor(-33.9, 151.2, 0));
    }

    [Fact]
    public void TheEquatorAndPrimeMeridianMeetAtTheCornerOfFourTiles()
    {
        Assert.Equal((1, 1), TerrainGrid.TileFor(0.0, 0.0, 1));
        Assert.Equal((0, 0), TerrainGrid.TileFor(45.0, -90.0, 1));
        Assert.Equal((1, 0), TerrainGrid.TileFor(45.0, 90.0, 1));
    }

    [Fact]
    public void ResolutionDoublesWithEachZoom()
    {
        double shallow = TerrainGrid.MetresPerPixel(12, 0);
        double deep = TerrainGrid.MetresPerPixel(13, 0);
        Assert.Equal(shallow / 2, deep, 6);
        Assert.Equal(38.2, TerrainGrid.MetresPerPixel(12, 0), 1);
    }

    [Fact]
    public void PixelsShrinkTowardsThePoles()
    {
        Assert.True(TerrainGrid.MetresPerPixel(12, 60) < TerrainGrid.MetresPerPixel(12, 0));
    }

    [Fact]
    public void TheChosenZoomResolvesAtLeastAsFinelyAsTheSampleSpacing()
    {
        foreach (double spacing in new[] { 2.0, 10.0, 40.0, 200.0, 2000.0 })
        {
            int zoom = TerrainGrid.ZoomForSpacing(spacing, 45);
            Assert.True(TerrainGrid.MetresPerPixel(zoom, 45) <= spacing || zoom == TerrainGrid.MaxZoom,
                $"zoom {zoom} is too coarse for {spacing} m sampling");
        }
    }

    [Fact]
    public void ACloseSpacingStopsAtWhatTheSourcePublishes()
    {
        Assert.Equal(TerrainGrid.MaxZoom, TerrainGrid.ZoomForSpacing(0.5, 45));
    }

    [Fact]
    public void ALongPathIsReadAtAShallowerZoomThanAShortOne()
    {
        Assert.True(TerrainGrid.ZoomForSpacing(500, 45) < TerrainGrid.ZoomForSpacing(20, 45));
    }

    // -- helpers ------------------------------------------------------------

    private static float Decode(byte r, byte g, byte b) =>
        TerrainGrid.DecodeTerrarium(SolidPng(TerrainGrid.TileSize, TerrainGrid.TileSize, r, g, b))[0];

    /// <summary>A decoded image of one repeated colour, standing in for a tile
    /// without going through PNG encoding.</summary>
    private static PngImage SolidPng(int width, int height, byte r, byte g, byte b)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = r;
            rgb[i + 1] = g;
            rgb[i + 2] = b;
        }
        return PngImage.FromRgb(width, height, rgb);
    }
}
