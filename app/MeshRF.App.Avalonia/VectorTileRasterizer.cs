// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using MeshRF.Map;

namespace MeshRF.AvaloniaApp;

/// <summary>Draws a vector tile into a raster tile.
///
/// The result is an ordinary bitmap, so it joins the same memory cache, disk
/// cache and draw path as a tile fetched from a raster provider: the map
/// control does not know the difference. Rasterising once per tile also keeps
/// the cost off the render loop, which redraws several times a second while
/// nodes are arriving and could not afford to re-tessellate this geometry each
/// time.
///
/// Labels are placed once per source tile and zoom, in a coordinate space the
/// output tiles share, so every tile magnified from one parent agrees about
/// which names are drawn and a name crossing a tile edge is drawn by both
/// sides. Icons are still not drawn: they need the style's sprite sheet.</summary>
internal static class VectorTileRasterizer
{
    /// <summary>Geometry beyond the tile edge by more than this many pixels
    /// cannot contribute ink, allowing for the widest strokes a style uses.</summary>
    private const double CullMargin = 32.0;

    public static Bitmap Render(
        VectorTile tile, MapStyle style, int zoom, int x, int y, int sourceMaxZoom, int size)
    {
        var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var context = target.CreateDrawingContext();

        var zoomOnly = new StyleFeatureContext(zoom);

        foreach (var layer in style.LayersAt(zoom))
        {
            if (!layer.IsVisible(zoomOnly)) continue;

            switch (layer.Type)
            {
                case MapStyleLayerType.Background:
                    DrawBackground(context, layer, zoomOnly, size);
                    break;

                case MapStyleLayerType.Fill:
                case MapStyleLayerType.Line:
                {
                    if (layer.SourceLayer is null) continue;
                    var source = tile.Layer(layer.SourceLayer);
                    if (source is null) continue;

                    var projection = TileProjection.For(
                        zoom, x, y, sourceMaxZoom, source.Extent, size);

                    if (layer.Type == MapStyleLayerType.Fill)
                        DrawFills(context, layer, source, projection, zoom, size);
                    else
                        DrawLines(context, layer, source, projection, zoom, size);
                    break;
                }

                // Raster layers are not drawn: the only one any OpenFreeMap
                // style declares is a low-zoom shaded relief underlay.
                default:
                    break;
            }
        }

        DrawLabels(context, tile, style, zoom, x, y, sourceMaxZoom, size);
        return target;
    }

    private static void DrawBackground(
        DrawingContext context, MapStyleLayer layer, in StyleFeatureContext ctx, int size)
    {
        var color = layer.Color("background-color", ctx, StyleColor.Black);
        double opacity = layer.Number("background-opacity", ctx, 1.0);
        if (Brush(color, opacity) is not { } brush) return;
        context.FillRectangle(brush, new Rect(0, 0, size, size));
    }

    private static void DrawFills(
        DrawingContext context, MapStyleLayer layer, VectorTileLayer source,
        TileProjection projection, int zoom, int size)
    {
        foreach (var feature in source.Features)
        {
            if (feature.Type != VectorTileGeometryType.Polygon) continue;

            var ctx = Context(zoom, feature);
            if (!layer.Accepts(ctx)) continue;

            var brush = Brush(layer.Color("fill-color", ctx, StyleColor.Black),
                              layer.Number("fill-opacity", ctx, 1.0));
            var outline = layer.Has("fill-outline-color")
                ? Brush(layer.Color("fill-outline-color", ctx, StyleColor.Transparent), 1.0)
                : null;
            if (brush is null && outline is null) continue;

            var geometry = BuildPolygon(feature, projection, size);
            if (geometry is null) continue;

            context.DrawGeometry(brush, outline is null ? null : new ImmutablePen(outline, 1.0), geometry);
        }
    }

