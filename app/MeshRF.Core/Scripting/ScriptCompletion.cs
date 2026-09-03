// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>One value the editor can offer to insert.</summary>
/// <param name="Label">What the user sees and what a typed prefix is matched
/// against — the bare value, without the quotes YAML may need around it.</param>
/// <param name="Insert">What actually goes into the file. Node ids carry their
/// quotes: a bare <c>!a1b2c3d4</c> opens a YAML tag and the file stops
/// parsing.</param>
/// <param name="Note">What it is, shown beside the value in the list.</param>
/// <param name="NoteInFile">Whether the note is worth keeping in the script as
/// a comment after the value. True for a node id, which is eight hex digits
/// saying nothing about whose node it is; false for a channel name, where a
/// comment repeating what the name already says is only noise.</param>
public readonly record struct ScriptSuggestion(
    string Label, string Insert, string Note = "", bool NoteInFile = false)
{
    public ScriptSuggestion(string label, string note = "") : this(label, label, note) { }
}

/// <summary>What this node knows, for the editor to suggest from.</summary>
/// <param name="Channels">Configured channel names, in tab order.</param>
/// <param name="Nodes">Known nodes, already formatted as id + name.</param>
/// <param name="Credentials">Names of the stored API credentials.</param>
/// <param name="Geofences">Names of the waypoints that carry a fence, which are
/// the only ones a <c>geofence:</c> trigger could ever fire on.</param>
public sealed record ScriptCompletionSource(
    IReadOnlyList<ScriptSuggestion> Channels,
    IReadOnlyList<ScriptSuggestion> Nodes,
    IReadOnlyList<string> Credentials,
    IReadOnlyList<ScriptSuggestion> Geofences)
{
    public static readonly ScriptCompletionSource Empty = new([], [], [], []);
}

/// <summary>An offer: what to show, and what part of the file it replaces.</summary>
/// <param name="Suggestions">Matches for what has been typed so far, best
/// first.</param>
/// <param name="Start">Where the replaced token begins.</param>
/// <param name="Length">How much of it the caret has already consumed.</param>
/// <param name="AllowComment">Whether the rest of the line is empty, so a
/// suggestion's note can be written in after the value without landing in the
/// middle of something.</param>
public sealed record ScriptCompletionResult(
    IReadOnlyList<ScriptSuggestion> Suggestions, int Start, int Length, bool AllowComment);

