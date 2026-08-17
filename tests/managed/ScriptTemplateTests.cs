// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

public class ScriptTemplateTests
{
    private static readonly ScriptEvent Sample = new()
    {
        Text = "!wx london now",
        FromNode = 0xa1b2c3d4,
        FromShort = "PEER",
        FromLong = "Peer Node",
        Channel = "LongFast",
        IsDirect = true,
        SnrDb = -7.25,
        RssiDbm = -104.6,
        Hops = 2,
        Self = new ScriptSelf(0x11111111, "ME", "My Node", 101),
        At = new DateTimeOffset(2026, 8, 14, 9, 5, 0, TimeSpan.Zero),
    };

    [Theory]
    [InlineData("{msg.text}", "!wx london now")]
    [InlineData("{from.id}", "!a1b2c3d4")]
    [InlineData("{from.short}", "PEER")]
    [InlineData("{from.long}", "Peer Node")]
    [InlineData("{channel}", "LongFast")]
    [InlineData("{snr}", "-7.3")]
    [InlineData("{rssi}", "-105")]
    [InlineData("{hops}", "2")]
    [InlineData("{hops|keycap}", "2️⃣")]
    [InlineData("{time}", "09:05")]
    [InlineData("{date}", "2026-08-14")]
    [InlineData("{my.id}", "!11111111")]
    [InlineData("{my.short}", "ME")]
    [InlineData("{my.long}", "My Node")]
    public void Placeholders_Expand(string template, string expected)
    {
        Assert.Equal(expected, ScriptTemplate.Expand(template, Sample));
    }

    [Fact]
    public void A_Hop_Count_Becomes_A_Keycap()
    {
        // Digit + VS16 + combining keycap for 0-9, then the single-code-point
        // ten. A packet heard directly is 0 hops, which is the answer a range
        // test most wants to see.
        Assert.Equal("0️⃣", Expand("{hops|keycap}", 0));
        Assert.Equal("7️⃣", Expand("{hops|keycap}", 7));
        Assert.Equal("\U0001F51F", Expand("{hops|keycap}", 10));

        // Past the keycaps there is no glyph to use, and no real packet can get
        // here — firmware caps hop_limit at 7.
        Assert.Equal("11", Expand("{hops|keycap}", 11));
    }

    private static string Expand(string template, int hops) =>
        ScriptTemplate.Expand(template, Sample with { Hops = hops });

    [Theory]
    [InlineData("{from.short|lower}", "peer")]
    [InlineData("{from.long|upper}", "PEER NODE")]
    [InlineData("{snr|round:0}", "-7")]
    [InlineData("{snr|round:1}", "-7.3")]
    [InlineData("{msg.text|truncate:6}", "!wx lo…")]
    [InlineData("{my.lat|default:nowhere}", "nowhere")]
    // Left to right, and a filter may follow one that changed the value.
    [InlineData("{msg.text|upper|truncate:3}", "!WX…")]
    // A filter that cannot do anything with the value leaves it alone rather
    // than emptying the sentence.
    [InlineData("{from.short|round:2}", "PEER")]
    public void Filters_Transform_The_Value(string template, string expected)
    {
        Assert.Equal(expected, ScriptTemplate.Expand(template, Sample));
    }

    [Theory]
    // Each point owns 22.5°, centred on its bearing rather than starting there,
    // so anything within 11.25° of north is still north.
    [InlineData("0", "N", "↑")]
    [InlineData("11", "N", "↑")]
    [InlineData("12", "NNE", "↑")]
    [InlineData("35", "NE", "↗")]
    [InlineData("180", "S", "↓")]
    // The N sector runs from 348.75°, so 348 is still NNW and 349 is north.
    [InlineData("348", "NNW", "↖")]
    [InlineData("349", "N", "↑")]
    // Wraps rather than failing: an API may report either side of the circle.
    [InlineData("360", "N", "↑")]
    [InlineData("-45", "NW", "↖")]
    public void A_Bearing_Becomes_A_Compass_Point_And_An_Arrow(string degrees, string point, string arrow)
    {
        var evt = Sample with { Text = degrees };
        Assert.Equal(point, ScriptTemplate.Expand("{msg.text|compass}", evt));
        Assert.Equal(arrow, ScriptTemplate.Expand("{msg.text|arrow}", evt));
    }

