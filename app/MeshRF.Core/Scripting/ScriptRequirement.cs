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

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>One-line description for the log, before evaluation.</summary>
    public string Describe() => Comparison switch
    {
        ScriptComparison.IsEmpty => $"{Value} is empty",
        ScriptComparison.NotEmpty => $"{Value} is not empty",
        ScriptComparison.Between => $"{Value} between {Operand} and {Operand2}",
        _ => $"{Value} {Comparison.ToString().ToLowerInvariant()} {Operand}",
    };
}
