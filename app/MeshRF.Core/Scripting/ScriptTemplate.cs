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
    private static readonly Regex s_token =
        new(@"\{([a-zA-Z][a-zA-Z0-9_.]*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        Func<string, string>? escape = null)
    {
        if (template.Length == 0 || !template.Contains('{')) return template;

        return s_token.Replace(template, match =>
        {
            var value = Resolve(match.Groups[1].Value, evt, args, captures, httpResults);
            if (value is null) return match.Value;
            return escape is null ? value : escape(value);
        });
    }

    private static string? Resolve(
        string token, ScriptEvent evt, IReadOnlyList<string>? args, IReadOnlyList<string>? captures,
        IReadOnlyDictionary<string, string>? httpResults)
    {
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

            case "channel": return evt.Channel;
            case "snr": return Number(evt.SnrDb, "0.#");
            case "rssi": return Number(evt.RssiDbm, "0");
            case "hops": return evt.Hops.ToString(CultureInfo.InvariantCulture);

            case "time": return evt.At.ToString("HH:mm", CultureInfo.InvariantCulture);
            case "date": return evt.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            case "my.id": return evt.Self.Id;
            case "my.short": return evt.Self.ShortName;
            case "my.long": return evt.Self.LongName;

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