    [Theory]
    // Matched on the words, so the qualifiers APIs attach come out the same.
    [InlineData("clear sky", "☀️")]
    [InlineData("few clouds", "⛅")]
    [InlineData("broken clouds", "☁️")]
    [InlineData("light rain", "🌧️")]
    [InlineData("heavy intensity rain", "🌧️")]
    [InlineData("shower rain", "🌦️")]
    [InlineData("thunderstorm with heavy rain", "⛈️")]
    // Order matters where two rules could match: freezing rain is snow-shaped,
    // and a thunderstorm is a storm before it is rain.
    [InlineData("freezing rain", "🌨️")]
    [InlineData("mist", "🌫️")]
    // Nothing matched: better the words than a wrong picture.
    [InlineData("volcanic haze of unknown origin", "🌫️")]
    [InlineData("brisk", "brisk")]
    public void A_Condition_Becomes_One_Emoji(string description, string expected)
    {
        Assert.Equal(expected, ScriptTemplate.Expand("{msg.text|weather}", Sample with { Text = description }));
    }

    [Fact]
    public void Units_Convert_For_A_Reader_Who_Thinks_In_Them()
    {
        // Full precision out, so the script's own round: decides the decimals.
        Assert.Equal("51.944", ScriptTemplate.Expand("{msg.text|fahrenheit}", Sample with { Text = "11.08" }));
        Assert.Equal("52", ScriptTemplate.Expand("{msg.text|fahrenheit|round:0}", Sample with { Text = "11.08" }));
        Assert.Equal("32", ScriptTemplate.Expand("{msg.text|fahrenheit|round:0}", Sample with { Text = "0" }));
        Assert.Equal("-40", ScriptTemplate.Expand("{msg.text|fahrenheit|round:0}", Sample with { Text = "-40" }));

        Assert.Equal("5.4", ScriptTemplate.Expand("{msg.text|mph|round:1}", Sample with { Text = "2.4" }));
        Assert.Equal("0.10", ScriptTemplate.Expand("{msg.text|inches|round:2}", Sample with { Text = "2.54" }));

        // Not a reading: left alone rather than turned into a wrong number.
        Assert.Equal("?", ScriptTemplate.Expand("{msg.text|fahrenheit}", Sample with { Text = "?" }));
    }

