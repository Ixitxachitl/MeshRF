// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.IO;
using Google.Protobuf;
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>Decoding checks built on the worked examples in the Mapbox Vector
/// Tile specification, so the expected coordinates are the spec's own rather
/// than this decoder's output written back as the answer.</summary>
public class VectorTileTests
{
    // -- Encoding helpers ---------------------------------------------------

    private static byte[] Feature(
        ulong id, VectorTileGeometryType type, uint[] geometry, uint[]? tags = null)
    {
        var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        o.WriteTag(1, WireFormat.WireType.Varint);
        o.WriteUInt64(id);
        if (tags is { Length: > 0 })
        {
            o.WriteTag(2, WireFormat.WireType.LengthDelimited);
            o.WriteBytes(Packed(tags));
        }
        o.WriteTag(3, WireFormat.WireType.Varint);
        o.WriteEnum((int)type);
        o.WriteTag(4, WireFormat.WireType.LengthDelimited);
        o.WriteBytes(Packed(geometry));
        o.Flush();
        return ms.ToArray();
    }

    private static ByteString Packed(uint[] values)
    {
        var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        foreach (var v in values) o.WriteUInt32(v);
        o.Flush();
        return ByteString.CopyFrom(ms.ToArray());
    }

    private static byte[] StringValue(string s)
    {
        var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        o.WriteTag(1, WireFormat.WireType.LengthDelimited);
        o.WriteString(s);
        o.Flush();
        return ms.ToArray();
    }

    private static byte[] Tile(
        string layerName, IEnumerable<byte[]> features,
        int extent = 4096, string[]? keys = null, string[]? values = null)
    {
        var layer = new MemoryStream();
        var lo = new CodedOutputStream(layer);
        lo.WriteTag(15, WireFormat.WireType.Varint);
        lo.WriteUInt32(2);
        lo.WriteTag(1, WireFormat.WireType.LengthDelimited);
        lo.WriteString(layerName);
        foreach (var f in features)
        {
            lo.WriteTag(2, WireFormat.WireType.LengthDelimited);
            lo.WriteBytes(ByteString.CopyFrom(f));
        }
        foreach (var k in keys ?? [])
        {
            lo.WriteTag(3, WireFormat.WireType.LengthDelimited);
            lo.WriteString(k);
        }
        foreach (var v in values ?? [])
        {
            lo.WriteTag(4, WireFormat.WireType.LengthDelimited);
            lo.WriteBytes(ByteString.CopyFrom(StringValue(v)));
        }
        lo.WriteTag(5, WireFormat.WireType.Varint);
        lo.WriteUInt32((uint)extent);
        lo.Flush();

        var tile = new MemoryStream();
        var to = new CodedOutputStream(tile);
        to.WriteTag(3, WireFormat.WireType.LengthDelimited);
        to.WriteBytes(ByteString.CopyFrom(layer.ToArray()));
        to.Flush();
        return tile.ToArray();
    }

    private static VectorTileFeature Single(byte[] tile)
    {
        var decoded = VectorTile.Parse(tile);
        var layer = Assert.Single(decoded.Layers);
        return Assert.Single(layer.Features);
    }

    // -- Geometry -----------------------------------------------------------

    [Fact]
    public void DecodesSinglePoint()
    {
        // Spec: MoveTo(1) with parameters 50, 34 encodes (25, 17).
        var f = Single(Tile("points", [Feature(1, VectorTileGeometryType.Point, [9, 50, 34])]));
        var part = Assert.Single(f.Parts);
        Assert.Equal(new TilePoint(25, 17), Assert.Single(part));
    }

    [Fact]
    public void DecodesMultiPointAsSeparateParts()
    {
        // Spec: MoveTo(2) encoding (5, 7) then (3, 2) as a delta of (-2, -5).
        var f = Single(Tile("points", [Feature(1, VectorTileGeometryType.Point, [17, 10, 14, 3, 9])]));
        Assert.Equal(2, f.Parts.Count);
        Assert.Equal(new TilePoint(5, 7), Assert.Single(f.Parts[0]));
        Assert.Equal(new TilePoint(3, 2), Assert.Single(f.Parts[1]));
    }

