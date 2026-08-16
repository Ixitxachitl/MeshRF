// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>The outcome of one <c>http:</c> action.</summary>
/// <param name="Ok">Whether the values were obtained.</param>
/// <param name="Values">Extracted, sanitised values by placeholder name, ready
/// to be broadcast.</param>
/// <param name="Status">HTTP status code, or 0 if the request never completed.</param>
/// <param name="Error">Why it failed, in plain language, for the log.</param>
public readonly record struct ScriptHttpResult(
    bool Ok, IReadOnlyDictionary<string, string> Values, int Status, string Error)
{
    public static ScriptHttpResult Failed(int status, string error) =>
        new(false, new Dictionary<string, string>(), status, error);
}

/// <summary>
/// Performs a script's REST call: builds the request, attaches the named
/// credential, and reduces the response to one line fit to put on the air.
/// </summary>
/// <remarks>
/// <para>The response comes from a third party and is about to be transmitted
/// on a shared channel, so it is treated as hostile: the read is capped, the
/// text is stripped of control characters and collapsed onto one line, and the
/// caller clamps it to the payload size. A body that arrives as five kilobytes
/// of pretty-printed JSON must not become five kilobytes of airtime.</para>
/// <para>Only http and https are accepted. The credential is attached last and
/// never appears in any string this class returns, so it cannot reach the log
/// or a message.</para>
/// </remarks>
public sealed class ScriptHttpClient : IDisposable
{
    /// <summary>
    /// Ceiling on how much of a response is read.
    /// </summary>
    /// <remarks>
    /// Sized for a feed rather than for a single reading: a list of every
    /// active incident in a country runs to about a megabyte, where a script
    /// pulling one temperature needs a few hundred bytes. Reaching this is
    /// reported as itself rather than left to surface as a JSON parse failure
    /// halfway through a truncated document.
    /// </remarks>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;

