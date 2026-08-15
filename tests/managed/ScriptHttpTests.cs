// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net;
using System.Net.Http;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The http: action: parsing and validation, and the request/response handling
/// against a stub transport.
/// </summary>
public class ScriptHttpTests
{
    private static readonly ScriptSelf Self = new(0x11111111, "ME", "My Node", 101);
    private static readonly DateTimeOffset Noon = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static ScriptEvent Event(string text = "!wx london") => new()
    {
        Kind = ScriptEventKind.Text,
        Text = text,
        FromNode = 0xa1b2c3d4,
        FromShort = "PEER",
        Channel = "LongFast",
        IsDirect = true,
        SnrDb = 7,
        PacketId = 0xdeadbeef,
        Self = Self,
        At = Noon,
    };

    private static ScriptExpansion Expansion(string text = "!wx london") =>
        new(Event(text), ScriptTemplate.SplitArguments(text));

    // ----- parsing ------------------------------------------------------------

    private static ScriptParseResult Parse(string httpBlock) =>
        ScriptParser.Parse("trigger:\n  - command: wx\naction:\n" + httpBlock);

    [Fact]
    public void A_Complete_Http_Action_Parses()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: wx
            action:
              - http:
                  url: "https://api.example.com/v1/weather?q={args}"
                  method: GET
                  credential: weather
                  json: current.temp_c
                  save_as: temp
                  timeout: 8s
              - reply: "{arg1}: {http.temp}°C"
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Empty(result.Problems);

