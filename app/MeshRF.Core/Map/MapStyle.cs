// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace MeshRF.Map;

public enum MapStyleLayerType
{
    Background,
    Fill,
    Line,
    Symbol,
    Raster,
}

/// <summary>Where a style's tiles come from. A vector source names a TileJSON
/// document rather than a tile URL: the OpenFreeMap tile path carries a dated
/// build in it, so the template has to be resolved at runtime rather than
/// baked in.</summary>
public sealed class MapStyleSource
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> Tiles { get; init; } = [];
    public int MinZoom { get; init; }
    public int MaxZoom { get; init; } = 22;
    public int TileSize { get; init; } = 512;

    public bool IsVector => string.Equals(Type, "vector", StringComparison.Ordinal);
}

/// <summary>One drawing rule: which features it selects, and the properties
/// that decide how they look. Filter, paint and layout are compiled
/// expressions, so evaluating them per feature costs no parsing.</summary>
public sealed class MapStyleLayer
{
    public required string Id { get; init; }
    public required MapStyleLayerType Type { get; init; }
    public string? Source { get; init; }
    public string? SourceLayer { get; init; }
    public double MinZoom { get; init; }
    public double MaxZoom { get; init; } = 24.0;
    public StyleExpression? Filter { get; init; }
    public required IReadOnlyDictionary<string, StyleExpression> Paint { get; init; }
    public required IReadOnlyDictionary<string, StyleExpression> Layout { get; init; }

    /// <summary>Whether the layer draws at this zoom. The upper bound is
    /// exclusive, matching the style spec, so adjacent layers that hand over at
    /// a zoom do not both draw on that level.</summary>
    public bool AppliesAt(double zoom) => zoom >= MinZoom && zoom < MaxZoom;

    /// <summary>Whether this feature passes the layer filter. A layer with no
    /// filter takes everything in its source layer.</summary>
    public bool Accepts(in StyleFeatureContext ctx) =>
        Filter is null || Filter.AsBoolean(ctx);

    /// <summary>A layout property marked "none" switches the layer off.</summary>
    public bool IsVisible(in StyleFeatureContext ctx) =>
        !string.Equals(Text("visibility", ctx), "none", StringComparison.Ordinal);

    private StyleExpression? Property(string name) =>
        Layout.TryGetValue(name, out var l) ? l :
        Paint.TryGetValue(name, out var p) ? p : null;

    public bool Has(string name) => Property(name) is not null;

    public double Number(string name, in StyleFeatureContext ctx, double fallback = 0.0) =>
        Property(name)?.AsNumber(ctx, fallback) ?? fallback;

    public StyleColor Color(string name, in StyleFeatureContext ctx, StyleColor fallback) =>
        Property(name)?.AsColor(ctx, fallback) ?? fallback;

    public string? Text(string name, in StyleFeatureContext ctx) =>
        Property(name)?.AsString(ctx);

    public bool Boolean(string name, in StyleFeatureContext ctx, bool fallback = false) =>
        Property(name)?.AsBoolean(ctx, fallback) ?? fallback;

    /// <summary>A property that is a list of numbers, as line-dasharray and
    /// text-offset are.</summary>
    public IReadOnlyList<double>? Numbers(string name, in StyleFeatureContext ctx)
    {
        if (Property(name)?.Evaluate(ctx) is not IReadOnlyList<object?> list) return null;
        var result = new double[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            if (StyleExpression.ToNumber(list[i]) is not { } n) return null;
            result[i] = n;
        }
        return result;
    }

    /// <summary>A property that is a list of strings, as text-font is.</summary>
    public IReadOnlyList<string>? Strings(string name, in StyleFeatureContext ctx)
    {
        if (Property(name)?.Evaluate(ctx) is not IReadOnlyList<object?> list) return null;
        var result = new List<string>(list.Count);
        foreach (var v in list)
            if (StyleExpression.ToText(v) is { } s) result.Add(s);
        return result;
    }
}