    public ScriptHttpClient(HttpClient? client = null)
    {
        // Per-request timeouts come from the script, so the client's own is
        // left open and cancellation does the work. Compression is on because
        // responses are read over someone's metered connection as often as not,
        // and the cap on how much is read applies after decoding either way.
        _http = client ?? new HttpClient(
            new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MeshRF-scripts/1.0");
    }

    public IScriptCredentialSource? Credentials { get; set; }

    public async Task<ScriptHttpResult> SendAsync(
        ScriptHttpRequest request, ScriptExpansion expansion, CancellationToken cancellation = default)
    {
        var url = expansion.ExpandUrl(request.Url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ScriptHttpResult.Failed(0,
                $"\"{url}\" is not an http or https address once its placeholders were filled in");
        }

        var resolved = new List<ScriptCredential>(request.CredentialNames.Count);
        foreach (var name in request.CredentialNames)
        {
            var credential = Credentials?.Find(name);
            if (credential is null)
            {
                return ScriptHttpResult.Failed(0,
                    $"no credential named \"{name}\" — add it under Credentials in the Scripts window");
            }
            resolved.Add(credential);
            // Query credentials go on before the request is built, since the
            // URI is fixed once the message exists.
            if (credential.Placement == ScriptCredentialPlacement.Query)
            {
                uri = AppendQuery(uri, credential.Parameter, credential.Value);
                if (credential.IsPair) uri = AppendQuery(uri, credential.Parameter2, credential.Value2);
            }
        }

        using var message = new HttpRequestMessage(MethodOf(request.Method), uri)
        {
            // Prefer HTTP/2, falling back to 1.1 when a server does not offer
            // it. Some edge filters refuse a request that claims to be a modern
            // client yet speaks 1.1 with none of the headers a browser sends,
            // answering 406 with nothing to indicate why — .NET defaults to 1.1
            // where curl and most libraries negotiate upward.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        // Before the credentials, so a script cannot shadow the header a
        // credential is about to set. A header named here also replaces the
        // client-wide default rather than adding to it, since HttpClient only
        // applies a default header the message does not already carry — which
        // is what lets a script choose its own User-Agent.
        foreach (var header in request.Headers)
            message.Headers.TryAddWithoutValidation(header.Name, expansion.Expand(header.Value));

        foreach (var credential in resolved)
        {
            switch (credential.Placement)
            {
                case ScriptCredentialPlacement.Bearer:
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
                    break;
                case ScriptCredentialPlacement.Header:
                    message.Headers.TryAddWithoutValidation(credential.Parameter, credential.Value);
                    if (credential.IsPair)
                        message.Headers.TryAddWithoutValidation(credential.Parameter2, credential.Value2);
                    break;
            }
        }

        if (request.Body.Length > 0)
        {
            // Placeholders inside a JSON body are escaped as JSON strings, so a
            // quote in a received message cannot break out of the field it was
            // substituted into.
            var body = request.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? expansion.ExpandJsonBody(request.Body)
                : expansion.Expand(request.Body);
            message.Content = new StringContent(body, Encoding.UTF8, request.ContentType);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(request.Timeout);

        try
        {
            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            int status = (int)response.StatusCode;
            var (text, truncated) = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(false);

            if (truncated)
            {
                return ScriptHttpResult.Failed(status,
                    $"the response is larger than {MaxResponseBytes / (1024 * 1024)} MB and was cut short, " +
                    "so it could not be read — narrow the request if the API allows it");
            }

            if (!response.IsSuccessStatusCode)
            {
                // A short excerpt of the body: APIs put the actual reason there,
                // and "400" on its own is not something a user can act on.
                var excerpt = Sanitize(text);
                if (excerpt.Length > 120) excerpt = excerpt[..120] + "…";
                return ScriptHttpResult.Failed(status,
                    $"the server answered {status} {response.ReasonPhrase}" +
                    (excerpt.Length > 0 ? $" — {excerpt}" : string.Empty));
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            if (request.Extractions.Count == 0)
            {
                values[request.SaveAs] = Sanitize(text);
                return new ScriptHttpResult(true, values, status, string.Empty);
            }

            foreach (var extraction in request.Extractions)
            {
                var value = JsonValuePath.Read(text, extraction.JsonPath, out var error);
                if (value is null)
                {
                    // All or nothing unless the script said absence was
                    // expected: one that reads a latitude and then fails to
                    // read the matching longitude must not go on to place a
                    // waypoint at half a position.
                    if (!request.Optional) return ScriptHttpResult.Failed(status, $"{extraction.SaveAs}: {error}");
                    values[extraction.SaveAs] = string.Empty;
                    continue;
                }
                values[extraction.SaveAs] = Sanitize(value);
            }
            return new ScriptHttpResult(true, values, status, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return ScriptHttpResult.Failed(0,
                $"the request took longer than {request.Timeout.TotalSeconds:0.#}s and was given up on");
        }
        catch (HttpRequestException ex)
        {
            return ScriptHttpResult.Failed(0, $"the request failed — {ex.Message}");
        }
    }

    private static HttpMethod MethodOf(ScriptHttpMethod method) => method switch
    {
        ScriptHttpMethod.Post => HttpMethod.Post,
        ScriptHttpMethod.Put => HttpMethod.Put,
        _ => HttpMethod.Get,
    };

    /// <summary>Reads at most <see cref="MaxResponseBytes"/>, whatever the
    /// server claims in Content-Length. Reads one byte past the cap so a
    /// response that reached it can be told from one that merely filled it.</summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        bool truncated = total > MaxResponseBytes;
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(total, MaxResponseBytes)), truncated);
    }

    private static readonly Regex s_whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reduces a response to one line of printable text. Control characters go,
    /// runs of whitespace collapse to single spaces, and the result is trimmed
    /// — a pretty-printed JSON body would otherwise be transmitted with all its
    /// newlines and indentation intact.
    /// </summary>
    internal static string Sanitize(string text)
    {
        if (text.Length == 0) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Tabs and newlines survive as spaces for the collapse below; every
            // other control character is dropped outright.
            if (ch is '\t' or '\n' or '\r') sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return s_whitespace.Replace(sb.ToString(), " ").Trim();
    }

    private static Uri AppendQuery(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var pair = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        builder.Query = builder.Query.Length > 1 ? $"{builder.Query[1..]}&{pair}" : pair;
        return builder.Uri;
    }

    public void Dispose() => _http.Dispose();
}
