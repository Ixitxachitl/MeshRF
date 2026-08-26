// SPDX-License-Identifier: GPL-3.0-or-later
using Google.Protobuf;

namespace MeshRF.Map;

/// <summary>Geometry kinds a Mapbox Vector Tile feature can carry.</summary>
public enum VectorTileGeometryType
{
    Unknown = 0,
    Point = 1,
    LineString = 2,
    Polygon = 3,
}

/// <summary>A point in a tile's own coordinate space, where
/// <see cref="VectorTileLayer.Extent"/> spans the tile edge to edge. Values
/// outside 0..Extent are legal: features are clipped with a buffer so lines and
/// labels crossing a tile edge stay continuous.</summary>
public readonly record struct TilePoint(int X, int Y);

/// <summary>One feature: a geometry plus the attributes a style filters on.
/// <see cref="Parts"/> holds one part per point of a multipoint, one part per
/// line of a multi-line, and one part per ring of a polygon. Rings are not
/// closed — the first point is not repeated at the end.</summary>
public sealed class VectorTileFeature
{
    public ulong Id { get; init; }
    public VectorTileGeometryType Type { get; init; }
    public required IReadOnlyDictionary<string, object?> Attributes { get; init; }
    public required IReadOnlyList<TilePoint[]> Parts { get; init; }

    /// <summary>Twice the signed area of a ring, positive when the ring winds
    /// clockwise in tile space. The vector tile spec gives exterior rings
    /// clockwise winding and interior rings counter-clockwise, so the sign is
    /// what separates a polygon outline from the holes punched in it.</summary>
    public static long SignedDoubleArea(IReadOnlyList<TilePoint> ring)
    {
        long sum = 0;
        for (int i = 0, n = ring.Count; i < n; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % n];
            sum += (long)a.X * b.Y - (long)b.X * a.Y;
        }
        return sum;
    }
}

/// <summary>One named layer of a tile — "water", "transportation", "poi" —
/// which a style's layers select by their source-layer.</summary>
public sealed class VectorTileLayer
{
    public required string Name { get; init; }
    public int Extent { get; init; } = 4096;
    public required IReadOnlyList<VectorTileFeature> Features { get; init; }
}

/// <summary>A decoded Mapbox Vector Tile.
///
/// Decoded straight off the wire with <see cref="CodedInputStream"/> rather
/// than from a generated message class. The schema is proto2, which the C#
/// protobuf runtime does not generate for, and reading it by hand also lets
/// geometry land directly in the render-ready shape above instead of an
/// intermediate object graph — a zoom 14 tile carries several thousand
/// features, so the copy that would otherwise sit in between is worth not
/// making.</summary>
public sealed class VectorTile
{
    public required IReadOnlyList<VectorTileLayer> Layers { get; init; }

    public VectorTileLayer? Layer(string name)
    {
        foreach (var l in Layers)
            if (string.Equals(l.Name, name, StringComparison.Ordinal)) return l;
        return null;
    }

