// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The geofence: trigger — what it matches, what it refuses, and the two rules
/// that only apply to it: a fence is named rather than identified, and our own
/// crossings count.
/// </summary>
public class ScriptGeofenceTriggerTests
{
    private const uint Me = 0x11111111;
    private const uint Peer = 0xa1b2c3d4;

    private static readonly ScriptSelf Self = new(Me, "ME", "My Node", 101);
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static ScriptEngine Engine(params string[] yaml)
    {
        var engine = new ScriptEngine();
        var files = yaml.Select((text, i) =>
        {
            var parse = ScriptParser.Parse(text);
            Assert.True(parse.IsValid, $"script {i} did not parse: {parse.FirstError}");
            return new ScriptFile($"s{i}.yaml", Path.Combine("x", $"s{i}.yaml"), text, Enabled: true, parse);
        });
        engine.Load(files, Noon);
        return engine;
    }

    /// <summary>A crossing caused by somebody else's position packet, which is
    /// what the receive path builds. Ours carries no packet — see
    /// <see cref="OurOwnCrossing"/>.</summary>
    private static ScriptEvent Crossing(
        string fence, bool entered = true, uint from = Peer, string channel = "LongFast",
        DateTimeOffset? at = null) =>
        new()
        {
            Kind = ScriptEventKind.Geofence,
            GeofenceName = fence,
            GeofenceEntered = entered,
            FromNode = from,
            FromShort = "PEER",
            FromLong = "Peer Node",
            FromLatitude = 37.5,
            FromLongitude = -122.0,
            Channel = channel,
            FromPacket = true,
            SnrDb = 5,
            RssiDbm = -104,
            Hops = 2,
            Self = Self,
            At = at ?? Noon,
        };

    /// <summary>Our own crossing, from a position this node sent. Nothing
    /// decodes our own transmissions back, so there is no packet behind it and
    /// no signal to report.</summary>
    private static ScriptEvent OurOwnCrossing(string fence, bool entered = true) =>
        Crossing(fence, entered, from: Me) with
        {
            FromShort = "ME",
            FromLong = "My Node",
            FromPacket = false,
            SnrDb = null,
            RssiDbm = null,
            Hops = 0,
        };

    private const string GateScript =
        """
        trigger:
          - geofence: "North Gate"
        action:
          - send:
              to: "!a1b2c3d4"
              text: "{from.short} {geofence.event} {geofence}"
        """;

    [Fact]
    public void A_Crossing_Fires_The_Script_That_Names_The_Fence()
    {
        var run = Assert.Single(Engine(GateScript).Evaluate(Crossing("North Gate")));
        var action = Assert.Single(run.Actions);

        Assert.Equal(ScriptActionKind.Send, action.Kind);
        Assert.Equal("PEER entered North Gate", run.Expansion.ExpandMessage(action.Text));
        // No packet behind a crossing, so nothing to thread under.
        Assert.Equal(0u, action.ReplyId);
    }

    [Fact]
    public void A_Fence_Is_Matched_By_Name_Without_Case()
    {
        Assert.Single(Engine(GateScript).Evaluate(Crossing("NORTH GATE")));
        Assert.Empty(Engine(GateScript).Evaluate(Crossing("South Gate")));
    }

    /// <summary>Enter is the default, so a script that says nothing about
    /// direction does not answer departures as well.</summary>
    [Fact]
    public void Enter_Is_The_Default_Direction()
    {
        Assert.Single(Engine(GateScript).Evaluate(Crossing("North Gate", entered: true)));
        Assert.Empty(Engine(GateScript).Evaluate(Crossing("North Gate", entered: false)));
    }

