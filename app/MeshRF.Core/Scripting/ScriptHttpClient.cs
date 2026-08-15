// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>The outcome of one <c>http:</c> action.</summary>
/// <param name="Ok">Whether a value was obtained.</param>
/// <param name="Value">The extracted, sanitised value, ready to be broadcast.</param>
/// <param name="Status">HTTP status code, or 0 if the request never completed.</param>
/// <param name="Error">Why it failed, in plain language, for the log.</param>
public readonly record struct ScriptHttpResult(bool Ok, string Value, int Status, string Error);

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
    /// <summary>Ceiling on how much of a response is read. Generous for an API
    /// answer, small enough that a misaimed URL returning a web page cannot
    /// tie up memory.</summary>
    private const int MaxResponseBytes = 64 * 1024;

    private readonly HttpClient _http;

    public ScriptHttpClient(HttpClient? client = null)
    {
        // Per-request timeouts come from the script, so the client's own is
        // left open and cancellation does the work.
        _http = client ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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
            return new ScriptHttpResult(false, string.Empty, 0,
                $"\"{url}\" is not an http or https address once its placeholders were filled in");
        }

        ScriptCredential? credential = null;
        if (request.Credential.Length > 0)
        {
            credential = Credentials?.Find(request.Credential);
            if (credential is null)
            {
                return new ScriptHttpResult(false, string.Empty, 0,
                    $"no credential named \"{request.Credential}\" — add it under Credentials in the Scripts window");
            }
            if (credential.Placement == ScriptCredentialPlacement.Query)
                uri = AppendQuery(uri, credential.Parameter, credential.Value);
        }

        using var message = new HttpRequestMessage(MethodOf(request.Method), uri);

        if (credential is not null)
        {
            switch (credential.Placement)
            {
                case ScriptCredentialPlacement.Bearer:
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
                    break;
                case ScriptCredentialPlacement.Header:
                    message.Headers.TryAddWithoutValidation(credential.Parameter, credential.Value);
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
            var text = await ReadCappedAsync(response, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A short excerpt of the body: APIs put the actual reason there,
                // and "400" on its own is not something a user can act on.
                var excerpt = Sanitize(text);
                if (excerpt.Length > 120) excerpt = excerpt[..120] + "…";
                return new ScriptHttpResult(false, string.Empty, status,
                    $"the server answered {status} {response.ReasonPhrase}" +
                    (excerpt.Length > 0 ? $" — {excerpt}" : string.Empty));
            }

            if (request.JsonPath.Length == 0)
                return new ScriptHttpResult(true, Sanitize(text), status, string.Empty);

            var value = JsonValuePath.Read(text, request.JsonPath, out var error);
            return value is null
                ? new ScriptHttpResult(false, string.Empty, status, error)
                : new ScriptHttpResult(true, Sanitize(value), status, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new ScriptHttpResult(false, string.Empty, 0,
                $"the request took longer than {request.Timeout.TotalSeconds:0.#}s and was given up on");
        }
        catch (HttpRequestException ex)
        {
            return new ScriptHttpResult(false, string.Empty, 0, $"the request failed — {ex.Message}");
        }
    }

    private static HttpMethod MethodOf(ScriptHttpMethod method) => method switch
    {
        ScriptHttpMethod.Post => HttpMethod.Post,
        ScriptHttpMethod.Put => HttpMethod.Put,
        _ => HttpMethod.Get,
    };

    /// <summary>Reads at most <see cref="MaxResponseBytes"/>, whatever the
    /// server claims in Content-Length.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
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
