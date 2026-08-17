// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace MeshRF.Scripting;

/// <summary>
/// The closed set of transforms a placeholder may be piped through:
/// <c>{snr|round:0}</c>, <c>{from.long|default:a stranger}</c>,
/// <c>{args|trim|truncate:40}</c>.
/// </summary>
/// <remarks>
/// <para>A fixed table for the same reason <see cref="ScriptPlaceholders"/> is
/// one: every filter is a named transform the editor can check for and the help
/// window can list, rather than an expression language needing a sandbox and a
/// timeout. Each is a pure string-to-string function, so a chain cannot loop or
/// reach anything the token did not already carry.</para>
/// <para>Filters run on the resolved value and before any escaping, so a
/// percent-encoded URL or a JSON body still escapes whatever the chain
/// produced.</para>
/// </remarks>
public static class ScriptFilters
{
    /// <summary>Filter name to the one-line description shown in the help
    /// window. Ordered for display.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> All =
    [
        ("upper",       "Upper-case."),
        ("lower",       "Lower-case."),
        ("trim",        "Drop leading and trailing whitespace."),
        ("round:1",     "Round a number to this many decimal places. 0 gives a whole number. A value that is not a number is left alone."),
        ("keycap",      "A whole number 0-10 as a keycap emoji, 0️⃣ through 🔟. Anything else is left alone."),
        ("truncate:40", "Cut to this many characters, ending with … when something was cut."),
        ("default:—",   "Use this text when the value is empty, which is what an unset location or a field an API left out expands to."),
        ("compass",     "A bearing in degrees as a 16-point compass name, N through NNW."),
        ("arrow",       "A bearing in degrees as an arrow, pointing the way its compass name reads: 0° is ↑, 45° is ↗."),
        ("weather",     "A condition description — \"light rain\", \"broken clouds\", \"thunderstorm\" — as one emoji. Reads the words, so it works with any API that describes conditions in English."),
        ("clock",       "A unix timestamp — or a 24-hour time like {time} — as a local wall-clock time, 6:12 AM. Seconds since the epoch is how most weather APIs report sunrise and sunset."),
        ("moon",        "The moon phase as an emoji, 🌑 through 🌘. Takes a date, a timestamp, a 0-1 phase from an API, or a phase name — {date|moon} needs no API at all."),
        ("fahrenheit",  "A temperature in °C as °F, for answering a reader who thinks in them. Fetch in metric and convert, rather than fetching twice."),
        ("mph",         "A speed in metres per second as miles per hour, the companion to fahrenheit."),
        ("inches",      "A depth in millimetres as inches, for rain and snow in the same report."),
        ("prefix:, ",   "Put this text in front of the value, but only when there is a value. For an optional field that needs a separator — a state that not every country has."),
    ];

    private static readonly HashSet<string> s_known =
        new(All.Select(f => Name(f.Name)), StringComparer.Ordinal);

    /// <summary>"round:1" -> "round", for the table above and for a written
    /// filter that carries an argument.</summary>
    private static string Name(string filter)
    {
        int colon = filter.IndexOf(':');
        return colon < 0 ? filter : filter[..colon];
    }

    public static bool IsKnown(string name) => s_known.Contains(name);

    /// <summary>Every filter name in the table, for the editor's did-you-mean.</summary>
    public static IEnumerable<string> Names => s_known;

    /// <summary>
    /// Runs one filter. An unknown name returns null, which the caller reports
    /// by leaving the whole token literal — the same treatment an unknown
    /// placeholder gets, and for the same reason: a visible mistake beats a
    /// silent one.
    /// </summary>
    public static string? Apply(string name, string? argument, string value) => name switch
    {
        "upper" => value.ToUpperInvariant(),
        "lower" => value.ToLowerInvariant(),
        "trim" => value.Trim(),
        "round" => Round(value, argument),
        "keycap" => Keycap(value),
        "truncate" => Truncate(value, argument),
        "compass" => Bearing(value, Points),
        "arrow" => Bearing(value, Arrows),
        "weather" => Weather(value),
        "clock" => Clock(value),
        "moon" => Moon(value),
        "fahrenheit" => Convert(value, celsius => celsius * 9 / 5 + 32),
        "mph" => Convert(value, metresPerSecond => metresPerSecond * 2.236936),
        "inches" => Convert(value, millimetres => millimetres / 25.4),
        // The mirror of default:, and the reason both exist: one fills a gap,
        // the other keeps a separator from being left hanging over one. A blank
        // value yields nothing at all rather than its own whitespace, since the
        // point is to leave no trace when there is nothing to separate.
        "prefix" => value.Trim().Length == 0 ? string.Empty : (argument ?? string.Empty) + value,
        // Whitespace-only counts as empty: a name nobody filled in usually
        // arrives as a space rather than as nothing.
        "default" => value.Trim().Length == 0 ? argument ?? string.Empty : value,
        _ => null,
    };

