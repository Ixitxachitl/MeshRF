// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>The waypoint: and require: actions, and the multi-value json: form
/// they depend on.</summary>
public class ScriptWaypointTests
{
    private static readonly ScriptSelf Self = new(0x11111111, "ME", "My Node", 101);
    private static readonly DateTimeOffset Noon = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static ScriptParseResult Parse(string actions) =>
        ScriptParser.Parse("trigger:\n  - every: 10m\naction:\n" + actions);

    private static ScriptExpansion Expansion(params (string Name, string Value)[] http)
    {
        var expansion = new ScriptExpansion(new ScriptEvent { Self = Self, At = Noon });
        foreach (var (name, value) in http) expansion.SetHttpResult(name, value);
        return expansion;
    }

    // ----- waypoint -----------------------------------------------------------

    [Fact]
    public void A_Complete_Waypoint_Parses()
    {
        var result = Parse(
            """
              - waypoint:
                  lat: "{http.lat}"
                  lon: "{http.lon}"
                  name: "Lightning"
                  description: "{http.count} strikes"
                  icon: "⛈"
                  radius: 30mi
                  expires: 2h
                  notify_on_enter: true
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        var waypoint = result.Script!.Actions[0].Waypoint!;

        Assert.Equal("{http.lat}", waypoint.Latitude);
        Assert.Equal("Lightning", waypoint.Name);
        Assert.Equal("⛈", waypoint.Icon);
        Assert.Equal(48280u, waypoint.RadiusM);          // 30 miles
        Assert.Equal(TimeSpan.FromHours(2), waypoint.Expires);
        Assert.True(waypoint.NotifyOnEnter);
        // Locked by default, so a marker a script drops cannot be rewritten by
        // whoever receives it.
        Assert.True(waypoint.LockToMe);
    }

    [Fact]
    public void Home_Uses_This_Nodes_Location_And_Needs_No_Lon()
    {
        var result = Parse("  - waypoint:\n      lat: home\n      name: Storm\n      expires: 1h\n");

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.True(result.Script!.Actions[0].Waypoint!.UseHome);
    }

    [Theory]
    [InlineData("30mi", 48280u)]
    [InlineData("50km", 50000u)]
    [InlineData("500m", 500u)]
    [InlineData("500", 500u)]
    [InlineData("1.5km", 1500u)]
    public void Radius_Accepts_Miles_Kilometres_And_Metres(string text, uint expected)
    {
        var result = Parse($"  - waypoint:\n      lat: home\n      expires: 1h\n      radius: {text}\n");

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Equal(expected, result.Script!.Actions[0].Waypoint!.RadiusM);
    }

    [Fact]
    public void A_Nonsense_Radius_Is_Rejected()
    {
        var result = Parse("  - waypoint:\n      lat: home\n      radius: soon\n");

        Assert.False(result.IsValid);
        Assert.Contains("30mi, 50km or 500m", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Literal_Coordinate_Out_Of_Range_Is_Rejected()
    {
        Assert.False(Parse("  - waypoint:\n      lat: 91\n      lon: 0\n").IsValid);
        Assert.False(Parse("  - waypoint:\n      lat: 0\n      lon: 181\n").IsValid);
        Assert.True(Parse("  - waypoint:\n      lat: 37.77\n      lon: -122.41\n      expires: 1h\n").IsValid);
    }

    [Fact]
    public void A_Placeholder_Coordinate_Is_Accepted_Because_It_Resolves_Later()
    {
        var result = Parse("  - waypoint:\n      lat: \"{http.lat}\"\n      lon: \"{http.lon}\"\n      expires: 1h\n");
        Assert.True(result.IsValid, result.FirstError?.ToString());
    }

    [Fact]
    public void Notify_Without_A_Radius_Is_Rejected()
    {
        var result = Parse("  - waypoint:\n      lat: home\n      expires: 1h\n      notify_on_enter: true\n");

        Assert.False(result.IsValid);
        Assert.Contains("need a radius:", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Waypoint_Without_An_Expiry_Warns()
    {
        // An automated marker nobody clears stays on everyone's map forever.
        var result = Parse("  - waypoint:\n      lat: home\n      name: Storm\n");

        Assert.True(result.IsValid);
        Assert.Contains(result.Problems, p => p.Message.Contains("stays on everyone's map"));
    }

    [Fact]
    public void A_Waypoint_Can_Be_Addressed_To_A_Node()
    {
        var result = Parse("  - waypoint:\n      lat: home\n      expires: 1h\n      to: \"!a1b2c3d4\"\n");
        Assert.True(result.IsValid, result.FirstError?.ToString());

        var engine = new ScriptEngine();
        engine.Load([new ScriptFile("a.yaml", "a.yaml", "x", Enabled: true, result)], Noon);

        var run = Assert.Single(engine.Tick(Noon.AddMinutes(11), Self));
        Assert.Equal(0xa1b2c3d4u, run.Actions[0].ToNode);
    }

    [Fact]
    public void A_Waypoint_Cannot_Name_A_Node_And_A_Channel_At_Once()
    {
        var result = Parse(
            "  - waypoint:\n      lat: home\n      expires: 1h\n      to: \"!a1b2c3d4\"\n      channel: LongFast\n");

        Assert.False(result.IsValid);
        Assert.Contains("not both", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Waypoints_To_Is_Rejected_When_It_Is_Not_A_Node_Id()
    {
        var result = Parse("  - waypoint:\n      lat: home\n      expires: 1h\n      to: \"Bob\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("is not a node id", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Waypoint_Counts_As_Airtime()
    {
        var result = Parse("  - waypoint:\n      lat: home\n      expires: 1h\n");
        var engine = new ScriptEngine();
        engine.Load([new ScriptFile("a.yaml", "a.yaml", "x", Enabled: true, result)], Noon);

        var run = Assert.Single(engine.Tick(Noon.AddMinutes(11), Self));
        Assert.True(run.Actions[0].Transmits);
    }

    // ----- require ------------------------------------------------------------

    [Theory]
    [InlineData("above: 5", "7", true)]
    [InlineData("above: 5", "3", false)]
    [InlineData("below: 5", "3", true)]
    [InlineData("at_least: 5", "5", true)]
    [InlineData("at_most: 5", "5", true)]
    [InlineData("equals: ok", "OK", true)]
    [InlineData("not_equals: ok", "OK", false)]
    [InlineData("contains: storm", "Thunderstorm warning", true)]
    [InlineData("matches: \"^Thunder\"", "Thunderstorm", true)]
    [InlineData("matches: \"^Thunder\"", "Rain", false)]
    [InlineData("not_empty: true", "something", true)]
    [InlineData("not_empty: true", "", false)]
    [InlineData("is_empty: true", "", true)]
    public void Comparisons_Evaluate(string comparison, string value, bool expected)
    {
        var result = Parse($"  - require:\n      value: \"{{http.v}}\"\n      {comparison}\n");
        Assert.True(result.IsValid, result.FirstError?.ToString());

        var requirement = result.Script!.Actions[0].Require!;
        Assert.Equal(expected, requirement.Holds(Expansion(("v", value)), out _));
    }

    [Theory]
    [InlineData("200", true)]
    [InlineData("232", true)]
    [InlineData("216", true)]
    [InlineData("500", false)]
    [InlineData("199", false)]
    public void Between_Bounds_A_Numeric_Range(string value, bool expected)
    {
        // The thunderstorm group in OpenWeather's condition codes.
        var result = Parse("  - require:\n      value: \"{http.code}\"\n      between: [200, 232]\n");
        Assert.True(result.IsValid, result.FirstError?.ToString());

        Assert.Equal(expected, result.Script!.Actions[0].Require!.Holds(Expansion(("code", value)), out _));
    }

    // ----- within: the distance filter an API may not offer ---------------------

    private static ScriptExpansion Located(double lat, double lon, params (string, string)[] http)
    {
        var expansion = new ScriptExpansion(new ScriptEvent
        {
            Self = new ScriptSelf(1, "ME", "My Node", 101, Latitude: lat, Longitude: lon),
        });
        foreach (var (name, value) in http) expansion.SetHttpResult(name, value);
        return expansion;
    }

    [Theory]
    // San Francisco to Oakland, about 13 km.
    [InlineData("37.8044,-122.2712", "30mi", true)]
    [InlineData("37.8044,-122.2712", "5mi", false)]
    // San Francisco to Sacramento, about 120 km.
    [InlineData("38.5816,-121.4944", "30mi", false)]
    [InlineData("38.5816,-121.4944", "100mi", true)]
    public void Within_Measures_From_This_Nodes_Home(string position, string range, bool expected)
    {
        var result = Parse($"  - require:\n      value: \"{{http.pos}}\"\n      within: {range}\n");
        Assert.True(result.IsValid, result.FirstError?.ToString());

        var holds = result.Script!.Actions[0].Require!
            .Holds(Located(37.7749, -122.4194, ("pos", position)), out var detail);

        Assert.Equal(expected, holds);
        Assert.Contains("km away", detail);
    }

    [Fact]
    public void Within_Fails_Closed_Without_A_Home_Location_Or_A_Position()
    {
        var requirement = Parse("  - require:\n      value: \"{http.pos}\"\n      within: 30mi\n")
            .Script!.Actions[0].Require!;

        // Nothing to measure from.
        var noHome = new ScriptExpansion(new ScriptEvent { Self = Self });
        noHome.SetHttpResult("pos", "37.8,-122.3");
        Assert.False(requirement.Holds(noHome, out var why));
        Assert.Contains("no home location", why);

        // Nothing to measure to — an unfilled or malformed value must not pass.
        Assert.False(requirement.Holds(Located(37.7749, -122.4194, ("pos", "")), out _));
        Assert.False(requirement.Holds(Located(37.7749, -122.4194, ("pos", "not a position")), out _));
    }

    [Fact]
    public void A_Nonsense_Within_Distance_Is_Rejected()
    {
        var result = Parse("  - require:\n      value: \"{http.pos}\"\n      within: soon\n");

        Assert.False(result.IsValid);
        Assert.Contains("30mi, 50km or 500m", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Non_Numeric_Value_Fails_Rather_Than_Throwing()
    {
        // An API that answered with an error string where a reading was
        // expected should stop the script, explicably.
        var result = Parse("  - require:\n      value: \"{http.v}\"\n      above: 5\n");

        Assert.False(result.Script!.Actions[0].Require!.Holds(Expansion(("v", "unavailable")), out var detail));
        Assert.Contains("is not a number", detail);
    }

    [Fact]
    public void An_Unfilled_Placeholder_Fails_Closed()
    {
        // Nothing ever set {http.v} — the fetch was skipped, so the guard must
        // not pass by accident.
        var result = Parse("  - require:\n      value: \"{http.v}\"\n      not_empty: true\n");

        Assert.False(result.Script!.Actions[0].Require!.Holds(Expansion(), out _));
    }

    [Fact]
    public void Require_Needs_Exactly_One_Comparison()
    {
        Assert.False(Parse("  - require:\n      value: \"{http.v}\"\n").IsValid);

        var two = Parse("  - require:\n      value: \"{http.v}\"\n      above: 1\n      below: 9\n");
        Assert.False(two.IsValid);
        Assert.Contains("more than one comparison", two.FirstError!.Value.Message);
    }

    [Fact]
    public void Require_Needs_A_Value()
    {
        var result = Parse("  - require:\n      above: 5\n");

        Assert.False(result.IsValid);
        Assert.Contains("needs a value:", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Bad_Between_Range_Is_Rejected()
    {
        var result = Parse("  - require:\n      value: \"{http.v}\"\n      between: [200]\n");

        Assert.False(result.IsValid);
        Assert.Contains("exactly two bounds", result.FirstError!.Value.Message);
    }

    [Fact]
    public void Require_Does_Not_Count_As_Airtime()
    {
        var result = Parse("  - require:\n      value: \"{http.v}\"\n      above: 5\n");
        var engine = new ScriptEngine();
        engine.Load([new ScriptFile("a.yaml", "a.yaml", "x", Enabled: true, result)], Noon);

        var run = Assert.Single(engine.Tick(Noon.AddMinutes(11), Self));
        Assert.False(run.Actions[0].Transmits);
        Assert.False(run.Transmits);
    }

    // ----- multi-value json ---------------------------------------------------

    [Fact]
    public void Json_Can_Pull_Several_Values_From_One_Response()
    {
        // A strike's latitude and longitude are useless apart, and fetching
        // them separately would mean two requests against a moving target.
        var result = Parse(
            """
              - http:
                  url: "https://api.test/lightning"
                  json:
                    lat: report[0].loc.lat
                    lon: report[0].loc.long
                    count: report[0].count
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        var extractions = result.Script!.Actions[0].Http!.Extractions;

        Assert.Equal(3, extractions.Count);
        Assert.Equal(["lat", "lon", "count"], extractions.Select(e => e.SaveAs).ToArray());
        Assert.Equal("report[0].loc.long", extractions[1].JsonPath);
    }

    [Fact]
    public void A_Bad_Path_In_The_Mapping_Names_Which_One()
    {
        var result = Parse(
            "  - http:\n      url: \"https://api.test/\"\n      json:\n        lat: ok.path\n        lon: \"a..b\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("json: lon:", result.FirstError!.Value.Message);
    }

    // ----- this node's own position -------------------------------------------

    [Fact]
    public void My_Location_Fills_In_From_The_Home_Location()
    {
        var located = new ScriptExpansion(new ScriptEvent
        {
            Self = new ScriptSelf(1, "ME", "My Node", 101, Latitude: 37.7749, Longitude: -122.4194),
        });

        Assert.Equal("37.7749,-122.4194", located.Expand("{my.lat},{my.lon}"));
    }

    [Fact]
    public void A_Coordinate_Is_Invariant_Whatever_The_Host_Locale_Does()
    {
        // A decimal comma reaching a URL would split the parameter in two.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var located = new ScriptExpansion(new ScriptEvent
            {
                Self = new ScriptSelf(1, "ME", "My Node", 101, Latitude: 37.7749, Longitude: -122.4194),
            });
            Assert.Equal("37.7749", located.Expand("{my.lat}"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void My_Location_Is_Empty_When_No_Home_Is_Set()
    {
        // Empty, not "?", because these go into URLs and waypoints — and empty
        // is what a require: can test for.
        var unset = new ScriptExpansion(new ScriptEvent { Self = Self });

        Assert.Equal(string.Empty, unset.Expand("{my.lat}"));

        var result = Parse("  - require:\n      value: \"{my.lat}\"\n      not_empty: true\n");
        Assert.False(result.Script!.Actions[0].Require!.Holds(unset, out _));
    }

    [Fact]
    public void My_Location_Is_A_Known_Placeholder()
    {
        var result = Parse("  - log: \"at {my.lat},{my.lon}\"\n");

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Credential_Accepts_A_Pair_For_Apis_That_Want_Id_And_Secret()
    {
        var result = Parse(
            "  - http:\n      url: \"https://api.test/\"\n      credential: [wx-id, wx-secret]\n");

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Equal(["wx-id", "wx-secret"], result.Script!.Actions[0].Http!.CredentialNames.ToArray());
    }

    [Fact]
    public void A_Single_Credential_Still_Works()
    {
        var result = Parse("  - http:\n      url: \"https://api.test/\"\n      credential: openai\n");

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Equal(["openai"], result.Script!.Actions[0].Http!.CredentialNames.ToArray());
    }

    [Fact]
    public void Status_Cannot_Be_Reused_As_An_Extraction_Name()
    {
        var result = Parse("  - http:\n      url: \"https://api.test/\"\n      json:\n        status: a.b\n");

        Assert.False(result.IsValid);
        Assert.Contains("is taken", result.FirstError!.Value.Message);
    }
}
