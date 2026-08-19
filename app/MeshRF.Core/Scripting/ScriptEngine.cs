// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace MeshRF.Scripting;

/// <summary>
/// Matches events against the loaded scripts and produces fully-resolved runs
/// for the app to execute.
/// </summary>
/// <remarks>
/// <para>The engine decides <em>what</em> should happen and never makes it
/// happen: it returns <see cref="ScriptRun"/>s with placeholders expanded and
/// destinations resolved, and the app turns those into frames. That split is
/// what lets the whole matching layer be tested without a radio, a node store
/// or a UI thread.</para>
/// <para>Two guards are unconditional and cannot be turned off from a script
/// file. Our own traffic never triggers anything, and a message a script sent
/// can never trigger another script — the host only ever feeds decoded,
/// not-from-us events in here, and <see cref="Evaluate"/> re-checks the sender
/// anyway. One hop of automation, always.</para>
/// </remarks>
public sealed class ScriptEngine
{
    /// <summary>A script plus everything derived from it at load time, so
    /// nothing is compiled or allocated on the decode path.</summary>
    private sealed class Loaded
    {
        public required string FileName { get; init; }
        public required MeshScript Script { get; init; }

        /// <summary>Compiled matcher per trigger, for text and command
        /// triggers. Null for triggers that need no regex.</summary>
        public required Regex?[] Patterns { get; init; }

        /// <summary>Next fire time per trigger, for every:/at:. Null for
        /// triggers that aren't scheduled.</summary>
        public required DateTimeOffset?[] NextDue { get; init; }
    }

    private readonly List<Loaded> _scripts = [];

    public ScriptRateLimiter Limiter { get; } = new();

    /// <summary>Number of scripts loaded and able to fire.</summary>
    public int ArmedCount => _scripts.Count;

    /// <summary>Names of the loaded scripts, in the order they run.</summary>
    public IReadOnlyList<string> ArmedNames => _scripts.Select(s => s.FileName).ToList();

    /// <summary>Raised for anything worth putting in the app log that isn't a
    /// run: a skipped action, a regex that timed out, a script held back by its
    /// limits.</summary>
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Replaces the loaded set with the enabled, valid scripts from
    /// <paramref name="files"/>, keeping the order given. Disabled and broken
    /// files are ignored — the Scripts window is where those get reported, and
    /// the engine has no business half-running a script that failed to parse.
    /// </summary>
    public void Load(IEnumerable<ScriptFile> files, DateTimeOffset now)
    {
        _scripts.Clear();
        // Cooldowns belonged to the previous set of scripts, and a user who has
        // just finished editing expects to be able to test straight away.
        Limiter.Reset();

        foreach (var file in files)
        {
            if (!file.Enabled || file.Parse.Script is not { } script) continue;

            var patterns = new Regex?[script.Triggers.Count];
            var nextDue = new DateTimeOffset?[script.Triggers.Count];

            for (int i = 0; i < script.Triggers.Count; i++)
            {
                var trigger = script.Triggers[i];
                switch (trigger.Kind)
                {
                    case ScriptTriggerKind.Text:
                        patterns[i] = Compile(trigger.Pattern, trigger.IgnoreCase, file.FileName);
                        break;

                    case ScriptTriggerKind.Command:
                        // The command form is sugar, so it becomes the regex the
                        // user would otherwise have had to write. \b would not
                        // do: "!ping" followed by "!" has no word boundary.
                        patterns[i] = Compile(
                            $@"^\s*!{Regex.Escape(trigger.Pattern)}(?:\s|$)", ignoreCase: true, file.FileName);
                        break;

                    case ScriptTriggerKind.Every:
                        // Deliberately not "due immediately": every enabled
                        // beacon would otherwise transmit at once on startup.
                        nextDue[i] = now + trigger.Interval;
                        break;

                    case ScriptTriggerKind.At:
                        nextDue[i] = NextOccurrence(trigger.TimeOfDay, now);
                        break;
                }
            }

            if (patterns.Where((p, i) => script.Triggers[i].Kind is ScriptTriggerKind.Text or ScriptTriggerKind.Command)
                        .Any(p => p is null))
            {
                // Compile() already said why.
                continue;
            }

            _scripts.Add(new Loaded
            {
                FileName = file.FileName,
                Script = script,
                Patterns = patterns,
                NextDue = nextDue,
            });
        }
    }

    private Regex? Compile(string pattern, bool ignoreCase, string fileName)
    {
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return new Regex(pattern, options, ScriptParser.RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            // The parser validates patterns, so reaching here means the file
            // changed under us. Drop the script rather than run it half-armed.
            Diagnostic?.Invoke($"script {fileName} disabled: bad pattern — {ex.Message}");
            return null;
        }
    }

