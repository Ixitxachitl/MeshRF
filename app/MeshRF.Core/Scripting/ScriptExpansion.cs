// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MeshRF.Scripting;

/// <summary>
/// Everything a script's text can be filled in from, for one run.
/// </summary>
/// <remarks>
/// Expansion happens at execution time rather than when the script matched,
/// because an <c>http:</c> action earlier in the sequence produces values that
/// later actions refer to. The engine therefore resolves routing up front —
/// who a message goes to, on which channel — and leaves the words until the
/// moment they are needed.
/// </remarks>
public sealed class ScriptExpansion
{
    private readonly Dictionary<string, string> _http = new(StringComparer.Ordinal);

    public ScriptExpansion(
        ScriptEvent evt,
        IReadOnlyList<string>? args = null,
        IReadOnlyList<string>? captures = null)
    {
        Event = evt;
        Args = args ?? Array.Empty<string>();
        Captures = captures ?? Array.Empty<string>();
    }

    public ScriptEvent Event { get; }
    public IReadOnlyList<string> Args { get; }
    public IReadOnlyList<string> Captures { get; }

    /// <summary>Records the outcome of an <c>http:</c> action under the name it
    /// asked to be saved as, so later actions can use {http.name}.</summary>
    public void SetHttpResult(string name, string value) => _http[name] = value;

    public IReadOnlyDictionary<string, string> HttpResults => _http;

    /// <summary>
    /// The feed record currently being mirrored, as raw JSON, so a name or
    /// description can read any of its fields with {item.some.path}.
    /// </summary>
    /// <remarks>
    /// Held as text and read by path rather than flattened up front: a record
    /// carries dozens of fields and a template usually wants two, so resolving
    /// on demand costs less than copying them all — and it means a template can
    /// reach a field nobody thought to list.
    /// </remarks>
    public string? Item { get; set; }

    /// <summary>Fills in placeholders. Used for log lines and anywhere the
    /// result is not going over the air.</summary>
    public string Expand(string template) =>
        ScriptTemplate.Expand(template, Event, Args, Captures, _http, item: Item);

    /// <summary>Fills in placeholders for something about to be transmitted, so
    /// the result is clamped to what a Meshtastic text payload carries.</summary>
    public string ExpandMessage(string template) =>
        ScriptTemplate.ClampToPayload(Expand(template));

    /// <summary>
    /// Fills in placeholders inside a URL, percent-encoding every substituted
    /// value.
    /// </summary>
    /// <remarks>
    /// The values come from radio messages written by other people. Without
    /// encoding, a message containing <c>&amp;</c>, <c>#</c> or a space would
    /// silently rewrite the request — appending parameters the script never
    /// asked for, or truncating the query. Encoding is not optional here, which
    /// is why URLs get their own expansion rather than sharing the message one.
    /// </remarks>
    public string ExpandUrl(string template) =>
        ScriptTemplate.Expand(template, Event, Args, Captures, _http, Uri.EscapeDataString);

    /// <summary>
    /// Fills in placeholders inside a JSON request body, escaping each value as
    /// a JSON string fragment so a quote or a backslash in a received message
    /// cannot break out of the string it was substituted into.
    /// </summary>
    public string ExpandJsonBody(string template) =>
        ScriptTemplate.Expand(template, Event, Args, Captures, _http, JsonStringFragment);

    /// <summary>Escapes a value for use between the quotes of a JSON string.
    /// Serializes then strips the surrounding quotes, so the escaping rules are
    /// System.Text.Json's rather than a hand-rolled approximation.</summary>
    private static string JsonStringFragment(string value)
    {
        var encoded = JsonSerializer.Serialize(value, s_jsonBodyOptions);
        return encoded.Length >= 2 ? encoded[1..^1] : string.Empty;
    }

    /// <summary>Relaxed encoder: the default escapes non-ASCII and HTML-unsafe
    /// characters, which would turn an accented place name in a message into a
    /// wall of \u sequences in the request body.</summary>
    private static readonly JsonSerializerOptions s_jsonBodyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
