// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// One action with everything already decided: placeholders expanded, the
/// destination resolved to a node number or a channel name, the reply target
/// filled in.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Text">Message body or emoji, still holding its placeholders —
/// see the remarks on <see cref="ScriptExpansion"/> for why these are filled in
/// at execution time rather than here.</param>
/// <param name="ToNode">Destination node, or 0 to broadcast on
/// <paramref name="ChannelName"/>.</param>
/// <param name="ChannelName">Destination channel, or empty for the primary.</param>
/// <param name="ReplyId">Packet to thread under, or 0.</param>
/// <param name="Delay">How long to wait before running this action.</param>
/// <param name="Http">The request, for an http: action.</param>
/// <param name="Waypoint">The marker, for a waypoint: action.</param>
/// <param name="Require">The test, for a require: action.</param>
/// <param name="When">Gate on this action alone: it is skipped when the test
/// does not hold, and the rest of the sequence runs regardless.</param>
public sealed record ResolvedAction(
    ScriptActionKind Kind,
    string Text,
    uint ToNode,
    string ChannelName,
    uint ReplyId,
    TimeSpan Delay,
    ScriptHttpRequest? Http = null,
    ScriptWaypoint? Waypoint = null,
    ScriptRequirement? Require = null,
    ScriptRequirement? When = null,
    ScriptRingtone? Ringtone = null)
{
    /// <summary>Whether this action puts a frame on the air. http: makes a
    /// network request, require: only decides, and ring: is a noise on this
    /// machine, so none count against the airtime budget.</summary>
    public bool Transmits =>
        Kind is not (ScriptActionKind.Delay or ScriptActionKind.Log
                     or ScriptActionKind.Http or ScriptActionKind.Require
                     or ScriptActionKind.Ring);

    /// <summary>One-line description for the log, e.g.
    /// <c>reply to !a1b2c3d4: "pong — 7 dB"</c>. Takes the already-expanded
    /// text, since the raw template would show placeholders instead of what is
    /// actually being sent.</summary>
    public string Describe(Func<uint, string> nameOf, string expandedText) => Kind switch
    {
        ScriptActionKind.Reply or ScriptActionKind.Send =>
            $"{(Kind == ScriptActionKind.Reply ? "reply" : "send")} to " +
            $"{(ToNode == 0 ? $"#{(ChannelName.Length == 0 ? "primary" : ChannelName)}" : nameOf(ToNode))}: \"{expandedText}\"",
        ScriptActionKind.React => $"react {expandedText} to packet {ReplyId:x8}",
        ScriptActionKind.Position => $"send position to {nameOf(ToNode)}",
        ScriptActionKind.NodeInfo => $"send node info to {nameOf(ToNode)}",
        ScriptActionKind.Traceroute => $"traceroute to {nameOf(ToNode)}",
        ScriptActionKind.Http => $"{Http?.Method.ToString().ToUpperInvariant()} {expandedText}",
        ScriptActionKind.Waypoint =>
            $"waypoint \"{expandedText}\" to " +
            $"{(ToNode == 0 ? $"#{(ChannelName.Length == 0 ? "primary" : ChannelName)}" : nameOf(ToNode))}" +
            (Waypoint is { RadiusM: > 0 } fenced ? $" with a {fenced.RadiusM} m fence" : ""),
        ScriptActionKind.Require => $"require {Require?.Describe()}",
        ScriptActionKind.Delay => $"wait {Delay.TotalSeconds:0.#}s",
        ScriptActionKind.Log => $"log: \"{expandedText}\"",
        _ => Kind.ToString(),
    };
}

/// <summary>A script that matched, and the actions it wants to run.</summary>
/// <param name="ScriptName">File name, which is the script's identity.</param>
/// <param name="Alias">Display name, for the log.</param>
/// <param name="Mode">What a re-trigger does while this run is mid-delay.</param>
/// <param name="TriggerNode">Node that set it off, or 0 for a timer.</param>
/// <param name="Actions">The sequence to run, in order.</param>
/// <param name="Expansion">Fills in placeholders as the sequence runs, and
/// accumulates any http: results along the way.</param>
public sealed record ScriptRun(
    string ScriptName,
    string Alias,
    ScriptMode Mode,
    uint TriggerNode,
    IReadOnlyList<ResolvedAction> Actions,
    ScriptExpansion Expansion)
{
    /// <summary>Whether this run would put anything on the air. A run that is
    /// only logs and delays never consumes airtime.</summary>
    public bool Transmits => Actions.Any(a => a.Transmits);
}
