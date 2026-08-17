// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The scripts under samples/scripts are documentation people copy and run, so
/// they have to stay valid as the vocabulary moves. A rename in the parser that
/// silently invalidated them would otherwise only be found by whoever pasted
/// one in and watched it fail.
/// </summary>
public class SampleScriptTests
{
    /// <summary>Walks up from the test binary to the repository root, which is
    /// wherever the solution file is.</summary>
    private static string SamplesDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshRF.sln")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            var samples = Path.Combine(dir!.FullName, "samples", "scripts");
            Assert.True(Directory.Exists(samples), $"samples directory not found at {samples}");
            return samples;
        }
    }

    public static TheoryData<string> SampleFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(SamplesDirectory, "*.yaml"))
                data.Add(Path.GetFileName(path));
            return data;
        }
    }

    [Fact]
    public void The_Library_Is_Not_Empty()
    {
        // Guards the discovery above: a broken path would make every theory
        // below vacuously pass.
        Assert.NotEmpty(Directory.GetFiles(SamplesDirectory, "*.yaml"));
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Parses_Without_Errors(string fileName)
    {
        var result = ScriptParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName)));

        Assert.True(result.IsValid, $"{fileName}: {result.FirstError}");
    }

    private static ScriptParseResult ParseSample(string fileName) =>
        ScriptParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName)));

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Ships_Disabled(string fileName)
    {
        // Copying a sample into the scripts folder must not start it
        // transmitting, whichever kind of file it is.
        Assert.False(ParseSample(fileName).Enabled, $"{fileName} ships enabled");
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Is_Throttled_And_Explains_Itself(string fileName)
    {
        var parse = ParseSample(fileName);
        Assert.False(string.IsNullOrWhiteSpace(parse.Alias), $"{fileName} has no alias to show in the list");

        if (parse.Sync is { } sync)
        {
            // A feed's throttle is how often it reads; it sends only what
            // actually changed, so there is no per-hour ceiling to set.
            Assert.True(sync.Every >= TimeSpan.FromMinutes(1), $"{fileName} polls too fast");
            return;
        }

        var script = parse.Script!;
        Assert.True(script.Limits.MaxPerHour > 0, $"{fileName} has no hourly ceiling");
        Assert.True(script.Limits.Cooldown > TimeSpan.Zero, $"{fileName} has no cooldown");
    }

    [Fact]
    public void The_Test_Channel_Sample_Answers_With_A_Hop_Keycap()
    {
        // The one sample whose whole output is a placeholder: if the keycap
        // filter ever stopped resolving, the script would broadcast the literal
        // "{hops|keycap}" and still look valid to every test above.
        var engine = Armed("test-hops.yaml");

        var run = Assert.Single(engine.Evaluate(Heard("Anyone up for a test?", "Test", hops: 1)));
        var action = Assert.Single(run.Actions);
        Assert.Equal("1️⃣", run.Expansion.ExpandMessage(action.Text));

        // The word, not any word containing it, and the Test channel only.
        Assert.Empty(engine.Evaluate(Heard("latest firmware is up", "Test", hops: 1)));
        Assert.Empty(engine.Evaluate(Heard("test", "LongFast", hops: 1)));
    }

    /// <summary>
    /// OpenWeather's actual response shapes, which the script's json: paths are
    /// read against for real.
    /// </summary>
    /// <remarks>
    /// Stubbing the extracted values instead would test the wording and miss
    /// the thing most likely to be wrong: a path written for the wrong shape.
    /// The postcode endpoint answers with an object and the other two with a
    /// list, and that difference is invisible unless the fixture carries it.
    /// </remarks>
    private static string PlaceJson(string city, string country, bool asList, string? state = "CA") =>
        asList
            ? $$"""[{"name":"{{city}}","lat":39.2,"lon":-120.8,"country":"{{country}}"{{(state is null ? "" : $",\"state\":\"{state}\"")}}}]"""
            : $$"""{"zip":"95701","name":"{{city}}","lat":39.2,"lon":-120.8,"country":"{{country}}"}""";

    private static string ForecastJson() =>
        $$"""
        {"lat":39.2,"lon":-120.8,"timezone":"America/Los_Angeles",
         "current":{"dt":1,"sunrise":{{Epoch(6, 12)}},"sunset":{{Epoch(19, 48)}},
                    "temp":11.08,"feels_like":10.2,"humidity":72,
                    "wind_speed":2.4,"wind_deg":35,"rain":{"1h":2.54},
                    "weather":[{"id":500,"main":"Rain","description":"light rain","icon":"10d"}]},
         "daily":[{"dt":1,"moon_phase":0.5,"summary":"Expect a day of partly cloudy weather"}]}
        """;

    /// <summary>Answers both requests of a successful lookup, as the API would.</summary>
    private static Func<string, string> Answering(string city = "Alta", string country = "US") =>
        url => url.Contains("geo/1.0")
            ? PlaceJson(city, country, asList: !url.Contains("geo/1.0/zip"))
            : ForecastJson();

    [Fact]
    public void The_Weather_Sample_Reports_Where_The_Sender_Is_When_No_Place_Is_Named()
    {
        var (urls, replies) = Run("!wx", senderAt: (39.2, -120.8), respond: Answering());

        // The position the node table has for them, turned into a name to call
        // it by, then the forecast for it.
        Assert.Equal(2, urls.Count);
        Assert.Equal(
            "https://api.openweathermap.org/geo/1.0/reverse?lat=39.2&lon=-120.8&limit=1",
            urls[0]);
        Assert.StartsWith("https://api.openweathermap.org/data/3.0/onecall?lat=39.2&lon=-120.8", urls[1]);

        // The layout of the Home Assistant original — bold heading, then the
        // lines in its order — and the formatting it needed a template language
        // for: bearing to compass point and arrow, description to emoji, epoch
        // to wall-clock time, One Call's 0-1 phase to a moon, and — because
        // Alta is in the US — metric readings to Fahrenheit, miles per hour and
        // inches.
        var report = Assert.Single(replies);
        Assert.Equal(
            "**Alta, CA**\n" +
            $"{DateTime.Now:yyyy-MM-dd} {DateTime.Now:h:mm tt}\n" +
            "🌡️ 52°F feels 50°F 💧 72%\n" +
            "🌧️ 0.10in ❄️ 0in\n" +
            "📋 🌧️ light rain\n" +
            "💨 NE ↗ 5mph\n" +
            "🌛 🌕 🌅 6:12 AM 🌇 7:48 PM",
            report);

        // The reply is clamped as it expands, so this says the wording leaves
        // room rather than that it was cut to fit.
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(report) < 200,
            $"the report is {System.Text.Encoding.UTF8.GetByteCount(report)} bytes");
    }

    [Theory]
    // A postcode goes to the postcode endpoint...
    [InlineData("!wx 95701", "geo/1.0/zip?zip=95701")]
    [InlineData("!wx 8001,CH", "geo/1.0/zip?zip=8001%2CCH")]
    // ...and anything else to the geocoder, including a town whose name starts
    // with a number, which is what the postcode test has to not swallow.
    [InlineData("!wx Alta, CA", "geo/1.0/direct?q=Alta%2C%20CA&limit=1")]
    [InlineData("!wx 100 Mile House, BC", "geo/1.0/direct?q=100%20Mile%20House%2C%20BC&limit=1")]
    public void The_Weather_Sample_Sends_A_Named_Place_To_The_Right_Lookup(string text, string expected)
    {
        var (urls, replies) = Run(text, senderAt: (39.2, -120.8), respond: Answering());

        Assert.Equal(2, urls.Count);
        Assert.EndsWith(expected, urls[0]);
        // The named place is looked up instead of the sender's own position,
        // never as well as it.
        Assert.DoesNotContain("geo/1.0/reverse", urls[0]);
        Assert.StartsWith("https://api.openweathermap.org/data/3.0/onecall", urls[1]);

        // The postcode endpoint returns no state, so the heading is the town on
        // its own — prefix: leaves no comma dangling where there is nothing to
        // separate. The geocoder does return one, so those headings carry it.
        var heading = Assert.Single(replies).Split('\n')[0];
        Assert.Equal(text.StartsWith("!wx 95701") || text.StartsWith("!wx 8001")
            ? "**Alta**"
            : "**Alta, CA**", heading);
    }

    [Theory]
    // Fetched in metric either way; only the report changes. The US, Liberia
    // and Myanmar get Fahrenheit and miles per hour.
    [InlineData("US", "🌡️ 52°F feels 50°F 💧 72%", "💨 NE ↗ 5mph")]
    [InlineData("LR", "🌡️ 52°F feels 50°F 💧 72%", "💨 NE ↗ 5mph")]
    [InlineData("FR", "🌡️ 11°C feels 10°C 💧 72%", "💨 NE ↗ 2m/s")]
    [InlineData("JP", "🌡️ 11°C feels 10°C 💧 72%", "💨 NE ↗ 2m/s")]
    public void The_Weather_Sample_Answers_In_The_Units_The_Place_Uses(
        string country, string temperature, string wind)
    {
        var (urls, replies) = Run("!wx Somewhere", senderAt: (39.2, -120.8),
            respond: Answering("Somewhere", country));

        Assert.Contains("units=metric", urls[^1]);

        // Exactly one report goes out — the two are mutually exclusive, and a
        // reader getting both would be worse than getting the wrong one.
        var report = Assert.Single(replies);
        Assert.Contains(temperature, report);
        Assert.Contains(wind, report);
    }

    [Fact]
    public void The_Weather_Sample_Answers_On_The_Primary_Channel_Only()
    {
        var engine = Armed("weather.yaml");
        var asked = Heard("!wx 95701", "LongFast", hops: 1);

        Assert.Single(engine.Evaluate(asked with { IsPrimaryChannel = true }));

        // A secondary channel is somebody else's conversation, and a direct
        // message arrives on no channel at all.
        Assert.Empty(engine.Evaluate(asked with { IsPrimaryChannel = false }));
        Assert.Empty(engine.Evaluate(asked with { Channel = "PKC", IsDirect = true }));
    }

    [Fact]
    public void The_Weather_Sample_Says_So_When_The_Place_Is_Not_Found()
    {
        // OpenWeather answers an unknown place with an empty list rather than an
        // error, which optional: true turns into an empty {http.lat} — so the
        // weather call is skipped and the apology is what is left.
        var (urls, replies) = Run("!wx Atlantis", senderAt: (39.2, -120.8), respond: _ => "[]");

        Assert.Single(urls);
        Assert.Equal("I couldn't find Atlantis — try a postcode, or \"town, state\"",
                     Assert.Single(replies));
    }

    [Fact]
    public void The_Weather_Sample_Does_Not_Quote_An_Empty_Place_Back_At_You()
    {
        // The same apology covers a position that reverse-geocodes to nothing,
        // where there is no place name to quote.
        var (_, replies) = Run("!wx", senderAt: (39.2, -120.8), respond: _ => "[]");

        Assert.Equal("I couldn't find where you are — try a postcode, or \"town, state\"",
                     Assert.Single(replies));
    }

    [Fact]
    public void The_Weather_Sample_Stops_When_It_Has_Nowhere_To_Look()
    {
        // No place named and no position ever received from that node: nothing
        // to request, so it says so and stops rather than building a request
        // around an empty coordinate.
        var (urls, replies) = Run("!wx", senderAt: null, respond: Answering());

        Assert.Empty(urls);
        Assert.Equal("I don't know where you are — try !wx 95701 or !wx Alta, CA",
                     Assert.Single(replies));
    }

    /// <summary>
    /// Walks a sample's sequence the way the runner does — skipping an action
    /// whose when: does not hold, stopping at a failed require:, and feeding
    /// each http: answer into the expansion — and reports the URLs it asked for
    /// and the messages it would have sent.
    /// </summary>
    /// <param name="respond">The response body for one request, keyed by the
    /// expanded URL. Read through the script's own json: paths, so a path
    /// written for the wrong response shape fails here as it would on air.</param>
    private static (List<string> Urls, List<string> Replies) Run(
        string text,
        (double Lat, double Lon)? senderAt,
        Func<string, string> respond)
    {
        var evt = Heard(text, "LongFast", hops: 1) with { IsPrimaryChannel = true };
        if (senderAt is { } at) evt = evt with { FromLatitude = at.Lat, FromLongitude = at.Lon };

        var run = Assert.Single(Armed("weather.yaml").Evaluate(evt));
        List<string> urls = [], replies = [];

        foreach (var action in run.Actions)
        {
            if (action.When is { } gate && !gate.Holds(run.Expansion, out _)) continue;

            switch (action.Kind)
            {
                case ScriptActionKind.Require:
                    if (action.Require!.Holds(run.Expansion, out _)) continue;
                    return (urls, replies);

                case ScriptActionKind.Http:
                    var url = run.Expansion.ExpandUrl(action.Http!.Url);
                    urls.Add(url);
                    var body = respond(url);
                    foreach (var extraction in action.Http.Extractions)
                    {
                        var value = JsonValuePath.Read(body, extraction.JsonPath, out var error);
                        // Same rule the client applies: a missing path is empty
                        // when the action said absence was expected, and stops
                        // the sequence otherwise.
                        if (value is null && !action.Http.Optional)
                            Assert.Fail($"{extraction.SaveAs}: {extraction.JsonPath} — {error}");
                        run.Expansion.SetHttpResult(extraction.SaveAs, value ?? string.Empty);
                    }
                    continue;

                default:
                    replies.Add(run.Expansion.ExpandMessage(action.Text));
                    continue;
            }
        }

        return (urls, replies);
    }

    /// <summary>A unix timestamp for today at a local wall-clock time, so the
    /// clock filter's output does not move with the test machine's zone.</summary>
    private static string Epoch(int hour, int minute) =>
        new DateTimeOffset(new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day,
                                        hour, minute, 0, DateTimeKind.Local))
            .ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Loads one sample as if it had been copied in and switched on.</summary>
    private static ScriptEngine Armed(string fileName)
    {
        var path = Path.Combine(SamplesDirectory, fileName);
        var parse = ParseSample(fileName);
        var file = new ScriptFile(fileName, path, File.ReadAllText(path), Enabled: true, parse);

        var engine = new ScriptEngine();
        engine.Load([file], DateTimeOffset.Now);
        Assert.Equal(1, engine.ArmedCount);
        return engine;
    }

    private static ScriptEvent Heard(string text, string channel, int hops) => new()
    {
        Kind = ScriptEventKind.Text,
        Text = text,
        FromNode = 0xa1b2c3d4,
        FromShort = "PEER",
        Channel = channel,
        Hops = hops,
        PacketId = 0xdeadbeef,
        Self = new ScriptSelf(0x11111111, "ME", "My Node", 101),
        At = DateTimeOffset.Now,
    };

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Leaves_No_Waypoint_Nobody_Can_Clear(string fileName)
    {
        var parse = ParseSample(fileName);

        if (parse.Sync is { } sync)
        {
            // A mirrored marker is allowed to outlive a clock, because the sync
            // retires it when the record goes. What it must not be is both
            // unexpiring and locked, which would leave it on every map with
            // nobody able to remove it.
            Assert.True(sync.Waypoint.Expires > TimeSpan.Zero || !sync.Waypoint.LockToMe,
                $"{fileName} mirrors a waypoint that never expires and is locked");
            return;
        }

        foreach (var waypoint in parse.Script!.Actions.Select(a => a.Waypoint).OfType<ScriptWaypoint>())
        {
            // A script has no idea when its marker stops being true, so it has
            // to give one an expiry.
            Assert.True(waypoint.Expires > TimeSpan.Zero, $"{fileName} places a waypoint that never expires");
        }
    }
}