    [Fact]
    public void An_Exit_Trigger_Answers_Only_Departures()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: "North Gate"
                on: exit
            action:
              - log: "{from.short} {geofence.event}"
            """);

        Assert.Empty(engine.Evaluate(Crossing("North Gate", entered: true)));
        var run = Assert.Single(engine.Evaluate(Crossing("North Gate", entered: false)));
        Assert.Equal("PEER exited", run.Expansion.Expand(run.Actions[0].Text));
    }

    [Fact]
    public void Both_Answers_Either_Direction()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
                on: both
            action:
              - log: "{geofence} {geofence.event}"
            """);

        // Far enough apart that the default per-node cooldown is not what is
        // under test here.
        Assert.Equal("North Gate entered",
            Expand(engine.Evaluate(Crossing("North Gate", entered: true))));
        Assert.Equal("Back Field exited",
            Expand(engine.Evaluate(Crossing("Back Field", entered: false, at: Noon.AddHours(1)))));

        static string Expand(IReadOnlyList<ScriptRun> runs)
        {
            var run = Assert.Single(runs);
            return run.Expansion.Expand(run.Actions[0].Text);
        }
    }

    /// <summary>
    /// The one place the never-answer-ourselves guard is lifted. A crossing is
    /// caused by where this node is, not by anything it said, so it cannot feed
    /// back — and a fence round home is watched precisely to report us.
    /// </summary>
    [Fact]
    public void Our_Own_Crossing_Fires()
    {
        Assert.Single(Engine(GateScript).Evaluate(OurOwnCrossing("North Gate")));
        // Everything else from us is still ignored.
        Assert.Empty(Engine(GateScript).Evaluate(
            OurOwnCrossing("North Gate") with { Kind = ScriptEventKind.Text }));
    }

    /// <summary>Somebody else's crossing came out of their position packet, so
    /// the packet's signal is there to quote and to filter on.</summary>
    [Fact]
    public void A_Peers_Crossing_Carries_The_Position_Packets_Signal()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            condition:
              - hops_below: 3
            action:
              - log: "{from.short} at {snr} dB over {hops} hops"
            """);

        var run = Assert.Single(engine.Evaluate(Crossing("North Gate")));
        Assert.Equal("PEER at 5 dB over 2 hops", run.Expansion.Expand(run.Actions[0].Text));
    }

    /// <summary>Our own carries none of it, so a script that filters on signal
    /// fails closed on us rather than firing on a default of zero.</summary>
    [Fact]
    public void Signal_Conditions_Fail_Closed_On_Our_Own_Crossing()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            condition:
              - hops_below: 3
            action:
              - log: "here"
            """);

        Assert.Empty(engine.Evaluate(OurOwnCrossing("North Gate")));

        var snr = Engine(
            """
            trigger:
              - geofence: any
            condition:
              - snr_above: -12
            action:
              - log: "here"
            """);

        Assert.Single(snr.Evaluate(Crossing("North Gate")));
        Assert.Empty(snr.Evaluate(OurOwnCrossing("North Gate")));
    }

    /// <summary>
    /// The greeting case: someone arrives, and the script direct-messages them.
    /// A crossing has no sender in the packet sense, but it does name the node
    /// that crossed, so {from.id} resolves and the answer goes to them.
    /// </summary>
    [Fact]
    public void A_Welcome_Goes_To_Whoever_Arrived()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: "North Gate"
            condition:
              - not_from: ["{my.id}"]
            action:
              - send:
                  to: "{from.id}"
                  text: "Welcome to {geofence}, {from.short}"
            """);

        var run = Assert.Single(engine.Evaluate(Crossing("North Gate")));
        var action = Assert.Single(run.Actions);

        Assert.Equal(Peer, action.ToNode);
        Assert.Equal("Welcome to North Gate, PEER", run.Expansion.ExpandMessage(action.Text));

        // And walking into your own fence does not greet you.
        Assert.Empty(engine.Evaluate(OurOwnCrossing("North Gate")));
    }

    /// <summary>{my.id} is resolved against this node rather than parsed as an
    /// id, so a from:/not_from: naming it survives a renumbering.</summary>
    [Fact]
    public void The_Self_Token_Names_This_Node_In_A_From_Condition()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            condition:
              - from: ["{my.id}"]
            action:
              - log: "home"
            """);

        Assert.Single(engine.Evaluate(OurOwnCrossing("North Gate")));
        Assert.Empty(engine.Evaluate(Crossing("North Gate")));
    }

    [Fact]
    public void A_From_Condition_Still_Narrows_A_Crossing()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            condition:
              - from: ["!a1b2c3d4"]
            action:
              - log: "here"
            """);

        Assert.Single(engine.Evaluate(Crossing("North Gate", from: Peer)));
        Assert.Empty(engine.Evaluate(Crossing("North Gate", from: 0x00000042)));
    }

    /// <summary>What the crossing detector asks before it bothers tracking a
    /// fence whose waypoint wants no chime of its own.</summary>
    [Fact]
    public void The_Detector_Can_Ask_Which_Fences_Are_Watched()
    {
        var engine = Engine(GateScript);

        Assert.True(engine.WatchesGeofence("North Gate"));
        Assert.True(engine.WatchesGeofence("north gate"));
        Assert.False(engine.WatchesGeofence("Back Field"));

        // "any" watches every fence there is.
        var anywhere = Engine(
            """
            trigger:
              - geofence: any
            action:
              - log: "here"
            """);
        Assert.True(anywhere.WatchesGeofence("Back Field"));

        // And a mesh with no geofence script watches nothing.
        var unrelated = Engine(
            """
            trigger:
              - command: ping
            action:
              - reply: "pong"
            """);
        Assert.False(unrelated.WatchesGeofence("North Gate"));
    }

    /// <summary>The packet behind a crossing is a position, not a message, so
    /// there is nothing a client would render a reply threaded under — which is
    /// why the event carries no packet id even when a packet caused it.</summary>
    [Fact]
    public void Nothing_Threads_Under_A_Crossing_Even_When_A_Packet_Caused_It()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            action:
              - send:
                  channel: LongFast
                  text: "{from.short} arrived"
                  reply_link: true
            """);

        var action = Assert.Single(Assert.Single(engine.Evaluate(Crossing("North Gate"))).Actions);
        Assert.Equal(0u, action.ReplyId);
    }

    [Fact]
    public void A_React_Is_Skipped_Because_There_Is_Nothing_To_React_To()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            action:
              - react: 👍
              - log: "still ran"
            """);

        var run = Assert.Single(engine.Evaluate(Crossing("North Gate")));
        Assert.Equal(ScriptActionKind.Log, Assert.Single(run.Actions).Kind);
    }

    /// <summary>A reply: has no message to thread under, so it posts to the
    /// fence's own channel — the same place the crossing note goes.</summary>
    [Fact]
    public void A_Reply_Goes_To_The_Fences_Channel()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            action:
              - reply: "{from.short} arrived"
            """);

        var action = Assert.Single(Assert.Single(engine.Evaluate(Crossing("North Gate"))).Actions);
        Assert.Equal(0u, action.ToNode);
        Assert.Equal("LongFast", action.ChannelName);
        Assert.Equal(0u, action.ReplyId);
    }

    // ----- reaching somebody whose key we do not have --------------------------

    /// <summary>
    /// The full greeting flow: ask for their NodeInfo, wait for the answer,
    /// then send only if it produced a key. Without one the DM would be
    /// transmitted and binned at the far end — firmware rejects a text message
    /// addressed to it that decrypted with the channel key.
    /// </summary>
    [Fact]
    public void A_Welcome_Can_Ask_For_A_Key_Before_It_Sends()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: "North Gate"
            action:
              - nodeinfo:
                  request: true
              - delay: 45s
              - send:
                  to: "{from.id}"
                  text: "Welcome to {geofence}"
                  require_key: true
            """);

        var run = Assert.Single(engine.Evaluate(Crossing("North Gate")));
        Assert.Equal(3, run.Actions.Count);

        // The request is aimed at whoever crossed, not broadcast.
        Assert.Equal(ScriptActionKind.RequestNodeInfo, run.Actions[0].Kind);
        Assert.Equal(Peer, run.Actions[0].ToNode);

        Assert.Equal(TimeSpan.FromSeconds(45), run.Actions[1].Delay);

        Assert.True(run.Actions[2].RequireKey);
        Assert.Equal(Peer, run.Actions[2].ToNode);
    }

    /// <summary>Asking for a key is still airtime, so it counts against the
    /// budget the way any other transmission does.</summary>
    [Fact]
    public void Asking_For_A_Key_Counts_As_Airtime()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            action:
              - nodeinfo:
                  request: true
            """);

        Assert.True(Assert.Single(engine.Evaluate(Crossing("North Gate"))).Transmits);
    }

    [Fact]
    public void Require_Key_Defaults_Off_So_An_Ordinary_Send_Is_Unchanged()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            action:
              - send:
                  to: "{from.id}"
                  text: "hello"
            """);

        Assert.False(Assert.Single(Assert.Single(engine.Evaluate(Crossing("North Gate"))).Actions).RequireKey);
    }

    /// <summary>A channel message is encrypted with a key everyone on the
    /// channel already holds, so there is nothing for require_key: to mean.</summary>
    [Fact]
    public void Require_Key_On_A_Channel_Send_Is_An_Error()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - geofence: any
            action:
              - send:
                  channel: LongFast
                  text: "hello"
                  require_key: true
            """);

        Assert.False(result.IsValid);
        Assert.Contains("require_key:", result.FirstError?.Message);
    }

    /// <summary>The scalar form still advertises ours, so existing scripts do
    /// not quietly start asking instead.</summary>
    [Fact]
    public void The_Scalar_Nodeinfo_Form_Still_Sends_Ours()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: hi
            action:
              - nodeinfo: true
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Equal(ScriptActionKind.NodeInfo, Assert.Single(result.Script!.Actions).Kind);
    }

    [Fact]
    public void A_Nodeinfo_Mapping_Without_Request_Says_What_To_Write()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: hi
            action:
              - nodeinfo:
                  request: false
            """);

        Assert.False(result.IsValid);
        Assert.Contains("request: true", result.FirstError?.Message);
    }

    /// <summary>
    /// Asking costs airtime, so a script should only ask when the answer would
    /// tell it something. {from.has_key} comes off the crossing snapshot, so
    /// the gate gets the key state as it was when they arrived — and the wait
    /// is gated with it, or a script would idle 45s for somebody it already
    /// knows.
    /// </summary>
    [Fact]
    public void The_Request_And_Its_Wait_Are_Skipped_When_The_Key_Is_Already_On_File()
    {
        var engine = Engine(
            """
            trigger:
              - geofence: any
            action:
              - nodeinfo:
                  request: true
                when:
                  value: "{from.has_key}"
                  equals: false
              - delay: 45s
                when:
                  value: "{from.has_key}"
                  equals: false
              - send:
                  to: "{from.id}"
                  text: "Welcome to {geofence}"
                  require_key: true
            """);

        // A stranger: ask, wait, then send if the answer produced a key.
        var stranger = Assert.Single(engine.Evaluate(Crossing("North Gate")));
        Assert.Equal(
            [ScriptActionKind.RequestNodeInfo, ScriptActionKind.Delay, ScriptActionKind.Send],
            stranger.Actions.Select(a => a.Kind));
        Assert.All(stranger.Actions.Take(2), a => Assert.True(Gate(a, stranger)));

        // Somebody already on file: both gates close, and only the send runs.
        // An hour on, so the per-node cooldown is not what is being measured.
        var known = Assert.Single(engine.Evaluate(
            Crossing("Back Field", at: Noon.AddHours(1)) with { SenderHasKey = true }));
        Assert.False(Gate(known.Actions[0], known));
        Assert.False(Gate(known.Actions[1], known));
        Assert.True(Gate(known.Actions[2], known));

        static bool Gate(ResolvedAction action, ScriptRun run) =>
            action.When is null || action.When.Holds(run.Expansion, out _);
    }

    // ----- parsing ------------------------------------------------------------

    [Fact]
    public void An_Unnamed_Fence_Is_An_Error()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - geofence: ""
            action:
              - log: "here"
            """);

        Assert.False(result.IsValid);
        Assert.Contains("geofence:", result.FirstError?.Message);
    }

    [Fact]
    public void A_Direction_That_Is_Not_A_Direction_Is_An_Error()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - geofence: "North Gate"
                on: sideways
            action:
              - log: "here"
            """);

        Assert.False(result.IsValid);
        Assert.Contains("enter, exit or both", result.FirstError?.Message);
    }

    /// <summary>"on" is a YAML 1.1 boolean in some readers. This one keeps it a
    /// key, and the trigger depends on that.</summary>
    [Fact]
    public void The_On_Key_Survives_Being_Written_Unquoted()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - geofence: "North Gate"
                on: both
            action:
              - log: "here"
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        var trigger = Assert.Single(result.Script!.Triggers);
        Assert.Equal(ScriptGeofenceCrossing.Both, trigger.Crossing);
        Assert.Equal("North Gate", trigger.Pattern);
    }

    [Fact]
    public void Any_Parses_To_The_Match_Everything_Form()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - geofence: ANY
            action:
              - log: "here"
            """);

        Assert.True(result.IsValid, result.FirstError?.ToString());
        Assert.Equal(string.Empty, Assert.Single(result.Script!.Triggers).Pattern);
    }
}
