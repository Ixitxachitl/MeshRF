// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Scripting;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Runs the automation scripts: feeds decoded events to the engine and turns
/// the runs it hands back into transmissions.
/// </summary>
/// <remarks>
/// <para>The engine decides what should happen; this file is the only place
/// that makes it happen. Every send goes through <c>SendTextAsync</c> — the
/// same method the compose box uses — so scripted traffic gets duty-cycle
/// gating, licensed-mode blocking, MQTT uplink, delivery marks and history
/// exactly like a message the user typed.</para>
/// </remarks>
public partial class RadioViewModel : IScriptRuntime, IScriptCredentialSource
{
    private readonly ScriptEngine _scriptEngine = new();
    private readonly ScriptLibrary _scriptLibrary = new();
    private readonly ScriptHttpClient _scriptHttp = new();

    /// <summary>In-flight runs, keyed by script name, so <c>mode:</c> has
    /// something to act on. Only scripts containing a delay stay here long
    /// enough to matter.</summary>
    private readonly Dictionary<string, ScriptRunState> _scriptRuns = new(StringComparer.Ordinal);

    private sealed class ScriptRunState
    {
        public required CancellationTokenSource Cancellation { get; init; }
        public required Task Task { get; set; }
    }

    // ----- IScriptRuntime -----------------------------------------------------

    [ObservableProperty] private bool _scriptsEnabled;
    [ObservableProperty] private bool _scriptsDryRun;

    partial void OnScriptsEnabledChanged(bool value)
    {
        SaveSettings();
        RaiseScriptsStatusChanged();
        // Same gate SaveSettings() uses, and for the same reason. The settings
        // load assigns this property before InitScripting() has handed the
        // engine its scripts, so logging here during load always reported
        // "0 armed" no matter how many were sitting in the folder.
        // InitScripting() logs the real state once it has them.
        if (!_settingsLoaded) return;
        LogScriptsState();
    }

    partial void OnScriptsDryRunChanged(bool value)
    {
        SaveSettings();
        RaiseScriptsStatusChanged();
        if (!_settingsLoaded) return; // boot state is covered by LogScriptsState()
        if (ScriptsEnabled) _rxHost.Log(value ? "scripts: dry run on, nothing will be transmitted" : "scripts: dry run off");
    }

    /// <summary>
    /// One log line describing the master switch and how many scripts sit
    /// behind it. Shared between the toggle handler and startup so the two
    /// cannot drift, and so neither can report a count the engine has not
    /// actually loaded yet.
    /// </summary>
    private void LogScriptsState()
    {
        if (ScriptsEnabled)
        {
            _rxHost.Log($"scripts enabled — {_scriptEngine.ArmedCount} armed" +
                        (ScriptsDryRun ? ", dry run (nothing is transmitted)" : ""));
            return;
        }
        // Worth naming the count even when switched off: armed scripts waiting
        // behind a disabled master switch is a different situation from having
        // none, and the old line could not tell them apart.
        _rxHost.Log(_scriptEngine.ArmedCount == 0
            ? "scripts disabled"
            : $"scripts disabled — {_scriptEngine.ArmedCount} armed, not running");
    }

    public string ScriptsStatus
    {
        get
        {
            if (_scriptEngine.ArmedCount == 0) return "No scripts are enabled.";
            int fired = _scriptEngine.Limiter.FiredInLastHour(DateTimeOffset.Now);
            var armed = $"{_scriptEngine.ArmedCount} script{(_scriptEngine.ArmedCount == 1 ? "" : "s")} armed";
            var recent = $"{fired} run{(fired == 1 ? "" : "s")} in the last hour";
            if (!ScriptsEnabled) return $"{armed}, but scripts are switched off.";
            if (ScriptsDryRun) return $"{armed} · {recent} · dry run, nothing is transmitted.";
            return $"{armed} · {recent}.";
        }
    }

    public event Action? ScriptsStatusChanged;

    private void RaiseScriptsStatusChanged()
    {
        OnPropertyChanged(nameof(ScriptsStatus));
        ScriptsStatusChanged?.Invoke();
    }

    // ----- lifecycle ----------------------------------------------------------