    /// <summary>
    /// Rounds to a number of decimal places, default 0.
    /// </summary>
    /// <remarks>
    /// A value that is not a number passes through untouched rather than
    /// becoming an error or an empty gap: {snr} is "?" when the packet carried
    /// no reading, and "?" is what that sentence should still say.
    /// </remarks>
    private static string Round(string value, string? argument)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return value;

        int places = 0;
        if (argument is { Length: > 0 } &&
            int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var written))
        {
            places = Math.Clamp(written, 0, 15);
        }

        return Math.Round(number, places).ToString($"F{places}", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A whole number 0-10 as a keycap emoji: digit + VS16 + combining keycap,
    /// then the single-code-point ten.
    /// </summary>
    /// <remarks>
    /// Anything outside that range — including a value that is not a number at
    /// all — is left as written, since there is no glyph to use and printing a
    /// stray one would be worse than printing the number.
    /// </remarks>
    private static string Keycap(string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return value;

        return number switch
        {
            >= 0 and <= 9 => $"{(char)('0' + number)}️⃣",
            10 => "\U0001F51F",
            _ => value,
        };
    }

    private static readonly string[] Points =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
         "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    /// <summary>One arrow per compass point, pointing the way the name reads —
    /// N is ↑, NE is ↗. Paired with <see cref="Points"/> in a report, an arrow
    /// that disagreed with the letters beside it would read as a bug, whichever
    /// convention justified it.</summary>
    private static readonly string[] Arrows =
        ["↑", "↑", "↗", "↗", "→", "→", "↘", "↘",
         "↓", "↓", "↙", "↙", "←", "←", "↖", "↖"];

    /// <summary>
    /// Picks the entry of a 16-point table a bearing in degrees falls in.
    /// </summary>
    /// <remarks>
    /// Each point spans 22.5°, so the half-sector offset puts north at the
    /// middle of its sector rather than at its edge. Negative and over-360
    /// bearings wrap rather than failing, since an API may report either.
    /// </remarks>
    private static string Bearing(string value, string[] table)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
            return value;

        int sector = (int)Math.Floor(((degrees % 360 + 360) % 360 + 11.25) / 22.5) % 16;
        return table[sector];
    }

    /// <summary>
    /// One emoji for a condition described in words.
    /// </summary>
    /// <remarks>
    /// Matched on keywords rather than against a table of exact strings, so it
    /// survives the qualifiers APIs attach — "light rain", "heavy intensity
    /// rain" and "rain" all land on the same glyph, and a description no rule
    /// matches comes back unchanged rather than as a wrong picture. Order
    /// matters: "freezing rain" is snow-shaped, and "thunderstorm with rain" is
    /// a storm first.
    /// </remarks>
    private static string Weather(string value)
    {
        var text = value.ToLowerInvariant();

        if (text.Contains("thunder") || text.Contains("storm")) return "⛈️";
        if (text.Contains("freezing") || text.Contains("sleet") || text.Contains("snow")) return "🌨️";
        if (text.Contains("drizzle") || text.Contains("shower")) return "🌦️";
        if (text.Contains("rain")) return "🌧️";
        if (text.Contains("mist") || text.Contains("fog") || text.Contains("haze")
            || text.Contains("smoke") || text.Contains("dust") || text.Contains("sand")
            || text.Contains("ash") || text.Contains("squall") || text.Contains("tornado")) return "🌫️";
        if (text.Contains("few clouds") || text.Contains("partly")) return "⛅";
        if (text.Contains("cloud") || text.Contains("overcast")) return "☁️";
        if (text.Contains("clear") || text.Contains("sun")) return "☀️";
        return value;
    }

    /// <summary>
    /// A moment as a local wall-clock time: from a unix timestamp, which is how
    /// weather APIs report sunrise and sunset, or from a 24-hour time, which is
    /// what {time} expands to.
    /// </summary>
    /// <remarks>
    /// Local, because a sunrise is only useful in the reader's own hours, and
    /// the app already reports every other time that way. Anything that is
    /// neither is left alone.
    /// </remarks>
    private static string Clock(string value)
    {
        var text = value.Trim();

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime()
                    .ToString("h:mm tt", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Outside the representable range — a millisecond timestamp,
                // most likely. Better to show the number than drop the field.
                return value;
            }
        }

        return TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss", "H:mm"],
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("h:mm tt", CultureInfo.InvariantCulture)
            : value;
    }

    /// <summary>
    /// Applies a unit conversion, leaving anything that is not a number alone.
    /// </summary>
    /// <remarks>
    /// The result keeps full precision and is meant to be piped into
    /// <c>round:</c>, so a script decides how many decimals its sentence wants
    /// rather than having that decided here.
    /// </remarks>
    private static string Convert(string value, Func<double, double> conversion) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? conversion(number).ToString("0.#####", CultureInfo.InvariantCulture)
            : value;

    /// <summary>The eight phases, from new moon, as the northern hemisphere
    /// sees them — the same order and glyphs the Home Assistant moon sensor's
    /// phase names map to.</summary>
    private static readonly string[] Phases =
        ["🌑", "🌒", "🌓", "🌔", "🌕", "🌖", "🌗", "🌘"];

    /// <summary>Mean length of one new-moon-to-new-moon cycle, in days.</summary>
    private const double SynodicMonthDays = 29.530588853;

    /// <summary>A new moon to count cycles from: 2000-01-06 18:14 UTC.</summary>
    private static readonly DateTime ReferenceNewMoon =
        new(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

    /// <summary>
    /// The moon phase as an emoji, from a date, a unix timestamp, a 0-1 phase
    /// an API already worked out, or a phase name.
    /// </summary>
    /// <remarks>
    /// Computed rather than fetched, so a script can show the phase without an
    /// API that sells it: only the free "current weather" endpoints are needed
    /// for the rest of a report, and moon data usually sits behind a paid tier.
    /// </remarks>
    private static string Moon(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return value;

        if (PhaseByName(text) is { } named) return named;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            // 0-1 is a fraction of the cycle, which is how an API that reports
            // the phase directly does it. Anything larger is a timestamp — the
            // two cannot collide, since a unix time inside that range is 1970.
            if (number is >= 0 and <= 1) return Phases[PhaseIndex(number)];

            try
            {
                return Phases[PhaseIndex(Cycle(DateTimeOffset.FromUnixTimeSeconds((long)number).UtcDateTime))];
            }
            catch (ArgumentOutOfRangeException)
            {
                return value;
            }
        }

        // A written date, which is what {date} expands to. Read as local, since
        // that is the day the reader means.
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                                 DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var when)
            ? Phases[PhaseIndex(Cycle(when))]
            : value;
    }

    /// <summary>Maps the phase names Home Assistant's moon sensor reports, so a
    /// script ported from one keeps working against the same data.</summary>
    private static string? PhaseByName(string text) =>
        text.ToLowerInvariant().Replace(' ', '_') switch
        {
            "new_moon" => Phases[0],
            "waxing_crescent" => Phases[1],
            "first_quarter" => Phases[2],
            "waxing_gibbous" => Phases[3],
            "full_moon" => Phases[4],
            "waning_gibbous" => Phases[5],
            "last_quarter" or "third_quarter" => Phases[6],
            "waning_crescent" => Phases[7],
            _ => null,
        };

    /// <summary>
    /// How far through the new-moon-to-new-moon cycle a moment is, 0 to 1.
    /// </summary>
    /// <remarks>
    /// The mean synodic month, not a full lunar theory: the real cycle varies
    /// by up to about half a day either side, which is well inside the 3.7-day
    /// span each of the eight glyphs covers. Dates before the reference wrap
    /// the same way, since the fractional part is taken after flooring.
    /// </remarks>
    private static double Cycle(DateTime utc)
    {
        double cycles = (utc - ReferenceNewMoon).TotalDays / SynodicMonthDays;
        return cycles - Math.Floor(cycles);
    }

    /// <summary>Which of the eight glyphs a 0-1 phase falls on. Each owns an
    /// eighth of the cycle centred on its own phase, so the days either side of
    /// a full moon still read as full rather than as gibbous.</summary>
    private static int PhaseIndex(double cycle)
    {
        double wrapped = cycle - Math.Floor(cycle);
        return (int)Math.Floor(wrapped * 8 + 0.5) % 8;
    }

    /// <summary>
    /// Cuts to a character count, marking the cut with an ellipsis.
    /// </summary>
    /// <remarks>
    /// Characters rather than bytes: this is for keeping a quoted message or an
    /// API description to a readable length, while
    /// <see cref="ScriptTemplate.ClampToPayload"/> is what guarantees the frame
    /// fits. Cutting on a text element keeps an emoji or an accented character
    /// whole.
    /// </remarks>
    private static string Truncate(string value, string? argument)
    {
        if (argument is not { Length: > 0 } ||
            !int.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) ||
            limit <= 0)
        {
            return value;
        }

        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        var kept = new System.Text.StringBuilder();
        int count = 0;
        while (elements.MoveNext())
        {
            if (count == limit) return kept.Append('…').ToString();
            kept.Append(elements.GetTextElement());
            count++;
        }
        return value;
    }
}
