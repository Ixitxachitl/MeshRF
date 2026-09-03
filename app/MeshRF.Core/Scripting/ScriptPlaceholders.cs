// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>
/// The closed set of <c>{token}</c> substitutions allowed in script message
/// text. Deliberately a fixed table rather than an expression language: a
/// template engine would need a sandbox, a timeout, and an error surface of its
/// own, and every use these scripts have is "paste a value into a sentence".
/// </summary>
public static class ScriptPlaceholders
{
    /// <summary>Token name to the one-line description shown in the help
    /// window. Ordered for display, so the help table reads top-down.</summary>
    public static readonly IReadOnlyList<(string Token, string Description)> All =
    [
        ("msg.text",     "The full text of the triggering message."),
        ("args",         "Everything after the command word, for a command: trigger."),
        ("arg1",         "The first whitespace-separated argument (arg2, arg3, … follow)."),
        ("cap1",         "The first regex capture group from a text: trigger (cap2, … follow)."),
        ("from.id",      "Sender's node id, e.g. !a1b2c3d4."),
        ("from.short",   "Sender's short name."),
        ("from.long",    "Sender's long name."),
        ("from.lat",     "Sender's last reported latitude, from the node table. Empty if they have never sent a position."),
        ("from.lon",     "Sender's last reported longitude."),
        ("from.has_key", "\"true\" when the sender's public key is already on file, so a DM to them can be sealed. Gate a nodeinfo: request: with when: equals: false to ask only when it would tell you something."),
        ("channel",      "Channel the message arrived on, or PKC for a direct message."),
        ("geofence",     "Name of the fence that was crossed, for a geofence: trigger."),
        ("geofence.event", "\"entered\" or \"exited\", for a geofence: trigger."),
        ("snr",          "Signal-to-noise ratio of the triggering packet, in dB."),
        ("rssi",         "Received signal strength of the triggering packet, in dBm."),
        ("hops",         "Hops the triggering packet travelled."),
        ("time",         "Current local time, HH:mm."),
        ("date",         "Current local date."),
        ("my.id",        "This node's id."),
        ("my.short",     "This node's short name."),
        ("my.long",      "This node's long name."),
        ("my.lat",       "This node's home latitude. Empty if no home location is set."),
        ("my.lon",       "This node's home longitude. Empty if no home location is set."),
        ("node.battery", "This node's battery level, percent."),
        ("http.body",    "Result of the preceding http: action (the default save_as name)."),
        ("http.status",  "HTTP status code of the last http: action."),
        ("item.name",    "In a feed sync, any field of the record being mirrored — {item.data.acreage} works too."),
    ];

    private static readonly HashSet<string> s_known =
        new(All.Select(p => p.Token), StringComparer.Ordinal);

    /// <summary>Matches a placeholder token, filters and all — the same pattern
    /// expansion uses, so the editor checks exactly what the engine will read.
    /// Indexed families (arg1, cap2) are validated by <see cref="IsKnown"/>
    /// rather than enumerated here.</summary>
    private static readonly Regex s_token = ScriptTemplate.Token;

    /// <summary>True if <paramref name="token"/> names a real placeholder,
    /// including the numbered arg/cap families.</summary>
    public static bool IsKnown(string token)
    {
        if (s_known.Contains(token)) return true;

        // {http.<name>} where the name is whatever an http: action asked to be
        // saved as. Those names are chosen in the script, so any well-formed
        // one is accepted here; a name no action ever fills in expands to empty
        // rather than being flagged, since the two cannot be told apart without
        // running the sequence.
        if (token.StartsWith("http.", StringComparison.Ordinal))
        {
            var name = token["http.".Length..];
            return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        }

        // {item.<path>} reaches any field of a feed record, so the path is
        // whatever that feed happens to carry. Nothing here can tell a real
        // field from a typo, and guessing would flag correct scripts.
        if (token.StartsWith("item.", StringComparison.Ordinal))
            return token.Length > "item.".Length;

        foreach (var prefix in new[] { "arg", "cap" })
        {
            if (!token.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var digits = token[prefix.Length..];
            if (digits.Length > 0 && digits.All(char.IsAsciiDigit)) return true;
        }
        return false;
    }

    /// <summary>Every unrecognised token in <paramref name="text"/>. These are
    /// reported as warnings, not errors: a lone brace in message text is legal
    /// and shouldn't block saving, but a mistyped {from.shrt} would otherwise
    /// go out over the air as literal text.</summary>
    public static IEnumerable<string> UnknownTokens(string text)
    {
        foreach (Match m in s_token.Matches(text))
        {
            var name = m.Groups[1].Value;
            if (!IsKnown(name)) yield return name;
        }
    }

    /// <summary>Every unrecognised filter piped onto a placeholder in
    /// <paramref name="text"/>, reported the same way and for the same reason:
    /// a mistyped {snr|rnd:1} would otherwise go out as literal text.</summary>
    public static IEnumerable<string> UnknownFilters(string text)
    {
        foreach (Match m in s_token.Matches(text))
        {
            foreach (var (name, _) in ScriptTemplate.Filters(m.Groups[2].Value))
            {
                if (!ScriptFilters.IsKnown(name)) yield return name;
            }
        }
    }
}