/// <summary>
/// Works out what the script editor can usefully offer at the caret.
/// </summary>
/// <remarks>
/// <para>Lives here rather than in the window for the same reason the engine
/// does: what to suggest is a question about the vocabulary, answerable from a
/// string and a caret index, and testable without a text box. The window's job
/// is to show the list and splice the chosen value in.</para>
/// <para>Only value positions are offered, and only the keys that name
/// something this node already knows — a channel, a node, a credential, a
/// fence on the map. The keys themselves are in the Help window, and a
/// completion list that tried to cover the whole vocabulary would be in the
/// way while typing rather than useful.</para>
/// </remarks>
public static class ScriptCompletion
{
    /// <summary>
    /// The tail of a line that has a key and is partway through its value.
    /// </summary>
    /// <remarks>
    /// <c>[^:#]</c> in the value is what keeps this off a <c>url:</c> — a
    /// value carrying a colon is not one of the keys below, and a value
    /// carrying a <c>#</c> has passed into a comment.
    /// </remarks>
    private static readonly Regex ValueLine = new(
        @"^\s*(?:-\s+)?(?<key>[a-z_]+)\s*:(?<value>[^:#]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Separators a value may hold before the token being typed, so
    /// <c>channel: [Test, Wea</c> completes "Wea" rather than the whole list.
    /// </summary>
    /// <remarks>
    /// A space is deliberately not one of them. It only ever separates entries
    /// after one of these, and the spaces following a separator are skipped
    /// past instead — a waypoint is very often called "North Gate", and
    /// treating the space as a break made the list re-offer everything at
    /// "North " and then vanish at "North Ga".
    /// </remarks>
    private static readonly char[] Separators = ['[', ','];

    public static ScriptCompletionResult? Suggest(string text, int caret, ScriptCompletionSource source)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int lineStart = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;

        Match match;
        try
        {
            match = ValueLine.Match(text[lineStart..caret]);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        if (!match.Success) return null;

        var offered = For(match.Groups["key"].Value.ToLowerInvariant(), source);
        if (offered is null || offered.Count == 0) return null;

        var value = match.Groups["value"].Value;
        int cut = value.LastIndexOfAny(Separators) + 1;
        // Past the space after "key:" and after each list comma, so those are
        // not read as part of what has been typed.
        while (cut < value.Length && value[cut] == ' ') cut++;
        int start = lineStart + match.Groups["value"].Index + cut;

        // The opening quote stays inside the replaced span, because the
        // suggestion brings its own — otherwise accepting one after typing a
        // quote would leave two.
        var prefix = value[cut..];
        if (prefix.StartsWith('"') || prefix.StartsWith('\'')) prefix = prefix[1..];

        var matches = offered
            .Where(s => s.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;

        int lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) lineEnd = text.Length;

        return new ScriptCompletionResult(
            matches, start, caret - start,
            AllowComment: text[caret..lineEnd].Trim().Length == 0);
    }

    /// <summary>
    /// Wraps a value in double quotes when YAML would otherwise read it as
    /// something other than the text it is.
    /// </summary>
    /// <remarks>
    /// Waypoint names are typed by people and can be anything — "Gate: North",
    /// "#3", "Yes". Quoting unconditionally would put quotes round the ordinary
    /// ones too, which reads as though they were needed; quoting never would
    /// let a suggestion break the file it was inserted into.
    /// </remarks>
    /// <summary>Words a YAML reader may take for a boolean rather than for the
    /// text they are. YAML 1.1's set, which is wider than "true" and "false" —
    /// a waypoint called "No" is a name, not an answer.</summary>
    private static readonly HashSet<string> YamlBooleans = new(StringComparer.OrdinalIgnoreCase)
        { "y", "n", "yes", "no", "true", "false", "on", "off", "null", "~" };

    public static string QuoteForYaml(string value)
    {
        if (value.Length == 0) return "\"\"";

        bool needs =
            value != value.Trim()
            || value.Contains(": ", StringComparison.Ordinal)
            || value.EndsWith(':')
            || value.Contains(" #", StringComparison.Ordinal)
            || "-?:,[]{}#&*!|>'\"%@`".Contains(value[0])
            || YamlBooleans.Contains(value)
            || double.TryParse(value, System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out _);

        return needs ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" : value;
    }

    private static IReadOnlyList<ScriptSuggestion>? For(
        string key, ScriptCompletionSource source) => key switch
    {
        // The role token first: it is the answer for a mesh running a default
        // preset, whose primary has no name of its own to pick off the list.
        // Offered under a condition too, where it names the channel a message
        // arrived on by role — the same meaning it has where a destination is
        // chosen.
        "channel" or "not_channel" =>
        [
            new ScriptSuggestion(ScriptChannels.PrimaryToken, "the primary channel, whatever it is named"),
            .. source.Channels,
        ],

        // {from.id} only here. from:/not_from: are matched against literal ids
        // by the engine, so a placeholder in one would never match anything.
        "to" =>
        [
            new ScriptSuggestion("{from.id}", "\"{from.id}\"", "whoever triggered the script"),
            .. source.Nodes,
        ],

        // {my.id} leads: it is the answer for the one script that has to
        // exclude the person running it — a geofence: greeting, which this node
        // can set off by walking into its own fence.
        "from" or "not_from" =>
        [
            new ScriptSuggestion("{my.id}", "\"{my.id}\"", "this node"),
            .. source.Nodes,
        ],

        "credential" => [.. source.Credentials.Select(c => new ScriptSuggestion(c, "stored credential"))],

        "require_key" =>
        [
            new ScriptSuggestion("true", "only send if it can be PKC-sealed — an unsealed DM is binned by the peer"),
            new ScriptSuggestion("false", "send either way"),
        ],

        // "any" first, for the same reason {primary} leads the channels: it is
        // the answer that needs no list, and a fence list is a snapshot of what
        // happens to be on the map right now.
        "geofence" =>
        [
            new ScriptSuggestion("any", "any fence, whichever is crossed"),
            .. source.Geofences,
        ],

        _ => null,
    };
}
