// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

public enum ScriptHttpMethod
{
    Get,
    Post,
    Put,
}

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

    /// <summary>Name of a stored credential to authenticate with, or empty.
    /// The value itself never appears in a script file — see
    /// <see cref="ScriptCredential"/>.</summary>
    public string Credential { get; init; } = string.Empty;

    /// <summary>Dotted path into a JSON response, e.g.
    /// <c>current.temp_c</c> or <c>results[0].name</c>. Empty means use the
    /// whole response body.</summary>
    public string JsonPath { get; init; } = string.Empty;

    /// <summary>Placeholder name the result is stored under, so
    /// <c>save_as: temp</c> makes it available as <c>{http.temp}</c>.</summary>
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

    /// <summary>How the credential attaches, for the management dialog's list.
    /// Never includes the value.</summary>
    public string Describe() => Placement switch
    {
        ScriptCredentialPlacement.Bearer => "Authorization: Bearer …",
        ScriptCredentialPlacement.Header => $"{Parameter}: …",
        _ => $"?{Parameter}=…",
    };
}

/// <summary>Looks up a credential by the name a script used.</summary>
public interface IScriptCredentialSource
{
    ScriptCredential? Find(string name);
}