    // ----- event evaluation ---------------------------------------------------

    /// <summary>
    /// Every script that matches <paramref name="evt"/> and is within its
    /// limits, in execution order.
    /// </summary>
    public IReadOnlyList<ScriptRun> Evaluate(ScriptEvent evt)
    {
        // Never answer ourselves. The router already drops our own
        // transmissions before they reach the host, so this is the belt to that
        // braces — but it is the guard that makes a two-node feedback loop
        // impossible rather than merely unlikely.
        if (evt.FromNode != 0 && evt.FromNode == evt.Self.NodeNum) return [];

        var runs = new List<ScriptRun>();
        foreach (var loaded in _scripts)
        {
            if (!TryMatchTrigger(loaded, evt, out var captures, out var args)) continue;
            if (!ConditionsHold(loaded.Script, evt)) continue;
            if (BuildRun(loaded, evt, args, captures) is { } run) runs.Add(run);
        }
        return runs;
    }

    /// <summary>
    /// Scheduled triggers that have come due. Called from the app's poll timer.
    /// </summary>
    /// <remarks>
    /// A timer event has no sender, so conditions that ask about one
    /// (<c>scope</c>, <c>from</c>, <c>snr_above</c> and friends) fail closed
    /// and the script simply doesn't fire. Only <c>between:</c> is meaningful
    /// on a schedule, which is what the help window says.
    /// </remarks>
    public IReadOnlyList<ScriptRun> Tick(DateTimeOffset now, ScriptSelf self)
    {
        List<ScriptRun>? runs = null;

        foreach (var loaded in _scripts)
        {
            for (int i = 0; i < loaded.Script.Triggers.Count; i++)
            {
                if (loaded.NextDue[i] is not { } due || due > now) continue;

                var trigger = loaded.Script.Triggers[i];
                // Rescheduled from now, not from the due time: a laptop that
                // slept for six hours should resume beaconing, not fire six
                // catch-up beacons back to back.
                loaded.NextDue[i] = trigger.Kind == ScriptTriggerKind.Every
                    ? now + trigger.Interval
                    : NextOccurrence(trigger.TimeOfDay, now);

                var evt = new ScriptEvent { Kind = ScriptEventKind.Timer, Self = self, At = now };
                if (!ConditionsHold(loaded.Script, evt)) continue;
                if (BuildRun(loaded, evt, args: null, captures: null) is { } run) (runs ??= []).Add(run);
            }
        }

        return (IReadOnlyList<ScriptRun>?)runs ?? [];
    }