        var http = result.Script!.Actions[0].Http!;
        Assert.Equal(ScriptHttpMethod.Get, http.Method);
        Assert.Equal("weather", http.Credential);
        Assert.Equal("current.temp_c", http.JsonPath);
        Assert.Equal("temp", http.SaveAs);
        Assert.Equal(TimeSpan.FromSeconds(8), http.Timeout);
    }

    [Fact]
    public void Http_Does_Not_Count_As_Airtime()
    {
        var result = ScriptParser.Parse(
            "trigger:\n  - command: wx\naction:\n  - http:\n      url: \"https://x.test/\"\n");

        Assert.True(result.IsValid);
        // A fetch makes a network request but puts nothing on the channel, so
        // it must not be charged against the global transmission budget.
        var engine = new ScriptEngine();
        engine.Load(
            [new ScriptFile("a.yaml", "a.yaml", result.Script is null ? "" : "x", Enabled: true, result)],
            Noon);
        var run = Assert.Single(engine.Evaluate(Event()));
        Assert.False(run.Actions[0].Transmits);
        Assert.False(run.Transmits);
    }

    [Fact]
    public void A_Non_Http_Scheme_Is_Rejected()
    {
        // A placeholder may fill in the host or path, but never the scheme.
        var result = Parse("  - http:\n      url: \"file:///etc/passwd\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("has to start with https:// or http://", result.FirstError!.Value.Message);
    }

    [Fact]
    public void Plain_Http_Is_Allowed_But_Warned_About()
    {
        var result = Parse("  - http:\n      url: \"http://192.168.1.10/api\"\n");

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Problems);
        Assert.Equal(ScriptProblemSeverity.Warning, warning.Severity);
        Assert.Contains("unencrypted", warning.Message);
    }

    [Fact]
    public void A_Body_On_A_Get_Is_Rejected()
    {
        var result = Parse("  - http:\n      url: \"https://x.test/\"\n      body: \"{}\"\n");

        Assert.False(result.IsValid);
        Assert.Contains("only applies to POST or PUT", result.FirstError!.Value.Message);
    }

    [Fact]
    public void An_Unknown_Method_Is_Rejected()
    {
        var result = Parse("  - http:\n      url: \"https://x.test/\"\n      method: DELETE\n");

        Assert.False(result.IsValid);
        Assert.Contains("GET, POST or PUT", result.FirstError!.Value.Message);
    }

    [Fact]
    public void Save_As_Status_Is_Rejected_Because_It_Is_Taken()
    {
        var result = Parse("  - http:\n      url: \"https://x.test/\"\n      save_as: status\n");

        Assert.False(result.IsValid);
        Assert.Contains("is taken", result.FirstError!.Value.Message);
    }

    [Fact]
    public void An_Over_Long_Timeout_Is_Rejected()
    {
        var result = Parse("  - http:\n      url: \"https://x.test/\"\n      timeout: 5m\n");

        Assert.False(result.IsValid);
        Assert.Contains("cannot be longer than 30s", result.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Malformed_Json_Path_Is_Rejected()
    {
        Assert.False(Parse("  - http:\n      url: \"https://x.test/\"\n      json: \"a..b\"\n").IsValid);
        Assert.False(Parse("  - http:\n      url: \"https://x.test/\"\n      json: \"a[x]\"\n").IsValid);
        Assert.False(Parse("  - http:\n      url: \"https://x.test/\"\n      json: \"a[0\"\n").IsValid);
        Assert.True(Parse("  - http:\n      url: \"https://x.test/\"\n      json: \"a.b[0].c\"\n").IsValid);
    }

    [Fact]
    public void An_Unknown_Http_Option_Suggests_The_Right_One()
    {
        var result = Parse("  - http:\n      url: \"https://x.test/\"\n      methd: GET\n");

        Assert.False(result.IsValid);
        Assert.Contains("did you mean 'method'?", result.FirstError!.Value.Message);
    }

    [Fact]
    public void Http_Placeholders_Do_Not_Warn()
    {
        // {http.<name>} names come from save_as, so any well-formed one is
        // accepted rather than flagged as a typo.
        var result = ScriptParser.Parse(
            "trigger:\n  - command: wx\naction:\n  - http:\n      url: \"https://x.test/\"\n      save_as: temp\n" +
            "  - reply: \"{http.temp} at {http.status}\"\n");

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
    }

    // ----- JSON extraction ----------------------------------------------------

    [Theory]
    [InlineData("current.temp_c", "14.5")]
    [InlineData("current.text", "Cloudy")]
    [InlineData("current.ok", "true")]
    [InlineData("days[0].name", "Mon")]
    [InlineData("days[1].name", "Tue")]
    public void Json_Paths_Read_Values(string path, string expected)
    {
        const string json =
            """
            {"current":{"temp_c":14.5,"text":"Cloudy","ok":true},
             "days":[{"name":"Mon"},{"name":"Tue"}]}
            """;

        Assert.Equal(expected, JsonValuePath.Read(json, path, out _));
    }

    [Theory]
    [InlineData("current.missing", "no \"missing\"")]
    [InlineData("days[9].name", "does not exist")]
    [InlineData("current[0]", "not a list")]
    [InlineData("days.name", "not an object")]
    public void A_Path_That_Does_Not_Fit_Says_Why(string path, string expectedFragment)
    {
        const string json = """{"current":{"temp_c":1},"days":[{"name":"Mon"}]}""";

        Assert.Null(JsonValuePath.Read(json, path, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void A_Non_Json_Response_Says_So_Rather_Than_Throwing()
    {
        Assert.Null(JsonValuePath.Read("<html>nope</html>", "a.b", out var error));
        Assert.Contains("not valid JSON", error);
    }

    // ----- URL building -------------------------------------------------------

    [Fact]
    public void Url_Placeholders_Are_Percent_Encoded()
    {
        // Without this, a message containing & or a space silently rewrites the
        // request — appending parameters the script never asked for.
        var expansion = Expansion("!wx san francisco&admin=1");
        var url = expansion.ExpandUrl("https://api.test/v1?q={args}");

        Assert.Equal("https://api.test/v1?q=san%20francisco%26admin%3D1", url);
    }

    [Fact]
    public void Json_Body_Placeholders_Are_Escaped()
    {
        var expansion = Expansion("!wx say \"hi\"\\there");
        var body = expansion.ExpandJsonBody("""{"q": "{args}"}""");

        // Still parses as JSON, which it would not if the quote had escaped the
        // field it was substituted into.
        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("say \"hi\"\\there", document.RootElement.GetProperty("q").GetString());
    }

    [Fact]
    public void Message_Placeholders_Are_Not_Escaped()
    {
        var expansion = Expansion("!wx a&b c");
        Assert.Equal("a&b c", expansion.ExpandMessage("{args}"));
    }

    [Fact]
    public void An_Unfilled_Http_Placeholder_Expands_To_Nothing()
    {
        // Reaching one means the fetch was skipped; broadcasting the literal
        // "{http.temp}" would be worse than broadcasting nothing.
        Assert.Equal("it is  degrees", Expansion().ExpandMessage("it is {http.temp} degrees"));
    }

    // ----- requests -----------------------------------------------------------

    /// <summary>Answers every request from a canned response, and records what
    /// was asked for.</summary>
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        /// <summary>Captured here rather than read from <see cref="Last"/>
        /// afterwards: the client disposes the request message once it is sent,
        /// which correctly disposes its content too.</summary>
        public string? SentBody { get; private set; }
        public string? SentContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            if (request.Content is { } content)
            {
                SentBody = await content.ReadAsStringAsync(cancellationToken);
                SentContentType = content.Headers.ContentType?.MediaType;
            }
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static (ScriptHttpClient Client, StubHandler Handler) Stub(
        string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(status, body);
        return (new ScriptHttpClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task A_Json_Value_Is_Extracted_And_The_Url_Is_Built()
    {
        var (client, handler) = Stub("""{"current":{"temp_c":14.5}}""");
        using var _ = client;

        var result = await client.SendAsync(
            new ScriptHttpRequest
            {
                Url = "https://api.test/v1?q={args}",
                JsonPath = "current.temp_c",
                SaveAs = "temp",
            },
            Expansion("!wx london"));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("14.5", result.Value);
        Assert.Equal(200, result.Status);
        Assert.Equal("https://api.test/v1?q=london", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task The_Whole_Body_Is_Used_When_No_Path_Is_Given()
    {
        var (client, _) = Stub("plain text answer");
        using var __ = client;

        var result = await client.SendAsync(
            new ScriptHttpRequest { Url = "https://api.test/" }, Expansion());

        Assert.True(result.Ok);
        Assert.Equal("plain text answer", result.Value);
    }

    [Fact]
    public async Task A_Response_Is_Flattened_Onto_One_Line()
    {
        // Pretty-printed JSON would otherwise be transmitted with all its
        // newlines and indentation intact.
        var (client, _) = Stub("line one\n\n   line   two\t\r\nline three");
        using var __ = client;

        var result = await client.SendAsync(
            new ScriptHttpRequest { Url = "https://api.test/" }, Expansion());

        Assert.Equal("line one line two line three", result.Value);
    }

    [Fact]
    public async Task An_Error_Status_Fails_With_The_Reason()
    {
        var (client, _) = Stub("""{"error":"city not found"}""", HttpStatusCode.NotFound);
        using var __ = client;

        var result = await client.SendAsync(
            new ScriptHttpRequest { Url = "https://api.test/", JsonPath = "temp" }, Expansion());

        Assert.False(result.Ok);
        Assert.Equal(404, result.Status);
        // The body carries the actual reason; "404" alone is not actionable.
        Assert.Contains("city not found", result.Error);
    }

    [Fact]
    public async Task A_Bearer_Credential_Is_Attached_And_Never_Returned()
    {
        var (client, handler) = Stub("ok");
        using var __ = client;
        client.Credentials = new Credentials(new ScriptCredential
        {
            Name = "k", Placement = ScriptCredentialPlacement.Bearer, Value = "s3cret",
        });

        var result = await client.SendAsync(
            new ScriptHttpRequest { Url = "https://api.test/", Credential = "k" }, Expansion());

        Assert.Equal("Bearer", handler.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("s3cret", handler.Last.Headers.Authorization.Parameter);
        // Nothing the caller can log or broadcast contains the key.
        Assert.DoesNotContain("s3cret", result.Value);
        Assert.DoesNotContain("s3cret", result.Error);
    }

    [Fact]
    public async Task A_Header_Credential_Is_Attached()
    {
        var (client, handler) = Stub("ok");
        using var __ = client;
        client.Credentials = new Credentials(new ScriptCredential
        {
            Name = "k", Placement = ScriptCredentialPlacement.Header, Parameter = "X-API-Key", Value = "abc123",
        });

        await client.SendAsync(new ScriptHttpRequest { Url = "https://api.test/", Credential = "k" }, Expansion());

        Assert.Equal("abc123", handler.Last!.Headers.GetValues("X-API-Key").Single());
    }

    [Fact]
    public async Task A_Query_Credential_Is_Appended_Alongside_Existing_Parameters()
    {
        var (client, handler) = Stub("ok");
        using var __ = client;
        client.Credentials = new Credentials(new ScriptCredential
        {
            Name = "k", Placement = ScriptCredentialPlacement.Query, Parameter = "appid", Value = "abc123",
        });

        await client.SendAsync(
            new ScriptHttpRequest { Url = "https://api.test/v1?q={args}", Credential = "k" },
            Expansion("!wx london"));

        Assert.Equal("https://api.test/v1?q=london&appid=abc123", handler.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_Missing_Credential_Fails_With_A_Usable_Reason()
    {
        var (client, _) = Stub("ok");
        using var __ = client;
        client.Credentials = new Credentials();

        var result = await client.SendAsync(
            new ScriptHttpRequest { Url = "https://api.test/", Credential = "nope" }, Expansion());

        Assert.False(result.Ok);
        Assert.Contains("no credential named \"nope\"", result.Error);
    }

    [Fact]
    public async Task A_Url_Whose_Placeholders_Break_It_Fails_Before_Any_Request()
    {
        var (client, handler) = Stub("ok");
        using var __ = client;

        // {args} is empty, leaving "https://" with no host.
        var result = await client.SendAsync(
            new ScriptHttpRequest { Url = "https://{args}" }, Expansion("!wx"));

        Assert.False(result.Ok);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task A_Post_Body_Is_Sent_With_Its_Content_Type()
    {
        var (client, handler) = Stub("ok");
        using var __ = client;

        await client.SendAsync(
            new ScriptHttpRequest
            {
                Url = "https://api.test/",
                Method = ScriptHttpMethod.Post,
                Body = """{"q": "{args}"}""",
            },
            Expansion("!wx london"));

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("""{"q": "london"}""", handler.SentBody);
        Assert.Equal("application/json", handler.SentContentType);
    }

    [Fact]
    public void Write_Methods_Are_Identified_For_The_Dry_Run_Rule()
    {
        // Dry run performs GET (a read changes nothing) but skips writes.
        Assert.False(new ScriptHttpRequest { Method = ScriptHttpMethod.Get }.IsWrite);
        Assert.True(new ScriptHttpRequest { Method = ScriptHttpMethod.Post }.IsWrite);
        Assert.True(new ScriptHttpRequest { Method = ScriptHttpMethod.Put }.IsWrite);
    }

    private sealed class Credentials(params ScriptCredential[] credentials) : IScriptCredentialSource
    {
        public ScriptCredential? Find(string name) =>
            credentials.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
