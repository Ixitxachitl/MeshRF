// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

public class MapStyleTests
{
    private const string Sample = """
    {
      "name": "Test",
      "glyphs": "https://example.test/fonts/{fontstack}/{range}.pbf",
      "sprite": "https://example.test/sprites/ofm",
      "sources": {
        "shade": { "type": "raster", "tileSize": 256, "maxzoom": 6,
                   "tiles": ["https://example.test/ne2/{z}/{x}/{y}.png"] },
        "openmaptiles": { "type": "vector", "url": "https://example.test/planet" }
      },
      "layers": [
        { "id": "background", "type": "background", "paint": { "background-color": "#0c0c0c" } },
        { "id": "water", "type": "fill", "source": "openmaptiles", "source-layer": "water",
          "filter": ["match", ["geometry-type"], ["MultiPolygon", "Polygon"], true, false],
          "paint": { "fill-color": "rgb(20,30,40)", "fill-opacity": 0.5 } },
        { "id": "roads", "type": "line", "source": "openmaptiles", "source-layer": "transportation",
          "minzoom": 11, "maxzoom": 16,
          "filter": ["==", ["get", "class"], "motorway"],
          "layout": { "line-join": "round", "line-cap": "butt" },
          "paint": { "line-color": "#ffffff",
                     "line-width": ["interpolate", ["exponential", 1.55], ["zoom"], 13, 1.8, 20, 20],
                     "line-dasharray": [1.5, 1.5] } },
        { "id": "labels", "type": "symbol", "source": "openmaptiles", "source-layer": "place",
          "layout": { "text-field": ["get", "name"], "text-font": ["Noto Sans Regular"],
                      "text-size": 12 } },
        { "id": "hidden", "type": "line", "source": "openmaptiles", "source-layer": "transportation",
          "layout": { "visibility": "none" } },
        { "id": "extruded", "type": "fill-extrusion", "source": "openmaptiles",
          "source-layer": "building" }
      ]
    }
    """;

    private static MapStyle Parsed() => MapStyle.Parse(Sample);