    private static void DrawLines(
        DrawingContext context, MapStyleLayer layer, VectorTileLayer source,
        TileProjection projection, int zoom, int size)
    {
        foreach (var feature in source.Features)
        {
            // A polygon in a line layer is stroked around its rings.
            if (feature.Type is not (VectorTileGeometryType.LineString or VectorTileGeometryType.Polygon))
                continue;

            var ctx = Context(zoom, feature);
            if (!layer.Accepts(ctx)) continue;

            double width = layer.Number("line-width", ctx, 1.0);
            if (width <= 0) continue;

            var brush = Brush(layer.Color("line-color", ctx, StyleColor.Black),
                              layer.Number("line-opacity", ctx, 1.0));
            if (brush is null) continue;

            var geometry = BuildLine(feature, projection, size);
            if (geometry is null) continue;

            context.DrawGeometry(null, PenFor(layer, ctx, brush, width), geometry);
        }
    }

    // -- Geometry -----------------------------------------------------------

    private static StreamGeometry? BuildPolygon(
        VectorTileFeature feature, TileProjection projection, int size)
    {
        StreamGeometry? geometry = null;
        StreamGeometryContext? sink = null;

        foreach (var ring in feature.Parts)
        {
            if (ring.Length < 3) continue;
            if (!projection.Intersects(ring, size, CullMargin)) continue;

            if (sink is null)
            {
                geometry = new StreamGeometry();
                sink = geometry.Open();
                // Interior rings wind opposite their exterior, so non-zero
                // winding punches the holes without tracking which is which.
                sink.SetFillRule(FillRule.NonZero);
            }

            sink.BeginFigure(Map(projection, ring[0]), isFilled: true);
            for (int i = 1; i < ring.Length; i++) sink.LineTo(Map(projection, ring[i]));
            sink.EndFigure(isClosed: true);
        }

        sink?.Dispose();
        return geometry;
    }

    private static StreamGeometry? BuildLine(
        VectorTileFeature feature, TileProjection projection, int size)
    {
        StreamGeometry? geometry = null;
        StreamGeometryContext? sink = null;
        bool closed = feature.Type == VectorTileGeometryType.Polygon;

        foreach (var part in feature.Parts)
        {
            if (part.Length < 2) continue;
            if (!projection.Intersects(part, size, CullMargin)) continue;

            if (sink is null)
            {
                geometry = new StreamGeometry();
                sink = geometry.Open();
            }

            sink.BeginFigure(Map(projection, part[0]), isFilled: false);
            for (int i = 1; i < part.Length; i++) sink.LineTo(Map(projection, part[i]));
            sink.EndFigure(closed);
        }

        sink?.Dispose();
        return geometry;
    }

    private static Point Map(in TileProjection projection, TilePoint p) =>
        new(projection.MapX(p.X), projection.MapY(p.Y));

    // -- Paint --------------------------------------------------------------

