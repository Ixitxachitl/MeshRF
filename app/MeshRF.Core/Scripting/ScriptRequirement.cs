// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

public enum ScriptComparison
{
    Equals,
    NotEquals,
    Above,
    Below,
    AtLeast,
    AtMost,
    Between,
    Contains,
    Matches,
    IsEmpty,
    NotEmpty,
    /// <summary>A "lat,lon" value is no further than a given distance from this
    /// node's home location.</summary>
    Within,
}

/// <summary>
/// A test that stops a script's sequence when it does not hold.
/// </summary>
/// <remarks>
/// <para>Conditions are settled before any action runs, which makes them
/// useless for reacting to what an <c>http:</c> action returned. A requirement
/// is evaluated in sequence, so it can look at <c>{http.*}</c> — it is the
/// difference between a script that fetches and always acts, and one that acts
/// only when the answer warranted it.</para>
/// <para>Deliberately a structured comparison rather than an expression:
/// one value, one comparator, no operators to parse and nothing to sandbox.
/// The vocabulary is closed for the same reason the rest of the language
/// is.</para>
/// </remarks>
public sealed class ScriptRequirement
{
    /// <summary>The value under test, as a template — usually a placeholder.</summary>
    public string Value { get; init; } = string.Empty;

    public ScriptComparison Comparison { get; init; }

    /// <summary>The thing compared against, as a template. Unused by the
    /// emptiness tests.</summary>
    public string Operand { get; init; } = string.Empty;

    /// <summary>Upper bound for <see cref="ScriptComparison.Between"/>.</summary>
    public string Operand2 { get; init; } = string.Empty;

    /// <summary>Comparisons are case-insensitive by default: a script matching
    /// a weather description or an alert name should not turn on capitalisation
    /// the API happens to have used today.</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>Compiled at parse time for <see cref="ScriptComparison.Matches"/>.</summary>
    public Regex? Pattern { get; init; }

    /// <summary>
    /// Evaluates the test with placeholders filled in.
    /// </summary>
    /// <param name="expansion">Supplies {http.*} and everything else.</param>
    /// <param name="detail">What was actually compared, for the log — a script
    /// that quietly stops is otherwise very hard to reason about.</param>
    public bool Holds(ScriptExpansion expansion, out string detail)
    {
        var value = expansion.Expand(Value);
        var comparison = StringComparison.Ordinal;
        if (IgnoreCase) comparison = StringComparison.OrdinalIgnoreCase;

        switch (Comparison)
        {
            case ScriptComparison.IsEmpty:
                detail = $"\"{value}\" is empty";
                return value.Trim().Length == 0;

            case ScriptComparison.NotEmpty:
                detail = $"\"{value}\" is not empty";
                return value.Trim().Length > 0;

            case ScriptComparison.Matches:
                detail = $"\"{value}\" matches {Pattern?.ToString() ?? Operand}";
                try { return Pattern?.IsMatch(value) == true; }
                // A pattern that goes quadratic on an API response should stop
                // the script, not stall it.
                catch (RegexMatchTimeoutException) { return false; }

            case ScriptComparison.Within:
                return WithinRange(value, expansion.Event.Self, out detail);
        }

        var operand = expansion.Expand(Operand);

        switch (Comparison)
        {
            case ScriptComparison.Equals:
                detail = $"\"{value}\" equals \"{operand}\"";
                return string.Equals(value.Trim(), operand.Trim(), comparison);

            case ScriptComparison.NotEquals:
                detail = $"\"{value}\" is not \"{operand}\"";
                return !string.Equals(value.Trim(), operand.Trim(), comparison);

            case ScriptComparison.Contains:
                detail = $"\"{value}\" contains \"{operand}\"";
                return value.Contains(operand, comparison);
        }

        // The remaining comparators are numeric. A value that is not a number
        // fails rather than throwing: an API that answered with an error string
        // where a reading was expected should stop the script, quietly and
        // explicably.
        if (!TryNumber(value, out var left))
        {
            detail = $"\"{value}\" is not a number";
            return false;
        }

        if (Comparison == ScriptComparison.Between)
        {
            var lowText = expansion.Expand(Operand);
            var highText = expansion.Expand(Operand2);
            if (!TryNumber(lowText, out var low) || !TryNumber(highText, out var high))
            {
                detail = $"between {lowText} and {highText} is not a numeric range";
                return false;
            }
            if (low > high) (low, high) = (high, low);
            detail = $"{left} is between {low} and {high}";
            return left >= low && left <= high;
        }

        if (!TryNumber(operand, out var right))
        {
            detail = $"\"{operand}\" is not a number";
            return false;
        }

        switch (Comparison)
        {
            case ScriptComparison.Above: detail = $"{left} > {right}"; return left > right;
            case ScriptComparison.Below: detail = $"{left} < {right}"; return left < right;
            case ScriptComparison.AtLeast: detail = $"{left} >= {right}"; return left >= right;
            case ScriptComparison.AtMost: detail = $"{left} <= {right}"; return left <= right;
            default: detail = "unknown comparison"; return false;
        }
    }

    /// <summary>Metres, for <see cref="ScriptComparison.Within"/>. Parsed at
    /// load so a malformed distance is a red line in the editor.</summary>
    public double RangeMetres { get; init; }

    /// <summary>
    /// Whether a "lat,lon" value is inside <see cref="RangeMetres"/> of this
    /// node's home location.
    /// </summary>
    /// <remarks>
    /// Needed because not every API can narrow by distance itself. Watch Duty's
    /// returns every active incident and leaves the filtering to the caller —
    /// its Home Assistant integration runs the same haversine against each
    /// zone. Without this a script could only mark whichever fire happened to
    /// come back first, wherever it was.
    /// </remarks>
    private bool WithinRange(string value, ScriptSelf self, out string detail)
    {
        if (!self.HasLocation)
        {
            detail = "this node has no home location to measure from";
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryNumber(parts[0], out var lat) || !TryNumber(parts[1], out var lon))
        {
            detail = $"\"{value}\" is not a \"lat,lon\" position";
            return false;
        }

        double metres = HaversineMetres(self.Latitude!.Value, self.Longitude!.Value, lat, lon);
        detail = $"{metres / 1000.0:0.#} km away, limit {RangeMetres / 1000.0:0.#} km";
        return metres <= RangeMetres;
    }

    /// <summary>Great-circle distance in metres. The same spherical
    /// approximation the node-distance filter uses; at mesh ranges the error
    /// against a true ellipsoid is far smaller than the positions' own.</summary>
    internal static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
                   * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>One-line description for the log, before evaluation.</summary>
    public string Describe() => Comparison switch
    {
        ScriptComparison.IsEmpty => $"{Value} is empty",
        ScriptComparison.NotEmpty => $"{Value} is not empty",
        ScriptComparison.Between => $"{Value} between {Operand} and {Operand2}",
        ScriptComparison.Within => $"{Value} within {RangeMetres / 1000.0:0.#} km of home",
        _ => $"{Value} {Comparison.ToString().ToLowerInvariant()} {Operand}",
    };
}
