// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>
/// Substitutes <c>{placeholder}</c> tokens in script message text.
/// </summary>
/// <remarks>
/// A lookup table, not an evaluator: there is no arithmetic, no conditionals
/// and no nesting, so there is nothing to sandbox and nothing that can loop.
/// An unrecognised token is left exactly as written rather than blanked — the
/// editor already warned about it, and sending "{from.shrt}" makes the mistake
/// obvious in a way that silently sending nothing would not.
/// </remarks>
public static class ScriptTemplate
{
    /// <summary>A token, plus any filters piped onto it: <c>{snr|round:0}</c>,
    /// <c>{args|trim|truncate:40}</c>. A filter argument runs to the next pipe
    /// or the closing brace, so <c>{x|default:not sure}</c> keeps its spaces.</summary>
    internal static readonly Regex Token = new(
        @"\{([a-zA-Z][a-zA-Z0-9_.]*)((?:\|[a-zA-Z_]+(?::[^|{}]*)?)*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The value this app reports for its own battery when running on
    /// mains, mirroring firmware's convention.</summary>
    private const int MainsPoweredSentinel = 101;

    /// <param name="escape">Applied to every substituted value before it is
    /// spliced in — percent-encoding for a URL, JSON escaping for a request
    /// body. Null leaves values as they are, which is right for message text
    /// and wrong for anything with syntax of its own.</param>
    public static string Expand(
        string template,
        ScriptEvent evt,
        IReadOnlyList<string>? args = null,
        IReadOnlyList<string>? captures = null,
        IReadOnlyDictionary<string, string>? httpResults = null,
        Func<string, string>? escape = null,
        string? item = null)
    {
        if (template.Length == 0 || !template.Contains('{')) return template;

        return Token.Replace(template, match =>
        {
            var value = Resolve(match.Groups[1].Value, evt, args, captures, httpResults, item);
            if (value is null) return match.Value;

            // Filters run on the resolved value and before the escape, so a URL
            // or a JSON body still escapes whatever the chain produced.
            foreach (var (name, argument) in Filters(match.Groups[2].Value))
            {
                var filtered = ScriptFilters.Apply(name, argument, value);
                // An unknown filter leaves the whole token as written, the same
                // as an unknown placeholder: the editor already warned, and a
                // visible mistake beats a silent one.
                if (filtered is null) return match.Value;
                value = filtered;
            }

            return escape is null ? value : escape(value);
        });
    }

    /// <summary>
    /// Splits the pipe section of a token into filters, in order.
    /// </summary>
    /// <param name="chain">The captured tail, e.g. <c>|trim|truncate:40</c>.</param>
    internal static IEnumerable<(string Name, string? Argument)> Filters(string chain)
    {
        if (chain.Length == 0) yield break;

        foreach (var written in chain.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = written.IndexOf(':');
            yield return colon < 0
                ? (written, null)
                : (written[..colon], written[(colon + 1)..]);
        }
    }

    private static string? Resolve(
        string token, ScriptEvent evt, IReadOnlyList<string>? args, IReadOnlyList<string>? captures,
        IReadOnlyDictionary<string, string>? httpResults,
        string? item)
    {
        // {item.<path>} reads the feed record currently being mirrored. Empty
        // rather than literal when the path is absent, for the same reason
        // {http.*} is: a field this record happens to lack should leave a gap
        // in the sentence, not print its own name into a waypoint.
        if (token.StartsWith("item.", StringComparison.Ordinal))
        {
            if (item is null) return string.Empty;
            return JsonValuePath.Read(item, token["item.".Length..], out _) ?? string.Empty;
        }

        // {http.*} comes from an http: action earlier in the same sequence. An
        // unfilled one resolves to empty rather than staying literal: reaching
        // it means the fetch was skipped, and broadcasting "{http.temp}" would
        // be worse than broadcasting nothing.
        if (token.StartsWith("http.", StringComparison.Ordinal))
        {
            var name = token["http.".Length..];
            return httpResults is not null && httpResults.TryGetValue(name, out var result)
                ? result
                : string.Empty;
        }

        switch (token)
        {
            case "msg.text": return evt.Text;
            case "args": return args is null ? string.Empty : string.Join(' ', args);

            case "from.id": return evt.FromId;
            case "from.short": return evt.FromShort;
            case "from.long": return evt.FromLong;

            // Empty when the sender has never sent a position, for the same
            // reason {my.lat} is: these go into URLs, where a question mark
            // would build a nonsense request, and empty is what a require: or a
            // when: can test for.
            case "from.lat": return Coordinate(evt.FromLatitude);
            case "from.lon": return Coordinate(evt.FromLongitude);

            case "channel": return evt.Channel;
            case "snr": return Number(evt.SnrDb, "0.#");
            case "rssi": return Number(evt.RssiDbm, "0");
            case "hops": return evt.Hops.ToString(CultureInfo.InvariantCulture);

            case "time": return evt.At.ToString("HH:mm", CultureInfo.InvariantCulture);
            case "date": return evt.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            case "my.id": return evt.Self.Id;
            case "my.short": return evt.Self.ShortName;
            case "my.long": return evt.Self.LongName;

            // Empty rather than "?" when no home is set: these go into URLs and
            // waypoints, where a question mark would build a nonsense request.
            // Empty is what a require: can test for.
            case "my.lat": return Coordinate(evt.Self.Latitude);
            case "my.lon": return Coordinate(evt.Self.Longitude);

            case "node.battery":
                return evt.Self.BatteryPct switch
                {
                    null => "?",
                    // Not a percentage at all, so printing "101%" would be a lie.
                    MainsPoweredSentinel => "mains",
                    var pct => pct.Value.ToString(CultureInfo.InvariantCulture),
                };
        }

        if (Indexed(token, "arg") is { } argIndex)
            return args is not null && argIndex >= 1 && argIndex <= args.Count ? args[argIndex - 1] : string.Empty;

        if (Indexed(token, "cap") is { } capIndex)
            return captures is not null && capIndex >= 1 && capIndex <= captures.Count ? captures[capIndex - 1] : string.Empty;

        return null;
    }

    /// <summary>"arg2" -> 2 for the numbered placeholder families, null for
    /// anything else.</summary>
    private static int? Indexed(string token, string prefix)
    {
        if (!token.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var digits = token[prefix.Length..];
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) &&
               int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : null;
    }

    /// <summary>Formats a measurement, or "?" when the packet carried none —
    /// an empty gap in a sentence reads like a bug, a question mark reads like
    /// missing data.</summary>
    private static string Number(double? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "?";

    /// <summary>Formats a coordinate to roughly a metre, invariant so a decimal
    /// comma from the host locale can never reach a URL or a waypoint.</summary>
    private static string Coordinate(double? value) =>
        value?.ToString("0.#####", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Splits a command message into its arguments: everything after the
    /// command word, on whitespace. "!wx london today" gives ["london","today"],
    /// which become {arg1} and {arg2}, and {args} as the whole tail.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string messageText)
    {
        var trimmed = messageText.Trim();
        int space = trimmed.IndexOfAny([' ', '\t', '\n']);
        if (space < 0) return Array.Empty<string>();
        return trimmed[(space + 1)..]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Truncates an expanded body to what a Meshtastic text payload
    /// can actually carry, on a whole UTF-8 character. The radio would cut it
    /// mid-sequence otherwise, which can leave a broken glyph on the far
    /// end.</summary>
    public static string ClampToPayload(string text, int maxBytes = 200)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var bytes = Encoding.UTF8.GetBytes(text);
        int cut = maxBytes;
        // Walk back off a continuation byte (10xxxxxx) to a lead byte.
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
        return Encoding.UTF8.GetString(bytes, 0, cut);
    }
}