    [Fact]
    public void An_Epoch_Becomes_A_Local_Wall_Clock_Time()
    {
        var when = new DateTimeOffset(new DateTime(2026, 8, 16, 6, 12, 0, DateTimeKind.Local));
        var evt = Sample with { Text = when.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Assert.Equal("6:12 AM", ScriptTemplate.Expand("{msg.text|clock}", evt));

        // A 24-hour time — which is what {time} expands to — reads as the same
        // wall clock the sunrise beside it is written in.
        Assert.Equal("6:12 AM", ScriptTemplate.Expand("{msg.text|clock}", Sample with { Text = "06:12" }));
        Assert.Equal("5:31 PM", ScriptTemplate.Expand("{msg.text|clock}", Sample with { Text = "17:31" }));

        // Anything that is neither is left alone.
        Assert.Equal("dawn", ScriptTemplate.Expand("{msg.text|clock}", Sample with { Text = "dawn" }));
    }

    [Fact]
    public void A_Prefix_Only_Appears_When_There_Is_Something_To_Prefix()
    {
        // The mirror of default:, for an optional field that needs a separator:
        // a state the geocoder returned, or no dangling comma when it did not.
        Assert.Equal("Alta, CA",
            ScriptTemplate.Expand("Alta{msg.text|prefix:, }", Sample with { Text = "CA" }));
        Assert.Equal("Alta",
            ScriptTemplate.Expand("Alta{msg.text|prefix:, }", Sample with { Text = "" }));
        Assert.Equal("Alta",
            ScriptTemplate.Expand("Alta{msg.text|prefix:, }", Sample with { Text = "   " }));
    }

    [Theory]
    // Checked against eclipses, which pin the phase exactly: a solar eclipse
    // only happens at a new moon and a lunar one only at a full moon. Spread
    // across 26 years so a drifting cycle length would show up.
    [InlineData("2000-01-21", "🌕")] // total lunar eclipse
    [InlineData("2017-08-21", "🌑")] // total solar eclipse, USA
    [InlineData("2019-01-21", "🌕")] // total lunar eclipse
    [InlineData("2024-04-08", "🌑")] // total solar eclipse, USA
    [InlineData("2025-03-14", "🌕")] // total lunar eclipse
    [InlineData("2026-08-12", "🌑")] // total solar eclipse, Spain and Iceland
    public void A_Date_Becomes_A_Moon_Phase(string date, string expected)
    {
        Assert.Equal(expected, ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = date }));
    }

    [Fact]
    public void A_Moon_Phase_Can_Come_From_An_Api_Or_A_Timestamp_Too()
    {
        // 0-1 as a weather API reports it: 0 and 1 new, 0.25 first quarter,
        // 0.5 full, 0.75 last.
        Assert.Equal("🌑", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "0" }));
        Assert.Equal("🌓", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "0.25" }));
        Assert.Equal("🌕", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "0.5" }));
        Assert.Equal("🌗", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "0.75" }));
        Assert.Equal("🌑", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "1" }));

        // A timestamp cannot be mistaken for one of those, since a unix time in
        // that range is 1970. This is the 2024 solar eclipse.
        var eclipse = new DateTimeOffset(2024, 4, 8, 18, 17, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal("🌑", ScriptTemplate.Expand("{msg.text|moon}",
            Sample with { Text = eclipse.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

        // A phase name, as Home Assistant's moon sensor reports it, so a script
        // ported from one keeps working against the same data.
        Assert.Equal("🌔", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "waxing_gibbous" }));
        Assert.Equal("🌕", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "Full Moon" }));

        // Anything else is left alone rather than guessed at.
        Assert.Equal("cheese", ScriptTemplate.Expand("{msg.text|moon}", Sample with { Text = "cheese" }));
    }

    [Fact]
    public void The_Moon_Walks_Through_Every_Phase_In_Order()
    {
        // One synodic month from a known new moon has to visit all eight, once
        // each and in sequence — a bucketing mistake would repeat or skip one.
        var start = new DateTime(2024, 4, 8, 18, 17, 0, DateTimeKind.Utc);
        var seen = new List<string>();
        for (int hour = 0; hour < 30 * 24; hour++)
        {
            var glyph = ScriptTemplate.Expand("{msg.text|moon}",
                Sample with { Text = start.AddHours(hour).ToString("o") });
            if (seen.Count == 0 || seen[^1] != glyph) seen.Add(glyph);
        }

        Assert.Equal(["🌑", "🌒", "🌓", "🌔", "🌕", "🌖", "🌗", "🌘", "🌑"], seen);
    }

    [Fact]
    public void The_Senders_Position_Comes_From_The_Node_Table()
    {
        var located = Sample with { FromLatitude = 39.20106, FromLongitude = -120.82 };
        Assert.Equal("39.20106,-120.82", ScriptTemplate.Expand("{from.lat},{from.lon}", located));

        // Empty, not "?", when they have never sent one: these go into URLs,
        // and empty is what a require: or a when: tests for.
        Assert.Equal(",", ScriptTemplate.Expand("{from.lat},{from.lon}", Sample));
    }

    [Fact]
    public void An_Unknown_Filter_Leaves_The_Token_As_Written()
    {
        // Same treatment as an unknown placeholder: the editor warned, and
        // sending it literally makes the mistake visible.
        Assert.Equal("{snr|rnd:1}", ScriptTemplate.Expand("{snr|rnd:1}", Sample));

        // Including when it follows one that would have worked, so half a chain
        // never quietly goes out.
        Assert.Equal("{msg.text|upper|shout}", ScriptTemplate.Expand("{msg.text|upper|shout}", Sample));
    }

    [Fact]
    public void A_Filter_Runs_Before_The_Value_Is_Escaped()
    {
        // The escape has to see what the chain produced, or a filter could
        // reintroduce a character the URL was encoded to keep out.
        var evt = Sample with { Text = "a b&c" };
        Assert.Equal("q=A%20B%26C",
            ScriptTemplate.Expand("q={msg.text|upper}", evt, escape: Uri.EscapeDataString));
    }

    [Fact]
    public void The_Mains_Sentinel_Is_Not_Reported_As_A_Percentage()
    {
        // 101 is the "powered from mains" value this app reports, so printing
        // "101%" would be a lie.
        Assert.Equal("mains", ScriptTemplate.Expand("{node.battery}", Sample));

        var onBattery = Sample with { Self = Sample.Self with { BatteryPct = 64 } };
        Assert.Equal("64", ScriptTemplate.Expand("{node.battery}", onBattery));

        var unknown = Sample with { Self = Sample.Self with { BatteryPct = null } };
        Assert.Equal("?", ScriptTemplate.Expand("{node.battery}", unknown));
    }

    [Fact]
    public void A_Missing_Measurement_Reads_As_A_Question_Mark()
    {
        // An empty gap in a sentence reads like a bug; "?" reads like missing data.
        var noSignal = Sample with { SnrDb = null, RssiDbm = null };
        Assert.Equal("? dB / ? dBm", ScriptTemplate.Expand("{snr} dB / {rssi} dBm", noSignal));
    }

    [Fact]
    public void An_Unknown_Placeholder_Is_Left_Exactly_As_Written()
    {
        // The editor already warned. Sending it literally makes the mistake
        // visible, where blanking it would hide it.
        Assert.Equal("hi {from.shrt}", ScriptTemplate.Expand("hi {from.shrt}", Sample));
    }

    [Fact]
    public void Braces_That_Are_Not_Placeholders_Are_Left_Alone()
    {
        Assert.Equal("a { b } c {}", ScriptTemplate.Expand("a { b } c {}", Sample));
    }

    [Fact]
    public void Numbered_Placeholders_Index_Args_And_Captures()
    {
        var args = new[] { "london", "now" };
        var captures = new[] { "cap-one", "cap-two" };

        Assert.Equal("london now", ScriptTemplate.Expand("{arg1} {arg2}", Sample, args, captures));
        Assert.Equal("cap-one", ScriptTemplate.Expand("{cap1}", Sample, args, captures));
        // Out of range is empty, not an error: a script written for two
        // arguments should still send something when given one.
        Assert.Equal("", ScriptTemplate.Expand("{arg9}", Sample, args, captures));
        Assert.Equal("", ScriptTemplate.Expand("{arg1}", Sample, args: null));
    }

    [Theory]
    [InlineData("!ping", new string[0])]
    [InlineData("!ping ", new string[0])]
    [InlineData("!echo one", new[] { "one" })]
    [InlineData("!echo  one   two ", new[] { "one", "two" })]
    public void Arguments_Split_On_Whitespace_After_The_Command(string text, string[] expected)
    {
        Assert.Equal(expected, ScriptTemplate.SplitArguments(text));
    }

    [Fact]
    public void Clamping_Never_Splits_A_Character_In_Half()
    {
        // Every emoji is 4 UTF-8 bytes, so a naive cut at 200 would land inside
        // one and leave a broken glyph on the far end.
        var text = string.Concat(Enumerable.Repeat("😀", 60));
        var clamped = ScriptTemplate.ClampToPayload(text);

        Assert.True(Encoding.UTF8.GetByteCount(clamped) <= 200);
        Assert.Equal(200 / 4, clamped.EnumerateRunes().Count());
        // Round-trips cleanly, which it would not if a sequence were cut.
        Assert.Equal(clamped, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(clamped)));
    }

    [Fact]
    public void Short_Text_Passes_Through_Untouched()
    {
        Assert.Equal("pong", ScriptTemplate.ClampToPayload("pong"));
    }
}