    private static ImmutableSolidColorBrush? Brush(StyleColor color, double opacity)
    {
        double alpha = color.A * Math.Clamp(opacity, 0.0, 1.0);
        if (alpha <= 0.0) return null;
        return new ImmutableSolidColorBrush(
            Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255.0), 0, 255),
                           color.R8, color.G8, color.B8));
    }

    private static ImmutablePen PenFor(
        MapStyleLayer layer, in StyleFeatureContext ctx, IImmutableBrush brush, double width)
    {
        var cap = layer.Text("line-cap", ctx) switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Flat,
        };
        var join = layer.Text("line-join", ctx) switch
        {
            "bevel" => PenLineJoin.Bevel,
            "miter" => PenLineJoin.Miter,
            _ => PenLineJoin.Round,
        };

        // A style gives dash lengths in multiples of the line width, which is
        // also how a pen dash array is measured, so they carry across as-is.
        ImmutableDashStyle? dash = null;
        if (layer.Numbers("line-dasharray", ctx) is { Count: > 0 } dashes)
            dash = new ImmutableDashStyle(dashes, 0.0);

        return new ImmutablePen(brush, width, dash, cap, join);
    }

    // -- Labels -------------------------------------------------------------

    /// <summary>A name that won its space, positioned in the pixel space that
    /// the output tiles of one source tile share.</summary>
    private sealed record LabelPlacement(
        string Text, double X, double Y, double Size, double Rotation,
        IImmutableBrush Fill, ImmutablePen? Halo, LabelBox Box);

    /// <summary>The style asks for Noto Sans, which is published only as the
    /// signed-distance-field glyphs a GPU renderer needs. Drawing through a
    /// real text engine instead, the font the app already ships serves.</summary>
    private static readonly Typeface LabelTypeface = Typeface.Default;

    /// <summary>Placements per source tile and zoom. Every output tile
    /// magnified from one parent needs the same set, and at the deepest zoom
    /// that is more than a thousand tiles, so the work is done once.</summary>
    private const int MaxPlacementSets = 24;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<LabelPlacement>> s_placements = new();
    private static readonly ConcurrentQueue<string> s_placementOrder = new();

    private static void DrawLabels(
        DrawingContext context, VectorTile tile, MapStyle style,
        int zoom, int x, int y, int sourceMaxZoom, int size)
    {
        var (sourceZoom, sourceX, sourceY) = TileProjection.SourceTile(zoom, x, y, sourceMaxZoom);
        var key = $"{style.Name}_{sourceZoom}_{sourceX}_{sourceY}_{zoom}_{size}";

        if (!s_placements.TryGetValue(key, out var placements))
        {
            placements = PlaceLabels(tile, style, zoom, sourceMaxZoom, size);
            if (s_placements.TryAdd(key, placements))
            {
                s_placementOrder.Enqueue(key);
                while (s_placements.Count > MaxPlacementSets
                       && s_placementOrder.TryDequeue(out var oldest))
                    s_placements.TryRemove(oldest, out _);
            }
        }

        // Which slice of the shared space this tile shows.
        var projection = TileProjection.For(zoom, x, y, sourceMaxZoom, 4096, size);
        double offsetX = projection.OffsetX, offsetY = projection.OffsetY;

        foreach (var label in placements)
        {
            if (!label.Box.IntersectsTile(offsetX, offsetY, size)) continue;

            var text = new FormattedText(
                label.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, label.Size, label.Fill);

            double px = label.X - offsetX, py = label.Y - offsetY;
            var origin = new Point(px - text.Width / 2, py - text.Height / 2);

            using var rotated = label.Rotation != 0
                ? context.PushTransform(
                    Matrix.CreateTranslation(-px, -py)
                    * Matrix.CreateRotation(label.Rotation)
                    * Matrix.CreateTranslation(px, py))
                : default;

            if (label.Halo is { } halo)
            {
                // The halo is the text outline stroked behind the fill, which
                // is what keeps a name legible over whatever it crosses.
                var geometry = text.BuildGeometry(origin);
                if (geometry is not null) context.DrawGeometry(null, halo, geometry);
            }

            context.DrawText(text, origin);
        }
    }

    private static IReadOnlyList<LabelPlacement> PlaceLabels(
        VectorTile tile, MapStyle style, int zoom, int sourceMaxZoom, int size)
    {
        var placed = new List<LabelPlacement>();
        var collisions = new LabelCollisionMap(padding: 2.0);
        int magnification = 1 << Math.Max(0, zoom - sourceMaxZoom);

        foreach (var layer in style.LayersAt(zoom))
        {
            if (layer.Type != MapStyleLayerType.Symbol || layer.SourceLayer is null) continue;
            var source = tile.Layer(layer.SourceLayer);
            if (source is null) continue;

            // Tile-local units to the shared pixel space. No slice offset, so
            // the space covers the whole source tile.
            double scale = (double)magnification * size / source.Extent;

            foreach (var feature in source.Features)
            {
                var ctx = Context(zoom, feature);
                if (!layer.Accepts(ctx) || !layer.IsVisible(ctx)) continue;

                var content = LabelText(layer, ctx);
                if (content is null) continue;

                double fontSize = layer.Number("text-size", ctx, 12.0);
                if (fontSize <= 0) continue;

                if (!Anchor(feature, scale, out double ax, out double ay, out double rotation))
                    continue;

                // text-offset is measured in ems, as the style spec defines it.
                if (layer.Numbers("text-offset", ctx) is { Count: >= 2 } offset)
                {
                    ax += offset[0] * fontSize;
                    ay += offset[1] * fontSize;
                }

                var measured = new FormattedText(
                    content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    LabelTypeface, fontSize, Brushes.White);

                var box = LabelBox.Centered(ax, ay, measured.Width, measured.Height);
                if (!collisions.TryPlace(box)) continue;

                var fill = Brush(layer.Color("text-color", ctx, StyleColor.Black), 1.0);
                if (fill is null) continue;

                ImmutablePen? halo = null;
                double haloWidth = layer.Number("text-halo-width", ctx, 0.0);
                if (haloWidth > 0
                    && Brush(layer.Color("text-halo-color", ctx, StyleColor.Transparent), 1.0) is { } haloBrush)
                {
                    // A halo is given as a radius and a stroke straddles the
                    // outline it follows, so it is drawn at twice the width.
                    halo = new ImmutablePen(haloBrush, haloWidth * 2,
                        lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                }

                placed.Add(new LabelPlacement(content, ax, ay, fontSize, rotation, fill, halo, box));
            }
        }

        return placed;
    }

    private static string? LabelText(MapStyleLayer layer, in StyleFeatureContext ctx)
    {
        var text = layer.Text("text-field", ctx);
        if (string.IsNullOrWhiteSpace(text)) return null;

        // A style joins a latin and a local name with a newline; one line
        // reads better at tile scale.
        text = text.Replace('\n', ' ').Trim();
        if (text.Length == 0) return null;

        return layer.Text("text-transform", ctx) switch
        {
            "uppercase" => text.ToUpperInvariant(),
            "lowercase" => text.ToLowerInvariant(),
            _ => text,
        };
    }

    /// <summary>Where a label sits, and at what angle. A point feature takes
    /// its own position; a line takes the middle of its longest part and the
    /// bearing there, so a street name runs along its street.</summary>
    private static bool Anchor(
        VectorTileFeature feature, double scale,
        out double x, out double y, out double rotation)
    {
        x = y = rotation = 0;
        if (feature.Parts.Count == 0 || feature.Parts[0].Length == 0) return false;

        if (feature.Type == VectorTileGeometryType.Point)
        {
            var p = feature.Parts[0][0];
            x = p.X * scale;
            y = p.Y * scale;
            return true;
        }

        TilePoint[]? longest = null;
        double longestLength = -1;
        foreach (var part in feature.Parts)
        {
            if (part.Length < 2) continue;
            double length = 0;
            for (int i = 1; i < part.Length; i++)
            {
                double dx = part[i].X - part[i - 1].X, dy = part[i].Y - part[i - 1].Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }
            if (length > longestLength) { longestLength = length; longest = part; }
        }
        if (longest is null) return false;

        int mid = longest.Length / 2;
        var a = longest[Math.Max(0, mid - 1)];
        var b = longest[Math.Min(longest.Length - 1, mid)];

        x = (a.X + b.X) / 2.0 * scale;
        y = (a.Y + b.Y) / 2.0 * scale;

        rotation = Math.Atan2(b.Y - a.Y, b.X - a.X);
        // Keep text upright: a bearing past a quarter turn would read upside
        // down, so the line is followed the other way instead.
        if (rotation > Math.PI / 2) rotation -= Math.PI;
        else if (rotation < -Math.PI / 2) rotation += Math.PI;
        return true;
    }

    private static StyleFeatureContext Context(int zoom, VectorTileFeature feature) =>
        new(zoom, feature.Attributes,
            StyleFeatureContext.GeometryTypeName(feature.Type, feature.Parts.Count));
}
