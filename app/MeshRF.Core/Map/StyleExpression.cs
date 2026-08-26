// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MeshRF.Map;

/// <summary>Raised when a style uses an expression operator this evaluator does
/// not implement. Thrown while parsing rather than while drawing, so an
/// unsupported layer is dropped at load with a name attached instead of
/// failing silently a frame at a time.</summary>
public sealed class StyleExpressionException(string message) : Exception(message);

/// <summary>What an expression is evaluated against: the current zoom, and the
/// feature under consideration. Layout and paint properties that only vary by
/// zoom are evaluated with no feature at all.</summary>
public readonly record struct StyleFeatureContext(
    double Zoom,
    IReadOnlyDictionary<string, object?>? Attributes = null,
    string GeometryType = "")
{
    public object? Attribute(string key) =>
        Attributes is not null && Attributes.TryGetValue(key, out var v) ? v : null;

    public bool HasAttribute(string key) =>
        Attributes is not null && Attributes.ContainsKey(key);

    /// <summary>The name a style filter compares against. MapLibre reports the
    /// multi-part forms for features carrying more than one part, and styles
    /// routinely test for both, so the distinction is preserved.</summary>
    public static string GeometryTypeName(VectorTileGeometryType type, int partCount) => type switch
    {
        VectorTileGeometryType.Point => partCount > 1 ? "MultiPoint" : "Point",
        VectorTileGeometryType.LineString => partCount > 1 ? "MultiLineString" : "LineString",
        VectorTileGeometryType.Polygon => partCount > 1 ? "MultiPolygon" : "Polygon",
        _ => "Unknown",
    };
}

/// <summary>A compiled MapLibre style expression.
///
/// Style JSON is compiled to this tree once when the style loads, rather than
/// being walked as JSON per feature: a zoom 14 tile carries several thousand
/// features and every one is filtered and painted, so the interpretation cost
/// is paid once per style instead of once per feature per frame.</summary>
public abstract class StyleExpression
{
    public abstract object? Evaluate(in StyleFeatureContext ctx);

    // -- Typed accessors ----------------------------------------------------

    public double AsNumber(in StyleFeatureContext ctx, double fallback = 0.0) =>
        ToNumber(Evaluate(ctx)) ?? fallback;

    public bool AsBoolean(in StyleFeatureContext ctx, bool fallback = false) =>
        ToBoolean(Evaluate(ctx)) ?? fallback;

    public string? AsString(in StyleFeatureContext ctx) => ToText(Evaluate(ctx));

    public StyleColor AsColor(in StyleFeatureContext ctx, StyleColor fallback) =>
        Evaluate(ctx) switch
        {
            StyleColor c => c,
            string s => StyleColor.Parse(s, fallback),
            _ => fallback,
        };

    // -- Value coercion -----------------------------------------------------

    internal static double? ToNumber(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    internal static bool? ToBoolean(object? v) => v switch
    {
        null => null,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        long l => l != 0,
        string s => s.Length > 0,
        _ => true,
    };

    internal static string? ToText(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        StyleColor c => $"rgba({c.R8},{c.G8},{c.B8},{c.A.ToString("R", CultureInfo.InvariantCulture)})",
        _ => v.ToString(),
    };

    /// <summary>Equality across the loosely typed values a tile carries, where
    /// the same attribute may arrive as int64 from one writer and double from
    /// another.</summary>
    internal static bool ValuesEqual(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is bool ba && b is bool bb) return ba == bb;
        var na = ToNumber(a);
        var nb = ToNumber(b);
        if (na.HasValue && nb.HasValue) return na.Value == nb.Value;
        return string.Equals(ToText(a), ToText(b), StringComparison.Ordinal);
    }

    // -- Parsing ------------------------------------------------------------

    public static StyleExpression Parse(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Number: return new Literal(json.GetDouble());
            case JsonValueKind.True: return new Literal(true);
            case JsonValueKind.False: return new Literal(false);
            case JsonValueKind.Null: return new Literal(null);
            case JsonValueKind.String:
            {
                var s = json.GetString();
                // A colour is recognised here so it is parsed once rather than
                // re-parsed from its string on every feature.
                return StyleColor.TryParse(s, out var c) && s is not null && (s[0] == '#' || s.Contains('('))
                    ? new Literal(c)
                    : new Literal(s);
            }
            case JsonValueKind.Object:
                throw new StyleExpressionException(
                    "legacy stops functions are not supported; expected an expression array");
            case JsonValueKind.Array: break;
            default:
                throw new StyleExpressionException($"unsupported JSON value {json.ValueKind}");
        }