    public static VectorTile Parse(byte[] data)
    {
        var input = new CodedInputStream(data);
        var layers = new List<VectorTileLayer>();
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == 3)
                layers.Add(ParseLayer(input.ReadBytes().CreateCodedInput()));
            else input.SkipLastField();
        }
        return new VectorTile { Layers = layers };
    }

    private static VectorTileLayer ParseLayer(CodedInputStream input)
    {
        string name = string.Empty;
        int extent = 4096;
        var keys = new List<string>();
        var values = new List<object?>();
        var pending = new List<(ulong Id, VectorTileGeometryType Type, List<uint> Tags, List<uint> Geometry)>();

        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: name = input.ReadString(); break;
                case 5: extent = (int)input.ReadUInt32(); break;
                case 3: keys.Add(input.ReadString()); break;
                case 4: values.Add(ParseValue(input.ReadBytes().CreateCodedInput())); break;
                case 2: pending.Add(ParseFeature(input.ReadBytes().CreateCodedInput())); break;
                default: input.SkipLastField(); break;
            }
        }

        // Attributes resolve against the layer key and value tables, which may
        // follow the features on the wire, so they are bound only now.
        var features = new List<VectorTileFeature>(pending.Count);
        foreach (var (id, type, tags, geometry) in pending)
        {
            var attrs = new Dictionary<string, object?>(tags.Count / 2, StringComparer.Ordinal);
            for (int i = 0; i + 1 < tags.Count; i += 2)
            {
                int k = (int)tags[i], v = (int)tags[i + 1];
                if (k < keys.Count && v < values.Count) attrs[keys[k]] = values[v];
            }
            features.Add(new VectorTileFeature
            {
                Id = id,
                Type = type,
                Attributes = attrs,
                Parts = DecodeGeometry(geometry, type),
            });
        }

        return new VectorTileLayer { Name = name, Extent = extent, Features = features };
    }

    private static object? ParseValue(CodedInputStream input)
    {
        object? value = null;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: value = input.ReadString(); break;
                case 2: value = input.ReadFloat(); break;
                case 3: value = input.ReadDouble(); break;
                case 4: value = input.ReadInt64(); break;
                case 5: value = (long)input.ReadUInt64(); break;
                case 6: value = input.ReadSInt64(); break;
                case 7: value = input.ReadBool(); break;
                default: input.SkipLastField(); break;
            }
        }
        return value;
    }

    private static (ulong, VectorTileGeometryType, List<uint>, List<uint>) ParseFeature(CodedInputStream input)
    {
        ulong id = 0;
        var type = VectorTileGeometryType.Unknown;
        var tags = new List<uint>();
        var geometry = new List<uint>();

        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: id = input.ReadUInt64(); break;
                case 3: type = (VectorTileGeometryType)input.ReadEnum(); break;
                case 2: ReadRepeatedUInt32(input, tag, tags); break;
                case 4: ReadRepeatedUInt32(input, tag, geometry); break;
                default: input.SkipLastField(); break;
            }
        }
        return (id, type, tags, geometry);
    }

    /// <summary>Reads a repeated uint32 field. The spec marks these packed, but
    /// an unpacked encoding is still valid protobuf and some writers emit it,
    /// so both wire types are accepted.</summary>
    private static void ReadRepeatedUInt32(CodedInputStream input, uint tag, List<uint> into)
    {
        if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
        {
            var packed = input.ReadBytes().CreateCodedInput();
            while (!packed.IsAtEnd) into.Add(packed.ReadUInt32());
        }
        else into.Add(input.ReadUInt32());
    }

    /// <summary>Walks the command and parameter stream into parts. Coordinates
    /// are deltas from a cursor that persists across commands, and each MoveTo
    /// begins a new part.</summary>
    private static IReadOnlyList<TilePoint[]> DecodeGeometry(
        List<uint> geometry, VectorTileGeometryType type)
    {
        var parts = new List<TilePoint[]>();
        List<TilePoint>? current = null;
        int x = 0, y = 0, i = 0;

        void Flush()
        {
            if (current is { Count: > 0 }) parts.Add(current.ToArray());
            current = null;
        }

        while (i < geometry.Count)
        {
            uint header = geometry[i++];
            int command = (int)(header & 0x7);
            int count = (int)(header >> 3);

            switch (command)
            {
                case 1: // MoveTo
                    for (int k = 0; k < count && i + 1 < geometry.Count; k++)
                    {
                        // A multipoint is one MoveTo carrying every point, so
                        // each repeat has to start its own part.
                        if (type == VectorTileGeometryType.Point || k == 0) Flush();
                        x += Zigzag(geometry[i++]);
                        y += Zigzag(geometry[i++]);
                        (current ??= new List<TilePoint>()).Add(new TilePoint(x, y));
                    }
                    break;

                case 2: // LineTo
                    for (int k = 0; k < count && i + 1 < geometry.Count; k++)
                    {
                        x += Zigzag(geometry[i++]);
                        y += Zigzag(geometry[i++]);
                        (current ??= new List<TilePoint>()).Add(new TilePoint(x, y));
                    }
                    break;

                case 7: // ClosePath: the ring is implicitly closed, so the
                        // first point is not repeated; only the part ends.
                    Flush();
                    break;

                default: // An unknown command leaves the cursor unusable.
                    i = geometry.Count;
                    break;
            }
        }

        Flush();
        return parts;
    }

    private static int Zigzag(uint n) => (int)(n >> 1) ^ -(int)(n & 1);
}