    /// <summary>Wires the engine to the receive path. Called once during
    /// construction, after the host exists.</summary>
    private void InitScripting()
    {
        _scriptEngine.Diagnostic += line => LogFromAnyThread($"scripts: {line}");
        _rxHost.ScriptSelfProvider = BuildScriptSelf;
        _rxHost.ScriptEventObserved = OnScriptEvent;
        _scriptHttp.Credentials = this;
        ReloadScripts();
        // Now that the engine actually has its scripts, report the state the
        // settings load could not. Silent only when there is genuinely nothing
        // to say, so a user who never wrote a script gets no boot noise.
        if (ScriptsEnabled || _scriptEngine.ArmedCount > 0) LogScriptsState();
    }

    /// <summary>
    /// Resolves the name a script's <c>credential:</c> refers to.
    /// </summary>
    /// <remarks>
    /// Reads from settings, which is where the values live protected at rest.
    /// Nothing hands the value back to a script — it goes straight from here
    /// into the request headers, so a script cannot read its own key and
    /// therefore cannot broadcast it.
    /// </remarks>
    ScriptCredential? IScriptCredentialSource.Find(string name) =>
        _settings.ScriptCredentials.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The stored credentials, for the management dialog.</summary>
    public List<ScriptCredential> ScriptCredentials => _settings.ScriptCredentials;

    /// <summary>Persists credential edits made in the dialog.</summary>
    public void SaveScriptCredentials() => SaveSettings();

    /// <summary>Re-reads the scripts directory. Safe at any time — in-flight
    /// runs are left to finish under the rules they started with.</summary>
    public void ReloadScripts()
    {
        try
        {
            _scriptEngine.Load(_scriptLibrary.Load(), DateTimeOffset.Now);
            _scriptEngine.Limiter.GlobalMaxPerHour = ScriptsGlobalMaxPerHour;
        }
        catch (Exception ex)
        {
            _rxHost.Log($"scripts: could not read the scripts folder — {ex.Message}");
        }
        RaiseScriptsStatusChanged();
    }

    /// <summary>Ceiling on scripted transmissions per hour across every script
    /// together. Not settable from a script file: it is the limit that stands
    /// between a mistaken regex and a channel nobody else can use.</summary>
    private const int ScriptsGlobalMaxPerHour = 30;

    /// <summary>
    /// This node as scripts see it, rebuilt per event so a script always reads
    /// the current name and position rather than whatever they were when
    /// scripting started.
    /// </summary>
    private ScriptSelf BuildScriptSelf()
    {
        double? lat = null, lon = null;
        if (TryGetHomeLocation(out var homeLat, out var homeLon))
        {
            lat = homeLat;
            lon = homeLon;
        }

        return new ScriptSelf(
            _rxHost.MyNodeNum, MyShortName, MyLongName,
            // 101 is the mains-powered sentinel this app reports in its own
            // device metrics; ScriptTemplate renders it as "mains".
            BatteryPct: 101,
            Latitude: lat,
            Longitude: lon);
    }

    // ----- event path ---------------------------------------------------------

    /// <summary>
    /// A decoded event the engine might care about. Called on the UI thread
    /// from the receive host.
    /// </summary>
    private void OnScriptEvent(ScriptEvent evt)
    {
        if (!ScriptsEnabled || _scriptEngine.ArmedCount == 0) return;

        IReadOnlyList<ScriptRun> runs;
        try
        {
            runs = _scriptEngine.Evaluate(evt);
        }
        catch (Exception ex)
        {
            // A script must never be able to take the receive path down with it.
            _rxHost.Log($"scripts: evaluation failed — {ex.Message}");
            return;
        }

        foreach (var run in runs) Start(run);
        if (runs.Count > 0) RaiseScriptsStatusChanged();
    }

    /// <summary>Scheduled triggers. Driven from <c>Poll</c>, alongside the
    /// auto-report tick.</summary>
    private void TickScripts()
    {
        if (!ScriptsEnabled || _scriptEngine.ArmedCount == 0) return;

        IReadOnlyList<ScriptRun> runs;
        try
        {
            runs = _scriptEngine.Tick(
                DateTimeOffset.Now,
                BuildScriptSelf());
        }
        catch (Exception ex)
        {
            _rxHost.Log($"scripts: scheduled evaluation failed — {ex.Message}");
            return;
        }

        foreach (var run in runs) Start(run);
        if (runs.Count > 0) RaiseScriptsStatusChanged();
    }

    // ----- run execution ------------------------------------------------------

