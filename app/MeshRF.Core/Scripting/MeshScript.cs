// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// One automation script, parsed from a single YAML file under
/// <see cref="ScriptLibrary.ScriptsDirectory"/>. The shape follows Home
/// Assistant's automation skeleton — a list of triggers, a list of conditions
/// that all have to hold, and a sequence of actions — but with a closed
/// vocabulary and no expression language: reply text interpolates a fixed set
/// of <c>{placeholder}</c> tokens (see <see cref="ScriptPlaceholders"/>) and
/// nothing else.
/// </summary>
/// <remarks>
/// A script has no <c>id:</c> key: its identity is its filename, which is what
/// the Scripts window lists and what the (future) rate limiter books cooldowns
/// against. Execution order across scripts is the order the library reports,
/// not anything stored in here.
/// </remarks>
public sealed class MeshScript
{
    /// <summary>Whether the script may fire. Persisted as the top-level
    /// <c>enabled:</c> key so the file stays the single source of truth — the
    /// list's toggle rewrites just that line (see
    /// <see cref="ScriptLibrary.SetEnabled"/>).</summary>
    public bool Enabled { get; init; }

    /// <summary>Human-readable name for the list and the activity log. Falls
    /// back to the filename when the script omits <c>alias:</c>.</summary>
    public string Alias { get; init; } = string.Empty;

    /// <summary>What to do when the script is triggered again while an earlier
    /// run is still working through a <c>delay:</c>.</summary>
    public ScriptMode Mode { get; init; } = ScriptMode.Single;

    public IReadOnlyList<ScriptTrigger> Triggers { get; init; } = Array.Empty<ScriptTrigger>();
    public IReadOnlyList<ScriptCondition> Conditions { get; init; } = Array.Empty<ScriptCondition>();
    public IReadOnlyList<ScriptAction> Actions { get; init; } = Array.Empty<ScriptAction>();
    public ScriptLimits Limits { get; init; } = new();
}

/// <summary>Re-trigger behaviour while a delayed action sequence is in flight.
/// Only meaningful for scripts that contain a <c>delay:</c>.</summary>
public enum ScriptMode
{
    /// <summary>Ignore the new trigger; let the running sequence finish.</summary>
    Single,
    /// <summary>Abandon the running sequence and start over.</summary>
    Restart,
    /// <summary>Run the new sequence after the current one finishes.</summary>
    Queued,
}

public enum ScriptTriggerKind
{
    /// <summary>Regex against the received message body.</summary>
    Text,
    /// <summary>Sugar for <c>^!name\b</c>, exposing the rest as {args}.</summary>
    Command,
    /// <summary>A node we have no record of is heard for the first time.</summary>
    NewNode,
    /// <summary>An emoji tapback lands on one of our messages.</summary>
    Reaction,
    /// <summary>Fires on a fixed interval.</summary>
    Every,
    /// <summary>Fires once a day at a wall-clock time.</summary>
    At,
    /// <summary>Fires when its button in the Quick send bar is pressed.</summary>
    QuickSend,
}

public sealed class ScriptTrigger
{
    public ScriptTriggerKind Kind { get; init; }

    /// <summary>Regex for <see cref="ScriptTriggerKind.Text"/>, the bare command
    /// word for <see cref="ScriptTriggerKind.Command"/>, the emoji (empty =
    /// any) for <see cref="ScriptTriggerKind.Reaction"/>, and the button label
    /// for <see cref="ScriptTriggerKind.QuickSend"/>.</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Where a <see cref="ScriptTriggerKind.QuickSend"/> button sends:
    /// <see cref="QuickSendAsk"/> to choose at the moment it is pressed, the
    /// name of a channel, or a node id for a direct message.</summary>
    public string Destination { get; init; } = string.Empty;

    /// <summary>Case-insensitive matching, default on. Text triggers only.</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary><see cref="ScriptTriggerKind.Every"/> interval.</summary>
    public TimeSpan Interval { get; init; }

    /// <summary><see cref="ScriptTriggerKind.At"/> wall-clock time, local.</summary>
    public TimeOnly TimeOfDay { get; init; }

    /// <summary>1-based line in the source file, for error reporting and for
    /// the editor's jump-to-problem.</summary>
    public int Line { get; init; }

    /// <summary>The <c>to:</c> value that means "prompt for the destination
    /// when the button is pressed", the way the built-in quick sends do.</summary>
    public const string QuickSendAsk = "ask";

    /// <summary>Whether this button chooses its destination when pressed.</summary>
    public bool AsksForDestination =>
        Kind == ScriptTriggerKind.QuickSend
        && string.Equals(Destination, QuickSendAsk, StringComparison.OrdinalIgnoreCase);
}

public enum ScriptConditionKind
{
    Scope,
    Channel,
    NotChannel,
    From,
    NotFrom,
    SnrAbove,
    HopsBelow,
    Between,
    Favorite,
    HasKey,
}

/// <summary>Which kind of traffic a script is allowed to answer.</summary>
public enum ScriptScope
{
    Any,
    /// <summary>Addressed to us specifically (DM, PKC or legacy).</summary>
    Direct,
    /// <summary>Broadcast on a channel.</summary>
    Channel,
    /// <summary>On the primary channel, whatever it happens to be called.
    /// Named by role rather than by name so a script stays portable — the
    /// primary is the one channel every node in a mesh shares.</summary>
    Primary,
}

