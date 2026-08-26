// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

public class StyleExpressionTests
{
    private static StyleFeatureContext Ctx(
        double zoom = 14, string geometryType = "LineString", params (string Key, object? Value)[] attrs)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in attrs) d[k] = v;
        return new StyleFeatureContext(zoom, d, geometryType);
    }

    private static object? Eval(string json, StyleFeatureContext ctx) =>
        StyleExpression.Parse(json).Evaluate(ctx);

    // -- Data access --------------------------------------------------------

    [Fact]
    public void GetReadsFeatureAttributesAndMissingKeysAreNull()
    {
        var ctx = Ctx(attrs: [("class", "motorway")]);
        Assert.Equal("motorway", Eval("[\"get\",\"class\"]", ctx));
        Assert.Null(Eval("[\"get\",\"absent\"]", ctx));
    }

    [Fact]
    public void HasDistinguishesMissingFromNullValued()
    {
        var ctx = Ctx(attrs: [("brunnel", null)]);
        Assert.Equal(true, Eval("[\"has\",\"brunnel\"]", ctx));
        Assert.Equal(false, Eval("[\"has\",\"other\"]", ctx));
    }

    [Fact]
    public void ZoomAndGeometryTypeComeFromContext()
    {
        Assert.Equal(11.5, Eval("[\"zoom\"]", Ctx(zoom: 11.5)));
        Assert.Equal("Polygon", Eval("[\"geometry-type\"]", Ctx(geometryType: "Polygon")));
    }

    [Theory]
    [InlineData(VectorTileGeometryType.Point, 1, "Point")]
    [InlineData(VectorTileGeometryType.Point, 3, "MultiPoint")]
    [InlineData(VectorTileGeometryType.LineString, 1, "LineString")]
    [InlineData(VectorTileGeometryType.LineString, 2, "MultiLineString")]
    [InlineData(VectorTileGeometryType.Polygon, 1, "Polygon")]
    [InlineData(VectorTileGeometryType.Polygon, 2, "MultiPolygon")]
    public void GeometryTypeNameReportsMultiPartForms(
        VectorTileGeometryType type, int parts, string expected) =>
        Assert.Equal(expected, StyleFeatureContext.GeometryTypeName(type, parts));

    // -- Branching ----------------------------------------------------------

    [Fact]
    public void CaseTakesFirstTrueBranchOtherwiseFallback()
    {
        const string expr = "[\"case\",[\"has\",\"a\"],\"first\",[\"has\",\"b\"],\"second\",\"none\"]";
        Assert.Equal("first", Eval(expr, Ctx(attrs: [("a", 1L)])));
        Assert.Equal("second", Eval(expr, Ctx(attrs: [("b", 1L)])));
        Assert.Equal("none", Eval(expr, Ctx()));
    }

    [Fact]
    public void MatchAcceptsSingleLabelsAndArraysOfAlternatives()
    {
        // The shape every geometry filter in the style uses.
        const string expr =
            "[\"match\",[\"geometry-type\"],[\"MultiPolygon\",\"Polygon\"],true,false]";
        Assert.Equal(true, Eval(expr, Ctx(geometryType: "Polygon")));
        Assert.Equal(true, Eval(expr, Ctx(geometryType: "MultiPolygon")));
        Assert.Equal(false, Eval(expr, Ctx(geometryType: "LineString")));

        const string single = "[\"match\",[\"get\",\"class\"],\"primary\",2.0,\"minor\",1.0,0.0]";
        Assert.Equal(2.0, Eval(single, Ctx(attrs: [("class", "primary")])));
        Assert.Equal(1.0, Eval(single, Ctx(attrs: [("class", "minor")])));
        Assert.Equal(0.0, Eval(single, Ctx(attrs: [("class", "other")])));
    }

    [Fact]
    public void StepHoldsEachOutputUntilTheNextStop()
    {
        const string expr = "[\"step\",[\"zoom\"],0.0,10,1.0,14,2.0]";
        Assert.Equal(0.0, Eval(expr, Ctx(zoom: 9.99)));
        Assert.Equal(1.0, Eval(expr, Ctx(zoom: 10)));
        Assert.Equal(1.0, Eval(expr, Ctx(zoom: 13.99)));
        Assert.Equal(2.0, Eval(expr, Ctx(zoom: 14)));
        Assert.Equal(2.0, Eval(expr, Ctx(zoom: 22)));
    }

    // -- Interpolation ------------------------------------------------------

    [Fact]
    public void LinearInterpolationRampsBetweenStops()
    {
        const string expr = "[\"interpolate\",[\"linear\"],[\"zoom\"],10,1,20,11]";
        Assert.Equal(6.0, (double)Eval(expr, Ctx(zoom: 15))!, 9);
    }

    [Fact]
    public void ExponentialInterpolationMatchesTheStyleSpecCurve()
    {
        // Taken from the dark style's line-width, with the expected value
        // computed from the spec formula rather than from this implementation.
        const string expr = "[\"interpolate\",[\"exponential\",1.55],[\"zoom\"],13,1.8,20,20]";
        Assert.Equal(4.21895038487192, (double)Eval(expr, Ctx(zoom: 16))!, 9);
    }

    [Fact]
    public void InterpolationClampsOutsideItsStops()
    {
        const string expr = "[\"interpolate\",[\"exponential\",1.55],[\"zoom\"],13,1.8,20,20]";
        Assert.Equal(1.8, (double)Eval(expr, Ctx(zoom: 10))!, 9);
        Assert.Equal(20.0, (double)Eval(expr, Ctx(zoom: 22))!, 9);
    }

    [Fact]
    public void InterpolationBlendsColours()
    {
        const string expr = "[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#000000\",10,\"#ffffff\"]";
        var mid = Assert.IsType<StyleColor>(Eval(expr, Ctx(zoom: 5)));
        Assert.Equal(128, mid.R8);
        Assert.Equal(128, mid.G8);
        Assert.Equal(1.0, mid.A, 9);
    }

    // -- Strings ------------------------------------------------------------

    [Fact]
    public void CoalesceSkipsNullAndEmptyValues()
    {
        const string expr = "[\"coalesce\",[\"get\",\"name_en\"],[\"get\",\"name\"]]";
        Assert.Equal("Market Street", Eval(expr, Ctx(attrs: [("name", "Market Street")])));
        Assert.Equal("Market St", Eval(expr, Ctx(attrs: [("name_en", "Market St"), ("name", "Market Street")])));
        Assert.Null(Eval(expr, Ctx()));
    }

    [Fact]
    public void ConcatAndToStringBuildLabelText()
    {
        var ctx = Ctx(attrs: [("name:latin", "Tokyo"), ("name:nonlatin", "東京"), ("ref", 101L)]);
        Assert.Equal("Tokyo 東京",
            Eval("[\"concat\",[\"get\",\"name:latin\"],\" \",[\"get\",\"name:nonlatin\"]]", ctx));
        Assert.Equal("101", Eval("[\"to-string\",[\"get\",\"ref\"]]", ctx));
    }

    // -- Comparison and logic -----------------------------------------------

    [Fact]
    public void ComparisonsHandleMixedNumericTypesFromTiles()
    {
        // A tile may deliver the same attribute as int64 or double.
        Assert.Equal(true, Eval("[\"==\",[\"get\",\"layer\"],3]", Ctx(attrs: [("layer", 3L)])));
        Assert.Equal(true, Eval("[\"==\",[\"get\",\"layer\"],3]", Ctx(attrs: [("layer", 3.0)])));
        Assert.Equal(true, Eval("[\"!=\",[\"get\",\"brunnel\"],\"tunnel\"]", Ctx(attrs: [("brunnel", "bridge")])));
        Assert.Equal(true, Eval("[\"<=\",[\"get\",\"level\"],0]", Ctx(attrs: [("level", -3L)])));
        Assert.Equal(true, Eval("[\">\",[\"get\",\"rank\"],2]", Ctx(attrs: [("rank", 5L)])));
    }

    [Fact]
    public void OrderedComparisonAgainstAMissingAttributeIsFalseNotAnError()
    {
        Assert.Equal(false, Eval("[\"<=\",[\"get\",\"level\"],0]", Ctx()));
        Assert.Equal(false, Eval("[\">\",[\"get\",\"level\"],0]", Ctx()));
    }

    [Fact]
    public void LogicalOperatorsCombineFilters()
    {
        var ctx = Ctx(geometryType: "Polygon", attrs: [("class", "residential")]);
        const string expr =
            "[\"all\",[\"match\",[\"geometry-type\"],[\"MultiPolygon\",\"Polygon\"],true,false]," +
            "[\"==\",[\"get\",\"class\"],\"residential\"]]";
        Assert.Equal(true, Eval(expr, ctx));
        Assert.Equal(false, Eval("[\"!\",[\"has\",\"class\"]]", ctx));
        Assert.Equal(true, Eval("[\"any\",[\"has\",\"missing\"],[\"has\",\"class\"]]", ctx));
    }

    // -- Colours ------------------------------------------------------------

    [Theory]
    [InlineData("#000", 0, 0, 0, 1.0)]
    [InlineData("#181818", 24, 24, 24, 1.0)]
    [InlineData("rgb(35,35,35)", 35, 35, 35, 1.0)]
    [InlineData("rgba(35,35,35,0.5)", 35, 35, 35, 0.5)]
    [InlineData("hsla(0,0%,85%,0.53)", 217, 217, 217, 0.53)]
    [InlineData("hsl(120,100%,50%)", 0, 255, 0, 1.0)]
    [InlineData("#80808080", 128, 128, 128, 0.502)]
    public void ParsesTheColourFormsTheStyleUses(
        string text, int r, int g, int b, double a)
    {
        Assert.True(StyleColor.TryParse(text, out var c));
        Assert.Equal(r, c.R8);
        Assert.Equal(g, c.G8);
        Assert.Equal(b, c.B8);
        Assert.Equal(a, c.A, 2);
    }

    [Fact]
    public void RejectsMalformedColours()
    {
        Assert.False(StyleColor.TryParse("#12345", out _));
        Assert.False(StyleColor.TryParse("not-a-colour", out _));
        Assert.False(StyleColor.TryParse(null, out _));
    }

    // -- Failure modes ------------------------------------------------------

    [Fact]
    public void UnsupportedOperatorThrowsAtParseTimeNamingTheOperator()
    {
        var ex = Assert.Throws<StyleExpressionException>(
            () => StyleExpression.Parse("[\"cubic-bezier-magic\",1,2]"));
        Assert.Contains("cubic-bezier-magic", ex.Message);
    }

    [Fact]
    public void LegacyStopsFunctionsAreRejectedExplicitly()
    {
        var ex = Assert.Throws<StyleExpressionException>(
            () => StyleExpression.Parse("{\"base\":1.2,\"stops\":[[10,1],[20,2]]}"));
        Assert.Contains("stops", ex.Message);
    }

    [Fact]
    public void StringArraysThatAreNotOperatorCallsEvaluateAsValues()
    {
        // text-font is a list of font names, spelled exactly like an operator
        // call. Every OpenFreeMap style carries one.
        var fonts = Assert.IsAssignableFrom<IReadOnlyList<object?>>(
            Eval("[\"Noto Sans Regular\"]", Ctx()));
        Assert.Equal("Noto Sans Regular", Assert.Single(fonts));

        var two = Assert.IsAssignableFrom<IReadOnlyList<object?>>(
            Eval("[\"Noto Sans Italic\",\"Noto Sans Regular\"]", Ctx()));
        Assert.Equal(2, two.Count);
    }

    [Fact]
    public void AMisspelledOperatorWithNonStringArgumentsStillFails()
    {
        // The literal-array rule must not swallow a genuine typo.
        Assert.Throws<StyleExpressionException>(
            () => StyleExpression.Parse("[\"interpolat\",[\"linear\"],[\"zoom\"],0,1]"));
    }

    [Fact]
    public void BareArraysEvaluateAsValues()
    {
        // line-dasharray is a plain array, not an operator call.
        var v = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Eval("[1.5,1.5]", Ctx()));
        Assert.Equal(2, v.Count);
        Assert.Equal(1.5, v[0]);
    }
}