    /// <summary>
    /// Starts a run, applying <c>mode:</c> against anything already in flight
    /// for the same script.
    /// </summary>
    private void Start(ScriptRun run)
    {
        if (_scriptRuns.TryGetValue(run.ScriptName, out var existing) && !existing.Task.IsCompleted)
        {
            switch (run.Mode)
            {
                case ScriptMode.Single:
                    _rxHost.Log($"scripts: {run.Alias} is already running, skipped (mode: single)");
                    return;

                case ScriptMode.Restart:
                    existing.Cancellation.Cancel();
                    break;

                case ScriptMode.Queued:
                    // Chained rather than run in parallel, so two beacons can't
                    // key up at the same moment.
                    var previous = existing.Task;
                    existing.Task = previous.ContinueWith(
                        _ => ExecuteAsync(run, existing.Cancellation.Token),
                        TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
                    return;
            }
        }

        var cancellation = new CancellationTokenSource();
        var state = new ScriptRunState { Cancellation = cancellation, Task = Task.CompletedTask };
        _scriptRuns[run.ScriptName] = state;
        state.Task = ExecuteAsync(run, cancellation.Token);
    }

    private async Task ExecuteAsync(ScriptRun run, CancellationToken cancellation)
    {
        var prefix = ScriptsDryRun ? "would " : string.Empty;
        _rxHost.Log($"scripts: {run.Alias} fired");

        foreach (var action in run.Actions)
        {
            if (cancellation.IsCancellationRequested)
            {
                _rxHost.Log($"scripts: {run.Alias} restarted, abandoning the rest of the sequence");
                return;
            }

            if (action.Kind == ScriptActionKind.Delay)
            {
                try { await Task.Delay(action.Delay, cancellation); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            if (action.Kind == ScriptActionKind.Log)
            {
                _rxHost.Log($"scripts: {run.Alias}: {run.Expansion.Expand(action.Text)}");
                continue;
            }

            if (action.Kind == ScriptActionKind.Http)
            {
                if (!await FetchAsync(run, action, cancellation)) return;
                continue;
            }

            if (action.Kind == ScriptActionKind.Require)
            {
                if (action.Require is not { } requirement) continue;
                if (requirement.Holds(run.Expansion, out var detail)) continue;

                // Logged rather than silent: a script that stops here is doing
                // its job, but "why did nothing happen?" has to be answerable
                // without turning on dry run and waiting for it to fire again.
                _rxHost.Log($"scripts: {run.Alias} — stopped, {detail} is not true");
                return;
            }

            // Expanded here, not when the script matched: an http: action
            // earlier in this sequence may have supplied part of the wording.
            var text = run.Expansion.ExpandMessage(action.Text);
            _rxHost.Log($"scripts: {run.Alias} — {prefix}{action.Describe(_rxHost.NodeDisplayName, text)}");
            if (ScriptsDryRun) continue;

            try
            {
                await DispatchAsync(run, action, text);
            }
            catch (Exception ex)
            {
                _rxHost.Log($"scripts: {run.Alias} — action failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Runs one <c>http:</c> action and stores its result for the actions that
    /// follow. Returns false to abandon the rest of the sequence.
    /// </summary>
    /// <remarks>
    /// <para>A failed fetch stops the run rather than carrying on: the next
    /// action is almost always a reply built around the value, and broadcasting
    /// "It's °C in London" is worse than saying nothing.</para>
    /// <para>Dry run still performs GET, because seeing the real answer is most
    /// of what dry run is for and a read changes nothing. POST and PUT are
    /// skipped — those alter state on somebody else's server, which is not
    /// something a rehearsal should do.</para>
    /// </remarks>
    private async Task<bool> FetchAsync(ScriptRun run, ResolvedAction action, CancellationToken cancellation)
    {
        if (action.Http is not { } request) return true;

        var url = run.Expansion.ExpandUrl(request.Url);
        var method = request.Method.ToString().ToUpperInvariant();

        if (ScriptsDryRun && request.IsWrite)
        {
            _rxHost.Log($"scripts: {run.Alias} — would {method} {url} (skipped: dry run does not send write requests)");
            return false;
        }

        // The URL is logged as the script wrote it. A query-placed credential is
        // attached inside the client, after this point, so it cannot reach here.
        _rxHost.Log($"scripts: {run.Alias} — {method} {url}");

        ScriptHttpResult result;
        try
        {
            result = await _scriptHttp.SendAsync(request, run.Expansion, cancellation);
        }
        catch (Exception ex)
        {
            _rxHost.Log($"scripts: {run.Alias} — request failed: {ex.Message}");
            return false;
        }

        if (!result.Ok)
        {
            _rxHost.Log($"scripts: {run.Alias} — {result.Error}; the rest of the script was skipped");
            return false;
        }

        foreach (var (name, value) in result.Values) run.Expansion.SetHttpResult(name, value);
        run.Expansion.SetHttpResult("status", result.Status.ToString(CultureInfo.InvariantCulture));

        var read = string.Join(", ", result.Values.Select(v => $"{{http.{v.Key}}} = \"{v.Value}\""));
        _rxHost.Log($"scripts: {run.Alias} — {result.Status}, {read}");
        return true;
    }

    /// <summary>Turns one resolved action into a transmission.</summary>
    /// <param name="text">The message body, already expanded and clamped.</param>
    private async Task DispatchAsync(ScriptRun run, ResolvedAction action, string text)
    {
        if (!CanTransmit)
        {
            _rxHost.Log("scripts: nothing sent — no TX-capable device, or this node has no identity yet");
            return;
        }

        switch (action.Kind)
        {
            case ScriptActionKind.Reply:
            case ScriptActionKind.Send:
            {
                var (channel, to, messages) = ResolveDestination(action);
                if (channel is null && to == 0xFFFFFFFFu)
                {
                    _rxHost.Log("scripts: nothing sent — no channel to send on");
                    return;
                }
                if (text.Length == 0)
                {
                    _rxHost.Log("scripts: nothing sent — the message came out empty once filled in");
                    return;
                }
                await SendTextAsync(channel, to, text, action.ReplyId,
                                    ReplyContextFor(run, action.ReplyId), messages);
                break;
            }

            case ScriptActionKind.React:
            {
                var (channel, to, _) = ResolveDestination(action);
                if (channel is null) return;
                var packetId = NextPacketId();
                var frame = MeshEncoder.EncodeTextMessage(channel, _rxHost.MyNodeNum, packetId, action.Text,
                    to: to, replyId: action.ReplyId, emoji: 1);
                if (await TransmitFrameAsync(frame))
                    _rxHost.PersistOutgoingReaction(to, packetId, action.ReplyId, action.Text, channel.Name);
                break;
            }

            case ScriptActionKind.NodeInfo:
                HandleAutoReplyRequest(PortNum.NodeInfo, action.ToNode, action.ChannelName);
                break;

            case ScriptActionKind.Position:
                HandleAutoReplyRequest(PortNum.Position, action.ToNode, action.ChannelName);
                break;

            case ScriptActionKind.Waypoint:
                await DropWaypointAsync(run, action, text);
                break;

            case ScriptActionKind.Traceroute:
            {
                // Routed through the same command the context menu uses, so a
                // script cannot bypass the 30-second traceroute cooldown —
                // traceroutes are the most expensive thing this app transmits.
                var node = _rxHost.Nodes.FirstOrDefault(n => n.NodeNum == action.ToNode);
                if (node is null)
                {
                    _rxHost.Log($"scripts: no node record for {_rxHost.NodeDisplayName(action.ToNode)}, traceroute skipped");
                    return;
                }
                await Traceroute(node);
                break;
            }
        }
    }

    /// <summary>
    /// Sends a scripted waypoint.
    /// </summary>
    /// <remarks>
    /// Coordinates are resolved here rather than in the engine because they may
    /// come from an http: result earlier in the same sequence, or from this
    /// node's home location, neither of which the engine knows about when the
    /// script matched.
    /// </remarks>
    private async Task DropWaypointAsync(ScriptRun run, ResolvedAction action, string expandedName)
    {
        if (action.Waypoint is not { } waypoint) return;

        double lat, lon;
        if (waypoint.UseHome)
        {
            if (!TryGetHomeLocation(out lat, out lon))
            {
                _rxHost.Log("scripts: waypoint skipped — no home location is set (My Node → Home)");
                return;
            }
        }
        else
        {
            var latText = run.Expansion.Expand(waypoint.Latitude);
            var lonText = run.Expansion.Expand(waypoint.Longitude);
            if (!TryCoordinate(latText, 90, out lat) || !TryCoordinate(lonText, 180, out lon))
            {
                _rxHost.Log($"scripts: waypoint skipped — \"{latText}\", \"{lonText}\" is not a position");
                return;
            }
        }

        var channel = waypoint.Channel.Length > 0
            ? _rxHost.FindChannelByName(waypoint.Channel) ?? PrimaryChannel()
            : PrimaryChannel();
        if (channel is null)
        {
            _rxHost.Log("scripts: waypoint skipped — no channel to send it on");
            return;
        }

        var packetId = NextPacketId();
        // Expiry is relative in a script and absolute on the wire. A marker a
        // script drops unattended really wants one, which is why the parser
        // warns when it is missing.
        uint expireEpoch = waypoint.Expires > TimeSpan.Zero
            ? (uint)DateTimeOffset.UtcNow.Add(waypoint.Expires).ToUnixTimeSeconds()
            : 0;

        var frame = MeshEncoder.EncodeWaypoint(
            channel, _rxHost.MyNodeNum, packetId, waypointId: packetId, lat, lon,
            name: expandedName.Length > 0 ? expandedName : "Waypoint",
            description: ScriptTemplate.ClampToPayload(run.Expansion.Expand(waypoint.Description)),
            expireEpoch: expireEpoch,
            lockedTo: waypoint.LockToMe ? _rxHost.MyNodeNum : 0,
            icon: waypoint.Icon.Length > 0 ? EmojiToCodePoint(waypoint.Icon) : null,
            geofenceRadiusM: waypoint.RadiusM,
            notifyOnEnter: waypoint.NotifyOnEnter,
            notifyOnExit: waypoint.NotifyOnExit,
            to: 0xFFFFFFFFu,
            hopLimit: (byte)HopLimit,
            okToMqtt: OkToMqtt);

        if (!await TransmitFrameAsync(frame))
            _rxHost.Log("scripts: waypoint transmit failed");
    }

    /// <summary>
    /// The quote line shown above a scripted reply's own bubble, built the same
    /// way <c>ReplyToMessage</c> builds it for one the user sent.
    /// </summary>
    /// <remarks>
    /// Without this a scripted reply echoed as reply-linked but with no quote,
    /// so the bubble opened with a blank line where "replying to …" belongs and
    /// looked unlike every other reply in the conversation. Only the local echo
    /// is affected — the frame on the air carries reply_id either way.
    /// </remarks>
    private string ReplyContextFor(ScriptRun run, uint replyId)
    {
        if (replyId == 0) return string.Empty;

        var evt = run.Expansion.Event;
        if (evt.FromNode == 0) return string.Empty;

        var preview = evt.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (preview.Length > 80) preview = preview[..80] + "...";
        if (preview.Length == 0) preview = "(empty)";
        return $"replying to {_rxHost.NodeDisplayName(evt.FromNode)}: \"{preview}\"";
    }

    private static bool TryCoordinate(string text, double limit, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && Math.Abs(value) <= limit;

    /// <summary>
    /// Works out which channel a scripted message goes on, who it is addressed
    /// to, and which bubble list (if any) it should be echoed into.
    /// </summary>
    /// <remarks>
    /// A script can answer a channel the user has no tab open for, in which case
    /// there is nowhere to echo — the message is still sent and still recorded
    /// in history, it simply has no bubble until that tab is opened.
    /// </remarks>
    private (ChannelConfig? Channel, uint To, ObservableCollection<ChannelMessage>? Messages) ResolveDestination(
        ResolvedAction action)
    {
        if (action.ToNode != 0)
        {
            // A DM. The channel is only the legacy fallback when PKC isn't
            // available; SendTextAsync decides which is used.
            var conversation = Tabs.OfType<ConversationTabViewModel>()
                                   .FirstOrDefault(t => t.NodeNum == action.ToNode);
            return (PrimaryChannel(), action.ToNode, conversation?.Messages);
        }

        var named = action.ChannelName.Length > 0
            ? Tabs.OfType<ChannelTabViewModel>()
                  .FirstOrDefault(t => string.Equals(t.Config.Name, action.ChannelName, StringComparison.OrdinalIgnoreCase))
            : null;

        if (action.ChannelName.Length > 0 && named is null)
        {
            _rxHost.Log($"scripts: no channel named \"{action.ChannelName}\" — falling back to the primary");
        }

        var tab = named ?? Tabs.OfType<ChannelTabViewModel>().FirstOrDefault(t => t.Config.Role == ChannelRole.Primary)
                        ?? Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
        return (tab?.Config, 0xFFFFFFFFu, tab?.Messages);
    }
}