public sealed class ScriptCondition
{
    public ScriptConditionKind Kind { get; init; }
    public ScriptScope Scope { get; init; }

    /// <summary>Channel names for channel/not_channel, or node ids for
    /// from/not_from.</summary>
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    /// <summary>Threshold for snr_above / hops_below.</summary>
    public double Number { get; init; }

    /// <summary>Expected value for favorite / has_key.</summary>
    public bool Flag { get; init; }

    /// <summary><c>between:</c> window, local time. A window whose end is at or
    /// before its start wraps past midnight.</summary>
    public TimeOnly Start { get; init; }
    public TimeOnly End { get; init; }

    public int Line { get; init; }
}

public enum ScriptActionKind
{
    /// <summary>Answer in the conversation the trigger arrived on, reply-linked
    /// to the triggering message.</summary>
    Reply,
    /// <summary>Send to an explicit node or channel.</summary>
    Send,
    /// <summary>Emoji tapback on the triggering message.</summary>
    React,
    Position,
    NodeInfo,
    Traceroute,
    /// <summary>Call a REST endpoint and keep the answer for a later action to
    /// say. Transmits nothing itself.</summary>
    Http,
    /// <summary>Drop a waypoint on the map, optionally with a geofence.</summary>
    Waypoint,
    /// <summary>Stop the sequence unless something holds. The only way to act
    /// on what an earlier http: returned, since conditions are settled before
    /// any action runs.</summary>
    Require,
    /// <summary>Pause before the next action in the sequence.</summary>
    Delay,
    /// <summary>Write a line to the app log. Transmits nothing.</summary>
    Log,
    /// <summary>Sound the ringtone on this machine. Transmits nothing.</summary>
    Ring,
}

public sealed record ScriptAction
{
    public ScriptActionKind Kind { get; init; }

    /// <summary>Message body (reply/send/log) or emoji (react), before
    /// placeholder expansion.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Destination node for <c>send:</c>, as <c>!a1b2c3d4</c> or a
    /// placeholder. Empty means the channel form.</summary>
    public string To { get; init; } = string.Empty;

    /// <summary>Destination channel for <c>send:</c>. Empty means the primary.</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>Set reply_id on the outgoing message so clients thread it under
    /// the triggering one. Always on for <c>reply:</c>.</summary>
    public bool ReplyLink { get; init; }

    /// <summary>
    /// Hop limit for this one message, or null to use the app's configured
    /// limit. <c>send:</c> only — a waypoint carries its own on
    /// <see cref="ScriptWaypoint.Hops"/>, which a feed sync needs too.
    /// </summary>
    public byte? Hops { get; init; }

    public TimeSpan Delay { get; init; }

    /// <summary>The request, for <see cref="ScriptActionKind.Http"/>.</summary>
    public ScriptHttpRequest? Http { get; init; }

    /// <summary>The waypoint, for <see cref="ScriptActionKind.Waypoint"/>.</summary>
    public ScriptWaypoint? Waypoint { get; init; }

    /// <summary>The test, for <see cref="ScriptActionKind.Require"/>.</summary>
    public ScriptRequirement? Require { get; init; }

    /// <summary>The tune, for <see cref="ScriptActionKind.Ring"/>.</summary>
    public ScriptRingtone? Ringtone { get; init; }

    /// <summary>
    /// Optional gate: this one action runs only while the test holds, and the
    /// sequence carries on either way.
    /// </summary>
    /// <remarks>
    /// The difference from <see cref="ScriptActionKind.Require"/> is what
    /// happens when it does not hold — require: abandons everything after it,
    /// a when: skips its own action and nothing else. That is what lets a
    /// script choose between two answers, which a stop-only test cannot
    /// express. Evaluated in sequence like a require:, so it can read
    /// {http.*} from a fetch earlier in the same run.
    /// </remarks>
    public ScriptRequirement? When { get; init; }

    public int Line { get; init; }
}

/// <summary>
/// Per-script throttles. These are the script's own ceiling — the engine also
/// applies a global budget across every script, so a runaway regex can't take
/// the channel's airtime no matter what a file asks for.
/// </summary>
public sealed class ScriptLimits
{
    /// <summary>Minimum gap between firings. Default 60s rather than zero: an
    /// unthrottled script answering a busy channel is the failure mode worth
    /// defaulting against.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Apply <see cref="Cooldown"/> per sending node instead of
    /// globally, so one chatty node can't mute the script for everyone.</summary>
    public bool PerNode { get; init; } = true;

    /// <summary>Hard ceiling on firings per rolling hour.</summary>
    public int MaxPerHour { get; init; } = 6;
}

/// <summary>
/// A button a script asks the Quick send bar to show.
/// </summary>
/// <param name="Label">What the button says, and what identifies the press.</param>
/// <param name="Destination">Where it sends: <see cref="ScriptTrigger.QuickSendAsk"/>,
/// a channel name, or a node id.</param>
/// <param name="FileName">Script that declared it, for diagnostics.</param>
public sealed record QuickSendButton(string Label, string Destination, string FileName)
{
    public bool Asks =>
        string.Equals(Destination, ScriptTrigger.QuickSendAsk, StringComparison.OrdinalIgnoreCase);
}