    private static StyleFeatureContext Ctx(
        double zoom, string geometryType = "LineString", params (string, object?)[] attrs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in attrs) d[k] = v;
        return new StyleFeatureContext(zoom, d, geometryType);
    }

    // -- Document -----------------------------------------------------------

    [Fact]
    public void ParsesNameSourcesAndAssetUrls()
    {
        var s = Parsed();
        Assert.Equal("Test", s.Name);
        Assert.Equal(2, s.Sources.Count);
        Assert.Contains("{fontstack}", s.GlyphsUrl);
        Assert.Equal("https://example.test/sprites/ofm", s.SpriteUrl);

        var raster = s.Sources["shade"];
        Assert.False(raster.IsVector);
        Assert.Equal(256, raster.TileSize);
        Assert.Equal(6, raster.MaxZoom);
        Assert.Equal("https://example.test/ne2/{z}/{x}/{y}.png", Assert.Single(raster.Tiles));
    }

    [Fact]
    public void VectorSourceIsFoundRegardlessOfDeclarationOrder()
    {
        var v = Parsed().VectorSource();
        Assert.NotNull(v);
        Assert.Equal("openmaptiles", v.Id);
        Assert.Equal("https://example.test/planet", v.Url);
    }

    [Fact]
    public void UnsupportedLayerTypesAreDroppedAndReported()
    {
        var s = Parsed();
        Assert.DoesNotContain(s.Layers, l => l.Id == "extruded");
        Assert.Contains(s.Diagnostics, d => d.Contains("extruded") && d.Contains("unsupported"));
        // The rest of the style survives.
        Assert.Equal(5, s.Layers.Count);
    }

    [Fact]
    public void ALayerWithAnUnsupportedExpressionIsDroppedNotFatal()
    {
        const string json = """
        { "layers": [
            { "id": "good", "type": "background", "paint": { "background-color": "#000" } },
            { "id": "bad", "type": "line", "source-layer": "x",
              "paint": { "line-width": ["totally-made-up", 1, 2] } }
        ] }
        """;
        var s = MapStyle.Parse(json);
        Assert.Equal("good", Assert.Single(s.Layers).Id);
        Assert.Contains(s.Diagnostics, d => d.Contains("bad") && d.Contains("totally-made-up"));
    }

    // -- Zoom range ---------------------------------------------------------

    [Fact]
    public void ZoomRangeIncludesMinimumAndExcludesMaximum()
    {
        var roads = Parsed().Layers.First(l => l.Id == "roads");
        Assert.False(roads.AppliesAt(10.9));
        Assert.True(roads.AppliesAt(11));
        Assert.True(roads.AppliesAt(15.9));
        Assert.False(roads.AppliesAt(16));
    }

    [Fact]
    public void LayersAtPreservesStyleOrderWhichIsPaintOrder()
    {
        var ids = Parsed().LayersAt(14).Select(l => l.Id).ToList();
        Assert.Equal(["background", "water", "roads", "labels", "hidden"], ids);
        // Outside the roads range it drops out, the rest keep their order.
        Assert.Equal(["background", "water", "labels", "hidden"],
            Parsed().LayersAt(17).Select(l => l.Id).ToList());
    }

    // -- Filtering ----------------------------------------------------------

    [Fact]
    public void FiltersSelectFeatures()
    {
        var roads = Parsed().Layers.First(l => l.Id == "roads");
        Assert.True(roads.Accepts(Ctx(14, attrs: ("class", "motorway"))));
        Assert.False(roads.Accepts(Ctx(14, attrs: ("class", "minor"))));

        var water = Parsed().Layers.First(l => l.Id == "water");
        Assert.True(water.Accepts(Ctx(14, "Polygon")));
        Assert.False(water.Accepts(Ctx(14, "LineString")));
    }

    [Fact]
    public void ALayerWithoutAFilterTakesEverything()
    {
        var labels = Parsed().Layers.First(l => l.Id == "labels");
        Assert.Null(labels.Filter);
        Assert.True(labels.Accepts(Ctx(14, "Point")));
    }

    [Fact]
    public void VisibilityNoneSwitchesALayerOff()
    {
        var s = Parsed();
        Assert.False(s.Layers.First(l => l.Id == "hidden").IsVisible(Ctx(14)));
        Assert.True(s.Layers.First(l => l.Id == "roads").IsVisible(Ctx(14)));
    }

    // -- Properties ---------------------------------------------------------

    [Fact]
    public void PaintAndLayoutPropertiesResolveThroughOneLookup()
    {
        var roads = Parsed().Layers.First(l => l.Id == "roads");
        var ctx = Ctx(16, attrs: ("class", "motorway"));

        Assert.Equal("round", roads.Text("line-join", ctx));      // layout
        Assert.Equal(255, roads.Color("line-color", ctx, StyleColor.Black).R8);  // paint
        Assert.Equal(4.21895038487192, roads.Number("line-width", ctx), 9);
        Assert.True(roads.Has("line-cap"));
        Assert.False(roads.Has("line-blur"));
    }

    [Fact]
    public void MissingPropertiesFallBack()
    {
        var roads = Parsed().Layers.First(l => l.Id == "roads");
        var ctx = Ctx(14, attrs: ("class", "motorway"));
        Assert.Equal(2.5, roads.Number("line-blur", ctx, 2.5));
        Assert.Equal(StyleColor.Black, roads.Color("line-gap-color", ctx, StyleColor.Black));
        Assert.Null(roads.Text("text-field", ctx));
        Assert.Null(roads.Numbers("line-offset", ctx));
    }

    [Fact]
    public void NumericAndStringListPropertiesDecode()
    {
        var s = Parsed();
        var ctx = Ctx(14, attrs: ("class", "motorway"));

        var dash = s.Layers.First(l => l.Id == "roads").Numbers("line-dasharray", ctx);
        Assert.Equal([1.5, 1.5], dash);

        var fonts = s.Layers.First(l => l.Id == "labels").Strings("text-font", ctx);
        Assert.Equal("Noto Sans Regular", Assert.Single(fonts!));
    }

    [Fact]
    public void OpacityAndColourFunctionsEvaluateAgainstTheFeature()
    {
        var water = Parsed().Layers.First(l => l.Id == "water");
        var ctx = Ctx(14, "Polygon");
        Assert.Equal(0.5, water.Number("fill-opacity", ctx, 1.0), 9);
        var c = water.Color("fill-color", ctx, StyleColor.Black);
        Assert.Equal(20, c.R8);
        Assert.Equal(30, c.G8);
        Assert.Equal(40, c.B8);
    }

    [Fact]
    public void BackgroundLayerCarriesItsColour()
    {
        var bg = Parsed().Layers.First(l => l.Id == "background");
        Assert.Equal(MapStyleLayerType.Background, bg.Type);
        Assert.Equal(12, bg.Color("background-color", Ctx(14), StyleColor.Transparent).R8);
    }

    [Fact]
    public void AnEmptyStyleParsesToNothingRatherThanThrowing()
    {
        var s = MapStyle.Parse("{}");
        Assert.Empty(s.Layers);
        Assert.Empty(s.Sources);
        Assert.Null(s.VectorSource());
    }
}