/// <summary>A parsed MapLibre style document.
///
/// A layer whose expressions this evaluator cannot compile is dropped and
/// recorded in <see cref="Diagnostics"/> rather than failing the whole style:
/// losing one layer leaves a map missing some detail, where throwing leaves no
/// map at all.</summary>
public sealed class MapStyle
{
    public string Name { get; init; } = string.Empty;
    public required IReadOnlyList<MapStyleLayer> Layers { get; init; }
    public required IReadOnlyDictionary<string, MapStyleSource> Sources { get; init; }
    public string? SpriteUrl { get; init; }
    public string? GlyphsUrl { get; init; }
    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>The layers that draw at a zoom, in the order the style lists
    /// them, which is the order they must be painted in.</summary>
    public IEnumerable<MapStyleLayer> LayersAt(double zoom)
    {
        foreach (var layer in Layers)
            if (layer.AppliesAt(zoom)) yield return layer;
    }

    /// <summary>The first vector source, which is the basemap data.</summary>
    public MapStyleSource? VectorSource()
    {
        foreach (var s in Sources.Values)
            if (s.IsVector) return s;
        return null;
    }

    public static MapStyle Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var diagnostics = new List<string>();
        var sources = new Dictionary<string, MapStyleSource>(StringComparer.Ordinal);
        if (root.TryGetProperty("sources", out var sourcesNode)
            && sourcesNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var s in sourcesNode.EnumerateObject())
                sources[s.Name] = ParseSource(s.Name, s.Value);
        }

        var layers = new List<MapStyleLayer>();
        if (root.TryGetProperty("layers", out var layersNode)
            && layersNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in layersNode.EnumerateArray())
            {
                var id = l.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "?" : "?";
                try
                {
                    if (ParseLayer(id, l) is { } layer) layers.Add(layer);
                    else diagnostics.Add($"{id}: unsupported layer type");
                }
                catch (StyleExpressionException ex)
                {
                    diagnostics.Add($"{id}: {ex.Message}");
                }
            }
        }

        return new MapStyle
        {
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
            Layers = layers,
            Sources = sources,
            SpriteUrl = root.TryGetProperty("sprite", out var sp) ? sp.GetString() : null,
            GlyphsUrl = root.TryGetProperty("glyphs", out var gl) ? gl.GetString() : null,
            Diagnostics = diagnostics,
        };
    }

    private static MapStyleSource ParseSource(string id, JsonElement e)
    {
        var tiles = new List<string>();
        if (e.TryGetProperty("tiles", out var t) && t.ValueKind == JsonValueKind.Array)
            foreach (var u in t.EnumerateArray())
                if (u.GetString() is { } s) tiles.Add(s);

        return new MapStyleSource
        {
            Id = id,
            Type = e.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "",
            Url = e.TryGetProperty("url", out var url) ? url.GetString() : null,
            Tiles = tiles,
            MinZoom = Int(e, "minzoom", 0),
            MaxZoom = Int(e, "maxzoom", 22),
            TileSize = Int(e, "tileSize", 512),
        };
    }

    private static MapStyleLayer? ParseLayer(string id, JsonElement e)
    {
        var typeName = e.TryGetProperty("type", out var t) ? t.GetString() : null;
        MapStyleLayerType type;
        switch (typeName)
        {
            case "background": type = MapStyleLayerType.Background; break;
            case "fill": type = MapStyleLayerType.Fill; break;
            case "line": type = MapStyleLayerType.Line; break;
            case "symbol": type = MapStyleLayerType.Symbol; break;
            case "raster": type = MapStyleLayerType.Raster; break;
            default: return null;   // fill-extrusion, heatmap, hillshade, circle
        }

        return new MapStyleLayer
        {
            Id = id,
            Type = type,
            Source = e.TryGetProperty("source", out var s) ? s.GetString() : null,
            SourceLayer = e.TryGetProperty("source-layer", out var sl) ? sl.GetString() : null,
            MinZoom = Double(e, "minzoom", 0.0),
            MaxZoom = Double(e, "maxzoom", 24.0),
            Filter = e.TryGetProperty("filter", out var f) ? StyleExpression.Parse(f) : null,
            Paint = ParseProperties(e, "paint"),
            Layout = ParseProperties(e, "layout"),
        };
    }

    private static IReadOnlyDictionary<string, StyleExpression> ParseProperties(
        JsonElement layer, string section)
    {
        if (!layer.TryGetProperty(section, out var node) || node.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, StyleExpression>(0, StringComparer.Ordinal);

        var result = new Dictionary<string, StyleExpression>(StringComparer.Ordinal);
        foreach (var p in node.EnumerateObject()) result[p.Name] = StyleExpression.Parse(p.Value);
        return result;
    }

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : fallback;

    private static double Double(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : fallback;
}