    [Fact]
    public void DecodesLineString()
    {
        // Spec: (2, 2) -> (2, 10) -> (10, 10).
        var f = Single(Tile("roads",
            [Feature(1, VectorTileGeometryType.LineString, [9, 4, 4, 18, 0, 16, 16, 0])]));
        var part = Assert.Single(f.Parts);
        Assert.Equal([new TilePoint(2, 2), new TilePoint(2, 10), new TilePoint(10, 10)], part);
    }

    [Fact]
    public void DecodesPolygonRingWithoutRepeatingFirstPoint()
    {
        // Spec: (3, 6) -> (8, 12) -> (20, 34) -> ClosePath.
        var f = Single(Tile("water",
            [Feature(1, VectorTileGeometryType.Polygon, [9, 6, 12, 18, 10, 12, 24, 44, 15])]));
        var ring = Assert.Single(f.Parts);
        Assert.Equal([new TilePoint(3, 6), new TilePoint(8, 12), new TilePoint(20, 34)], ring);
    }

    [Fact]
    public void DecodesMultiLineStringAsSeparateParts()
    {
        // Two MoveTo/LineTo runs share one cursor, so the second line starts
        // relative to where the first ended.
        var f = Single(Tile("roads",
            [Feature(1, VectorTileGeometryType.LineString, [9, 4, 4, 10, 0, 16, 9, 2, 0, 10, 0, 4])]));
        Assert.Equal(2, f.Parts.Count);
        Assert.Equal([new TilePoint(2, 2), new TilePoint(2, 10)], f.Parts[0]);
        Assert.Equal([new TilePoint(3, 10), new TilePoint(3, 12)], f.Parts[1]);
    }

    // -- Ring winding -------------------------------------------------------

    [Fact]
    public void ExteriorAndInteriorRingsDifferInWindingSign()
    {
        // Clockwise in tile space (y grows downward) is an exterior ring.
        TilePoint[] clockwise = [new(0, 0), new(10, 0), new(10, 10), new(0, 10)];
        TilePoint[] counterClockwise = [new(0, 10), new(10, 10), new(10, 0), new(0, 0)];

        Assert.True(VectorTileFeature.SignedDoubleArea(clockwise) > 0);
        Assert.True(VectorTileFeature.SignedDoubleArea(counterClockwise) < 0);
        Assert.Equal(200, VectorTileFeature.SignedDoubleArea(clockwise));
    }

    // -- Attributes and layer metadata --------------------------------------

    [Fact]
    public void ResolvesAttributesAgainstLayerTables()
    {
        var tile = Tile("poi",
            [Feature(7, VectorTileGeometryType.Point, [9, 50, 34], [0, 0, 1, 1])],
            keys: ["class", "name"], values: ["restaurant", "Cafe"]);

        var f = Single(tile);
        Assert.Equal(7UL, f.Id);
        Assert.Equal("restaurant", f.Attributes["class"]);
        Assert.Equal("Cafe", f.Attributes["name"]);
    }

    [Fact]
    public void ReadsLayerNameAndExtent()
    {
        var decoded = VectorTile.Parse(Tile("transportation",
            [Feature(1, VectorTileGeometryType.Point, [9, 50, 34])], extent: 8192));
        var layer = Assert.Single(decoded.Layers);
        Assert.Equal("transportation", layer.Name);
        Assert.Equal(8192, layer.Extent);
        Assert.NotNull(decoded.Layer("transportation"));
        Assert.Null(decoded.Layer("missing"));
    }

    [Fact]
    public void UnknownFieldsAndEmptyTilesDoNotThrow()
    {
        Assert.Empty(VectorTile.Parse([]).Layers);

        // A field number the decoder does not know must be skipped, not fatal.
        var ms = new MemoryStream();
        var o = new CodedOutputStream(ms);
        o.WriteTag(9, WireFormat.WireType.Varint);
        o.WriteUInt32(42);
        o.Flush();
        Assert.Empty(VectorTile.Parse(ms.ToArray()).Layers);
    }

    [Fact]
    public void TruncatedGeometryStopsCleanly()
    {
        // A MoveTo claiming two points but carrying one must not run past the
        // end of the parameter list.
        var f = Single(Tile("points", [Feature(1, VectorTileGeometryType.Point, [17, 10, 14])]));
        Assert.Equal(new TilePoint(5, 7), Assert.Single(Assert.Single(f.Parts)));
    }
}
