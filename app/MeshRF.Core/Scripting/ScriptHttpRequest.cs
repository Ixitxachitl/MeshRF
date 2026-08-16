// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

public enum ScriptHttpMethod
{
    Get,
    Post,
    Put,
}

/// <summary>One value pulled out of a JSON response.</summary>
/// <param name="SaveAs">Placeholder name, so <c>lat</c> becomes {http.lat}.</param>
/// <param name="JsonPath">Dotted path, e.g. <c>report[0].loc.lat</c>.</param>
public readonly record struct ScriptHttpExtraction(string SaveAs, string JsonPath);

/// <summary>
/// An <c>http:</c> action: call a REST endpoint and keep part of the answer for
/// a later <c>reply:</c> or <c>send:</c> to say.
/// </summary>
/// <remarks>
/// Fetching and broadcasting are deliberately two steps rather than one. A
/// script often wants to shape the answer ("It's {http.temp}°C in {arg1}"),
/// call more than one endpoint, or send the result somewhere other than back to
/// the asker — and a fused "reply with this URL" action can do none of those.
/// </remarks>
public sealed class ScriptHttpRequest
{
    /// <summary>URL template. Placeholders in it are percent-encoded when
    /// expanded; see <see cref="ScriptExpansion.ExpandUrl"/>.</summary>
    public string Url { get; init; } = string.Empty;

    public ScriptHttpMethod Method { get; init; } = ScriptHttpMethod.Get;

    /// <summary>
    /// Stored credentials to authenticate with, by name. The values themselves
    /// never appear in a script file — see <see cref="ScriptCredential"/>.
    /// </summary>
    /// <remarks>
    /// A list because not every API authenticates with a single secret: an id
    /// and secret pair passed as two query parameters is common, and splitting
    /// them across two entries keeps both out of the script and lets each say
    /// where it attaches.
    /// </remarks>
    public IReadOnlyList<string> CredentialNames { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Values to pull out of a JSON response. Empty means store the whole body
    /// under <see cref="SaveAs"/>.
    /// </summary>
    /// <remarks>
    /// A list rather than one path because the interesting responses carry
    /// several values that belong together — a strike's latitude and longitude
    /// are useless apart, and fetching them separately would mean two requests
    /// against a moving target.
    /// </remarks>
    public IReadOnlyList<ScriptHttpExtraction> Extractions { get; init; } =
        Array.Empty<ScriptHttpExtraction>();

    /// <summary>
    /// Treat a path that is not in the response as empty instead of failing.
    /// </summary>
    /// <remarks>
    /// Off by default so a mistyped path is reported rather than silently
    /// yielding nothing. Turned on when absence is a normal answer — an API
    /// asked for the nearest lightning strike returns an empty list most of the
    /// time, and that is the quiet case, not a fault. Pair it with a
    /// <c>require:</c> on the value to decide what to do about it.
    /// </remarks>
    public bool Optional { get; init; }

    /// <summary>Placeholder name the whole body is stored under when no
    /// extraction is given, so <c>save_as: temp</c> makes it
    /// <c>{http.temp}</c>.</summary>
    public string SaveAs { get; init; } = "body";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Request body template for POST/PUT. Placeholders are escaped
    /// according to <see cref="ContentType"/>.</summary>
    public string Body { get; init; } = string.Empty;

    public string ContentType { get; init; } = "application/json";

    /// <summary>Whether this method changes state on the far end. Read-only
    /// requests are safe to make during a dry run; write ones are not.</summary>
    public bool IsWrite => Method is ScriptHttpMethod.Post or ScriptHttpMethod.Put;
}

/// <summary>Where a credential's value is attached to the request.</summary>
public enum ScriptCredentialPlacement
{
    /// <summary><c>Authorization: Bearer &lt;value&gt;</c>.</summary>
    Bearer,
    /// <summary>A named request header, e.g. <c>X-API-Key</c>.</summary>
    Header,
    /// <summary>A named query-string parameter, e.g. <c>appid</c>.</summary>
    Query,
}

/// <summary>
/// A named API key.
/// </summary>
/// <remarks>
/// <para>Kept out of the script files on purpose. Scripts are plain text that
/// people copy between machines and paste into chat to ask for help; a key
/// living in one would leak the first time that happened. Scripts name a
/// credential, and the value is stored once, protected at rest alongside the
/// MQTT password and the node's private key.</para>
/// <para>The value is never exposed as a placeholder and never written to the
/// log — a script cannot read its own key, so it cannot broadcast it.</para>
/// </remarks>
public sealed class ScriptCredential
{
    /// <summary>The name scripts refer to, e.g. <c>credential: weather</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public ScriptCredentialPlacement Placement { get; set; } = ScriptCredentialPlacement.Bearer;

    /// <summary>Header or query-parameter name. Unused for
    /// <see cref="ScriptCredentialPlacement.Bearer"/>.</summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>The secret, in memory. Routed through
    /// <see cref="ValueOnDisk"/> so the stored copy is protected.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Value { get; set; } = string.Empty;

    /// <summary>On-disk form of <see cref="Value"/>, DPAPI-protected on Windows
    /// and AES-GCM under a machine-bound key elsewhere.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("Value")]
    public string ValueOnDisk { get; set; } = string.Empty;

    /// <summary>
    /// Second parameter name, for an API that authenticates with a pair.
    /// Empty when the credential is a single secret.
    /// </summary>
    /// <remarks>
    /// A client id and client secret are one credential in every sense that
    /// matters — issued together, rotated together, useless apart — so they are
    /// one entry rather than two that have to be kept in step by hand. Both
    /// halves attach the same way, since an API splitting its credential across
    /// a header and a query string is vanishingly rare.
    /// </remarks>
    public string Parameter2 { get; set; } = string.Empty;

    /// <summary>Second secret, in memory. Routed through
    /// <see cref="Value2OnDisk"/> so the stored copy is protected.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Value2 { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("Value2")]
    public string Value2OnDisk { get; set; } = string.Empty;

    /// <summary>Whether this credential carries a second half.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPair => Parameter2.Trim().Length > 0;

    /// <summary>How the credential attaches, for the management dialog's list.
    /// Never includes either value.</summary>
    public string Describe() => Placement switch
    {
        ScriptCredentialPlacement.Bearer => "Authorization: Bearer …",
        ScriptCredentialPlacement.Header => IsPair ? $"{Parameter}: …  {Parameter2}: …" : $"{Parameter}: …",
        _ => IsPair ? $"?{Parameter}=…&{Parameter2}=…" : $"?{Parameter}=…",
    };
}

/// <summary>Looks up a credential by the name a script used.</summary>
public interface IScriptCredentialSource
{
    ScriptCredential? Find(string name);
}