        if (json.GetArrayLength() == 0) throw new StyleExpressionException("empty expression");
        var head = json[0];
        if (head.ValueKind != JsonValueKind.String)
        {
            // A bare array of values, as line-dasharray uses.
            var items = new List<object?>();
            foreach (var e in json.EnumerateArray()) items.Add(new Parsed(Parse(e)).Value);
            return new Literal(items);
        }

        var op = head.GetString()!;

        // An operator call and a plain array of strings are spelled alike:
        // text-font is ["Noto Sans Regular"], not a call to an operator of
        // that name. Only a known operator heads a call; anything else whose
        // elements are all strings is data. A misspelled operator carrying
        // non-string arguments still fails loudly below.
        if (!KnownOperators.Contains(op))
        {
            var strings = new List<object?>();
            foreach (var e in json.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String)
                    throw new StyleExpressionException($"unsupported expression operator \"{op}\"");
                strings.Add(e.GetString());
            }
            return new Literal(strings);
        }

        var args = new List<JsonElement>();
        for (int i = 1; i < json.GetArrayLength(); i++) args.Add(json[i]);

        return op switch
        {
            "literal" => new Literal(LiteralValue(args[0])),
            "get" => new Get(Parse(args[0])),
            "has" => new Has(Parse(args[0])),
            "zoom" => new ZoomRef(),
            "geometry-type" => new GeometryTypeRef(),
            "case" => Case.Build(args),
            "match" => Match.Build(args),
            "step" => Step.Build(args),
            "interpolate" => Interpolate.Build(args),
            "coalesce" => new Coalesce(ParseAll(args)),
            "concat" => new Concat(ParseAll(args)),
            "to-string" => new Convert(Parse(args[0]), Convert.Kind.Text),
            "to-number" => new Convert(Parse(args[0]), Convert.Kind.Number),
            "to-boolean" => new Convert(Parse(args[0]), Convert.Kind.Boolean),
            "==" or "!=" or "<" or "<=" or ">" or ">=" =>
                new Comparison(op, Parse(args[0]), Parse(args[1])),
            "all" => new Logical(Logical.Kind.All, ParseAll(args)),
            "any" => new Logical(Logical.Kind.Any, ParseAll(args)),
            "!" => new Logical(Logical.Kind.Not, ParseAll(args)),
            "in" => In.Build(args),
            _ => throw new StyleExpressionException($"unsupported expression operator \"{op}\""),
        };
    }

    private static readonly HashSet<string> KnownOperators = new(StringComparer.Ordinal)
    {
        "literal", "get", "has", "zoom", "geometry-type", "case", "match", "step",
        "interpolate", "coalesce", "concat", "to-string", "to-number", "to-boolean",
        "==", "!=", "<", "<=", ">", ">=", "all", "any", "!", "in",
    };

    public static StyleExpression Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement.Clone());
    }

    private static StyleExpression[] ParseAll(List<JsonElement> args)
    {
        var result = new StyleExpression[args.Count];
        for (int i = 0; i < args.Count; i++) result[i] = Parse(args[i]);
        return result;
    }

    /// <summary>The raw value of a JSON node, used for the operands of match
    /// and literal, which are data rather than sub-expressions.</summary>
    private static object? LiteralValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => e.EnumerateArray().Select(LiteralValue).ToList(),
        _ => null,
    };

    private readonly record struct Parsed(StyleExpression Expression)
    {
        public object? Value => Expression.Evaluate(new StyleFeatureContext(0));
    }

    // -- Nodes --------------------------------------------------------------

    private sealed class Literal(object? value) : StyleExpression
    {
        public override object? Evaluate(in StyleFeatureContext ctx) => value;
    }

    private sealed class Get(StyleExpression key) : StyleExpression
    {
        public override object? Evaluate(in StyleFeatureContext ctx) =>
            ToText(key.Evaluate(ctx)) is { } k ? ctx.Attribute(k) : null;
    }

    private sealed class Has(StyleExpression key) : StyleExpression
    {
        public override object Evaluate(in StyleFeatureContext ctx) =>
            ToText(key.Evaluate(ctx)) is { } k && ctx.HasAttribute(k);
    }

    private sealed class ZoomRef : StyleExpression
    {
        public override object Evaluate(in StyleFeatureContext ctx) => ctx.Zoom;
    }

    private sealed class GeometryTypeRef : StyleExpression
    {
        public override object Evaluate(in StyleFeatureContext ctx) => ctx.GeometryType;
    }

    private sealed class Coalesce(StyleExpression[] args) : StyleExpression
    {
        public override object? Evaluate(in StyleFeatureContext ctx)
        {
            foreach (var a in args)
            {
                var v = a.Evaluate(ctx);
                if (v is not null && v is not string { Length: 0 }) return v;
            }
            return null;
        }
    }

    private sealed class Concat(StyleExpression[] args) : StyleExpression
    {
        public override object Evaluate(in StyleFeatureContext ctx)
        {
            var sb = new StringBuilder();
            foreach (var a in args) sb.Append(ToText(a.Evaluate(ctx)));
            return sb.ToString();
        }
    }

    private sealed class Convert(StyleExpression inner, Convert.Kind kind) : StyleExpression
    {
        public enum Kind { Text, Number, Boolean }

        public override object? Evaluate(in StyleFeatureContext ctx)
        {
            var v = inner.Evaluate(ctx);
            return kind switch
            {
                Kind.Text => ToText(v) ?? string.Empty,
                Kind.Number => ToNumber(v) ?? 0.0,
                _ => ToBoolean(v) ?? false,
            };
        }
    }

    private sealed class Comparison(string op, StyleExpression left, StyleExpression right)
        : StyleExpression
    {
        public override object Evaluate(in StyleFeatureContext ctx)
        {
            var a = left.Evaluate(ctx);
            var b = right.Evaluate(ctx);
            if (op == "==") return ValuesEqual(a, b);
            if (op == "!=") return !ValuesEqual(a, b);

            // An ordered comparison against a missing attribute is false
            // rather than an error, matching how styles rely on it.
            var na = ToNumber(a);
            var nb = ToNumber(b);
            int cmp;
            if (na.HasValue && nb.HasValue) cmp = na.Value.CompareTo(nb.Value);
            else if (a is string sa && b is string sb) cmp = string.CompareOrdinal(sa, sb);
            else return false;

            return op switch
            {
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                _ => cmp >= 0,
            };
        }
    }

    private sealed class Logical(Logical.Kind kind, StyleExpression[] args) : StyleExpression
    {
        public enum Kind { All, Any, Not }

        public override object Evaluate(in StyleFeatureContext ctx)
        {
            switch (kind)
            {
                case Kind.All:
                    foreach (var a in args) if (ToBoolean(a.Evaluate(ctx)) != true) return false;
                    return true;
                case Kind.Any:
                    foreach (var a in args) if (ToBoolean(a.Evaluate(ctx)) == true) return true;
                    return false;
                default:
                    return args.Length > 0 && ToBoolean(args[0].Evaluate(ctx)) != true;
            }
        }
    }

    private sealed class In(StyleExpression needle, StyleExpression haystack) : StyleExpression
    {
        public static In Build(List<JsonElement> args) =>
            new(Parse(args[0]), Parse(args[1]));

        public override object Evaluate(in StyleFeatureContext ctx)
        {
            var v = needle.Evaluate(ctx);
            return haystack.Evaluate(ctx) switch
            {
                IReadOnlyList<object?> list => list.Any(item => ValuesEqual(item, v)),
                string s => ToText(v) is { } t && t.Length > 0 && s.Contains(t, StringComparison.Ordinal),
                _ => false,
            };
        }
    }

    private sealed class Case(
        (StyleExpression Condition, StyleExpression Output)[] branches, StyleExpression fallback)
        : StyleExpression
    {
        public static Case Build(List<JsonElement> args)
        {
            if (args.Count < 1) throw new StyleExpressionException("case needs a fallback");
            var branches = new List<(StyleExpression, StyleExpression)>();
            int i = 0;
            for (; i + 1 < args.Count; i += 2)
                branches.Add((Parse(args[i]), Parse(args[i + 1])));
            return new Case(branches.ToArray(), Parse(args[i]));
        }

        public override object? Evaluate(in StyleFeatureContext ctx)
        {
            foreach (var (condition, output) in branches)
                if (ToBoolean(condition.Evaluate(ctx)) == true) return output.Evaluate(ctx);
            return fallback.Evaluate(ctx);
        }
    }

    private sealed class Match(
        StyleExpression input, (object?[] Labels, StyleExpression Output)[] cases, StyleExpression fallback)
        : StyleExpression
    {
        public static Match Build(List<JsonElement> args)
        {
            if (args.Count < 2) throw new StyleExpressionException("match needs an input and a fallback");
            var input = Parse(args[0]);
            var cases = new List<(object?[], StyleExpression)>();
            int i = 1;
            for (; i + 1 < args.Count; i += 2)
            {
                // A label is a single value or an array of alternatives; both
                // are data, never sub-expressions.
                var raw = LiteralValue(args[i]);
                var labels = raw is List<object?> many ? many.ToArray() : [raw];
                cases.Add((labels, Parse(args[i + 1])));
            }
            return new Match(input, cases.ToArray(), Parse(args[i]));
        }

        public override object? Evaluate(in StyleFeatureContext ctx)
        {
            var v = input.Evaluate(ctx);
            foreach (var (labels, output) in cases)
                foreach (var label in labels)
                    if (ValuesEqual(label, v)) return output.Evaluate(ctx);
            return fallback.Evaluate(ctx);
        }
    }

    private sealed class Step(
        StyleExpression input, StyleExpression baseOutput, (double Stop, StyleExpression Output)[] stops)
        : StyleExpression
    {
        public static Step Build(List<JsonElement> args)
        {
            if (args.Count < 2) throw new StyleExpressionException("step needs an input and a base output");
            var input = Parse(args[0]);
            var baseOutput = Parse(args[1]);
            var stops = new List<(double, StyleExpression)>();
            for (int i = 2; i + 1 < args.Count; i += 2)
                stops.Add((args[i].GetDouble(), Parse(args[i + 1])));
            return new Step(input, baseOutput, stops.ToArray());
        }

        public override object? Evaluate(in StyleFeatureContext ctx)
        {
            double v = ToNumber(input.Evaluate(ctx)) ?? 0.0;
            var chosen = baseOutput;
            foreach (var (stop, output) in stops)
            {
                if (v < stop) break;
                chosen = output;
            }
            return chosen.Evaluate(ctx);
        }
    }

    private sealed class Interpolate(
        double exponentialBase, StyleExpression input, (double Stop, StyleExpression Output)[] stops)
        : StyleExpression
    {
        public static Interpolate Build(List<JsonElement> args)
        {
            if (args.Count < 4) throw new StyleExpressionException("interpolate needs at least one stop");

            double expBase = 1.0;
            var kind = args[0];
            if (kind.ValueKind == JsonValueKind.Array && kind.GetArrayLength() > 0)
            {
                var name = kind[0].GetString();
                if (name == "exponential" && kind.GetArrayLength() > 1) expBase = kind[1].GetDouble();
                else if (name is not ("linear" or "exponential"))
                    throw new StyleExpressionException($"unsupported interpolation \"{name}\"");
            }

            var input = Parse(args[1]);
            var stops = new List<(double, StyleExpression)>();
            for (int i = 2; i + 1 < args.Count; i += 2)
                stops.Add((args[i].GetDouble(), Parse(args[i + 1])));
            if (stops.Count == 0) throw new StyleExpressionException("interpolate needs at least one stop");
            return new Interpolate(expBase, input, stops.ToArray());
        }

        public override object? Evaluate(in StyleFeatureContext ctx)
        {
            double v = ToNumber(input.Evaluate(ctx)) ?? 0.0;

            if (v <= stops[0].Stop) return stops[0].Output.Evaluate(ctx);
            var last = stops[^1];
            if (v >= last.Stop) return last.Output.Evaluate(ctx);

            int i = 0;
            while (i + 1 < stops.Length && v >= stops[i + 1].Stop) i++;
            var (lowStop, lowExpr) = stops[i];
            var (highStop, highExpr) = stops[i + 1];

            double t = Fraction(v, lowStop, highStop);
            var lo = lowExpr.Evaluate(ctx);
            var hi = highExpr.Evaluate(ctx);

            if (lo is StyleColor ca && hi is StyleColor cb) return StyleColor.Lerp(ca, cb, t);
            var na = ToNumber(lo);
            var nb = ToNumber(hi);
            if (na.HasValue && nb.HasValue) return na.Value + (nb.Value - na.Value) * t;

            // Values that cannot be blended step at the lower stop.
            return lo;
        }

        /// <summary>Position between two stops. A base of 1 is linear; any
        /// other base curves the ramp, which is how a style keeps road widths
        /// growing sensibly across many zoom levels.</summary>
        private double Fraction(double v, double low, double high)
        {
            double span = high - low;
            if (span <= 0) return 0;
            double progress = v - low;
            if (Math.Abs(exponentialBase - 1.0) < 1e-9) return progress / span;
            return (Math.Pow(exponentialBase, progress) - 1.0)
                 / (Math.Pow(exponentialBase, span) - 1.0);
        }
    }
}