    private static DateTimeOffset NextOccurrence(TimeOnly timeOfDay, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Year, now.Month, now.Day,
            timeOfDay.Hour, timeOfDay.Minute, 0, now.Offset);
        return today > now ? today : today.AddDays(1);
    }

    private bool TryMatchTrigger(
        Loaded loaded, ScriptEvent evt,
        out IReadOnlyList<string>? captures, out IReadOnlyList<string>? args)
    {
        captures = null;
        args = null;

        for (int i = 0; i < loaded.Script.Triggers.Count; i++)
        {
            var trigger = loaded.Script.Triggers[i];
            switch (trigger.Kind)
            {
                case ScriptTriggerKind.Text when evt.Kind == ScriptEventKind.Text:
                {
                    if (Match(loaded, i, evt.Text) is not { Success: true } match) break;
                    captures = match.Groups.Cast<Group>().Skip(1).Select(g => g.Value).ToList();
                    args = ScriptTemplate.SplitArguments(evt.Text);
                    return true;
                }

                case ScriptTriggerKind.Command when evt.Kind == ScriptEventKind.Text:
                {
                    if (Match(loaded, i, evt.Text) is not { Success: true }) break;
                    args = ScriptTemplate.SplitArguments(evt.Text);
                    return true;
                }

                case ScriptTriggerKind.NewNode when evt.Kind == ScriptEventKind.NewNode:
                    return true;

                case ScriptTriggerKind.Reaction when evt.Kind == ScriptEventKind.Reaction:
                    // An empty pattern is the "any" form.
                    if (trigger.Pattern.Length == 0 ||
                        string.Equals(trigger.Pattern, evt.Emoji, StringComparison.Ordinal))
                        return true;
                    break;
            }
        }
        return false;
    }

    private Match? Match(Loaded loaded, int triggerIndex, string text)
    {
        if (loaded.Patterns[triggerIndex] is not { } regex) return null;
        try
        {
            return regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic backtracking on attacker-supplied message text is a
            // real possibility, which is why patterns carry a timeout at all.
            Diagnostic?.Invoke($"script {loaded.FileName}: pattern took too long on an incoming message, skipped");
            return null;
        }
    }

    private static bool ConditionsHold(MeshScript script, ScriptEvent evt)
    {
        foreach (var condition in script.Conditions)
        {
            if (!Holds(condition, evt)) return false;
        }
        return true;
    }

    /// <summary>
    /// One condition. Everything fails closed when the event carries no answer:
    /// a script asking about signal strength should not fire on an event that
    /// has none, and silence is the safe direction for something that keys up a
    /// transmitter.
    /// </summary>
    private static bool Holds(ScriptCondition condition, ScriptEvent evt)
    {
        switch (condition.Kind)
        {
            case ScriptConditionKind.Scope:
                return condition.Scope switch
                {
                    ScriptScope.Any => true,
                    ScriptScope.Direct => evt.Kind != ScriptEventKind.Timer && evt.IsDirect,
                    ScriptScope.Channel => evt.Kind != ScriptEventKind.Timer && !evt.IsDirect,
                    // Broadcast on the primary. A direct message carries no
                    // channel of its own, so it is never "on" one.
                    ScriptScope.Primary => evt.Kind != ScriptEventKind.Timer && !evt.IsDirect && evt.IsPrimaryChannel,
                    _ => false,
                };

            case ScriptConditionKind.Channel:
                return evt.Channel.Length > 0 &&
                       condition.Values.Contains(evt.Channel, StringComparer.OrdinalIgnoreCase);

            case ScriptConditionKind.From:
                return evt.FromNode != 0 && MatchesNode(condition.Values, evt.FromNode);

            case ScriptConditionKind.NotFrom:
                // The inverse is vacuously true with no sender: a timer event
                // is not from anybody, so it is not from the excluded node.
                return evt.FromNode == 0 || !MatchesNode(condition.Values, evt.FromNode);

            case ScriptConditionKind.SnrAbove:
                return evt.SnrDb is { } snr && snr > condition.Number;

            case ScriptConditionKind.HopsBelow:
                return evt.Kind != ScriptEventKind.Timer && evt.Hops < condition.Number;

            case ScriptConditionKind.Between:
                // The event's own offset, not the machine's current one. The
                // host stamps events with DateTimeOffset.Now, so this is local
                // wall-clock time in practice — but reading it off the event
                // keeps the engine free of ambient state, which is what makes
                // a time window testable at all.
                return InWindow(TimeOnly.FromTimeSpan(evt.At.TimeOfDay), condition.Start, condition.End);

            case ScriptConditionKind.Favorite:
                return evt.FromNode != 0 && evt.SenderIsFavorite == condition.Flag;

            case ScriptConditionKind.HasKey:
                return evt.FromNode != 0 && evt.SenderHasKey == condition.Flag;

            default:
                return false;
        }
    }

    /// <summary>Half-open window. A window whose end is before its start wraps
    /// past midnight, so "22:00-06:00" is the night rather than an empty
    /// set.</summary>
    private static bool InWindow(TimeOnly now, TimeOnly start, TimeOnly end) =>
        start <= end ? now >= start && now < end : now >= start || now < end;

    private static bool MatchesNode(IReadOnlyList<string> values, uint nodeNum)
    {
        foreach (var value in values)
        {
            if (TryParseNodeId(value) == nodeNum) return true;
        }
        return false;
    }

    /// <summary>Parses <c>!a1b2c3d4</c> (or the bare hex) to a node number.
    /// Returns 0 for anything else, which every caller treats as "no
    /// destination".</summary>
    public static uint TryParseNodeId(string text)
    {
        var id = text.Trim();
        if (id.StartsWith('!')) id = id[1..];
        return id.Length == 8 && uint.TryParse(id, System.Globalization.NumberStyles.HexNumber,
                                               System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    // ----- action resolution --------------------------------------------------

    private ScriptRun? BuildRun(
        Loaded loaded, ScriptEvent evt, IReadOnlyList<string>? args, IReadOnlyList<string>? captures)
    {
        var expansion = new ScriptExpansion(evt, args, captures);

        var actions = new List<ResolvedAction>(loaded.Script.Actions.Count);
        foreach (var action in loaded.Script.Actions)
        {
            if (Resolve(loaded.FileName, action, evt, expansion) is not { } resolved) continue;
            actions.Add(action.When is null ? resolved : resolved with { When = action.When });
        }

        if (actions.Count == 0) return null;

        // Charged against the global airtime budget only if it actually keys up.
        // A gated action counts here even though it may be skipped: a when: can
        // read {http.*}, so what it decides is not known until the sequence
        // runs, and booking airtime that goes unused is the safe direction to
        // be wrong in. Same as a run that stops at a require:.
        bool transmits = actions.Any(a => a.Transmits);
        if (!Limiter.TryFire(loaded.FileName, loaded.Script.Limits, evt.FromNode, evt.At, transmits, out var reason))
        {
            Diagnostic?.Invoke($"script {loaded.FileName} matched but {reason}");
            return null;
        }

        return new ScriptRun(
            loaded.FileName,
            loaded.Script.Alias.Length > 0 ? loaded.Script.Alias : loaded.FileName,
            loaded.Script.Mode,
            evt.FromNode,
            actions,
            expansion);
    }

    /// <remarks>
    /// Message text is left as a template here and expanded when the action
    /// runs, because an http: action earlier in the sequence may supply part of
    /// it. Routing is resolved now: a destination cannot come from a fetch, so
    /// a bad one is worth catching before anything is sent.
    /// </remarks>
    private ResolvedAction? Resolve(
        string fileName, ScriptAction action, ScriptEvent evt, ScriptExpansion expansion)
    {
        // Where an answer goes: back to the sender for a direct message,
        // otherwise out on the channel it arrived on.
        uint replyTo = evt.IsDirect ? evt.FromNode : 0;
        string replyChannel = evt.IsDirect ? string.Empty : evt.Channel;

        switch (action.Kind)
        {
            case ScriptActionKind.Reply:
                if (evt.Kind == ScriptEventKind.Timer)
                {
                    Diagnostic?.Invoke($"script {fileName}: reply: skipped, a scheduled trigger has nobody to reply to");
                    return null;
                }
                return new ResolvedAction(
                    ScriptActionKind.Reply, action.Text,
                    replyTo, replyChannel, evt.PacketId, TimeSpan.Zero);

            case ScriptActionKind.Send:
            {
                uint to = 0;
                if (action.To.Length > 0)
                {
                    var expanded = expansion.Expand(action.To);
                    to = TryParseNodeId(expanded);
                    if (to == 0)
                    {
                        Diagnostic?.Invoke(
                            $"script {fileName}: send: skipped, to: \"{expanded}\" is not a node id");
                        return null;
                    }
                }
                return new ResolvedAction(
                    ScriptActionKind.Send, action.Text,
                    to, action.Channel,
                    action.ReplyLink && evt.Kind != ScriptEventKind.Timer ? evt.PacketId : 0,
                    TimeSpan.Zero);
            }

            case ScriptActionKind.Http:
                return action.Http is null
                    ? null
                    : new ResolvedAction(
                        ScriptActionKind.Http, action.Http.Url,
                        0, string.Empty, 0, TimeSpan.Zero, action.Http);

            case ScriptActionKind.Waypoint:
            {
                if (action.Waypoint is not { } waypoint) return null;
                uint marked = 0;
                if (waypoint.To.Length > 0)
                {
                    var expanded = expansion.Expand(waypoint.To);
                    marked = TryParseNodeId(expanded);
                    if (marked == 0)
                    {
                        Diagnostic?.Invoke(
                            $"script {fileName}: waypoint: skipped, to: \"{expanded}\" is not a node id");
                        return null;
                    }
                }
                return new ResolvedAction(
                    ScriptActionKind.Waypoint, waypoint.Name,
                    marked, waypoint.Channel, 0, TimeSpan.Zero, Waypoint: waypoint);
            }

            case ScriptActionKind.Require:
                return action.Require is null
                    ? null
                    : new ResolvedAction(
                        ScriptActionKind.Require, action.Require.Value,
                        0, string.Empty, 0, TimeSpan.Zero, Require: action.Require);

            case ScriptActionKind.React:
                if (evt.PacketId == 0)
                {
                    Diagnostic?.Invoke($"script {fileName}: react: skipped, nothing to react to");
                    return null;
                }
                return new ResolvedAction(
                    ScriptActionKind.React, action.Text, replyTo, replyChannel, evt.PacketId, TimeSpan.Zero);

            case ScriptActionKind.Position:
            case ScriptActionKind.NodeInfo:
            case ScriptActionKind.Traceroute:
                if (evt.FromNode == 0)
                {
                    Diagnostic?.Invoke(
                        $"script {fileName}: {action.Kind.ToString().ToLowerInvariant()}: skipped, no node to send it to");
                    return null;
                }
                return new ResolvedAction(
                    action.Kind, string.Empty, evt.FromNode, evt.Channel, 0, TimeSpan.Zero);

            case ScriptActionKind.Delay:
                return new ResolvedAction(
                    ScriptActionKind.Delay, string.Empty, 0, string.Empty, 0, action.Delay);

            case ScriptActionKind.Log:
                return new ResolvedAction(
                    ScriptActionKind.Log, action.Text, 0, string.Empty, 0, TimeSpan.Zero);

            case ScriptActionKind.Ring:
                return new ResolvedAction(
                    ScriptActionKind.Ring, string.Empty, 0, string.Empty, 0, TimeSpan.Zero,
                    Ringtone: action.Ringtone);

            default:
                return null;
        }
    }
}
