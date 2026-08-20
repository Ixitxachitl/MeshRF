// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

public class ScriptEngineTests
{
    private const uint Me = 0x11111111;
    private const uint Peer = 0xa1b2c3d4;

    private static readonly ScriptSelf Self = new(Me, "ME", "My Node", 101);
    private static readonly DateTimeOffset Noon =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Loads one script straight from YAML, as if the library had read
    /// it off disk and found it enabled.</summary>
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

    private static ScriptEvent Text(string body, bool direct = true, string channel = "LongFast",
        double? snr = 5, int hops = 0, uint from = Peer) =>
        new()
        {
            Kind = ScriptEventKind.Text,
            Text = body,
            FromNode = from,
            FromShort = "PEER",
            FromLong = "Peer Node",
            Channel = channel,
            IsDirect = direct,
            SnrDb = snr,
            RssiDbm = -104,
            Hops = hops,
            PacketId = 0xdeadbeef,
            Self = Self,
            At = Noon,
        };

    [Fact]
    public void A_Command_Fires_And_Resolves_Its_Reply()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
            action:
              - reply: "pong — {snr} dB from {my.short}"
            """);

        var run = Assert.Single(engine.Evaluate(Text("!ping")));
        var action = Assert.Single(run.Actions);

        Assert.Equal(ScriptActionKind.Reply, action.Kind);
        Assert.Equal("pong — 5 dB from ME", run.Expansion.ExpandMessage(action.Text));
        Assert.Equal(Peer, action.ToNode);
        Assert.Equal(0xdeadbeefu, action.ReplyId);
    }

    [Fact]
    public void A_Command_Needs_The_Bang_And_The_Whole_Word()
    {
        // Limits out of the way: this test is about matching. Each call gets
        // its own timestamp, since a cooldown quite correctly blocks a second
        // firing at the very same instant.
        var engine = Engine(
            "trigger:\n  - command: ping\naction:\n  - reply: \"pong\"\n" +
            "limits:\n  cooldown: 1s\n  per_node: false\n  max_per_hour: 100\n");

        int step = 0;
        ScriptEvent Next(string body) => Text(body) with { At = Noon.AddSeconds(++step * 10) };

        Assert.Single(engine.Evaluate(Next("!ping")));
        Assert.Single(engine.Evaluate(Next("!PING")));          // case-insensitive
        Assert.Single(engine.Evaluate(Next("  !ping now")));    // leading space, trailing args
        Assert.Empty(engine.Evaluate(Next("ping")));            // no bang
        Assert.Empty(engine.Evaluate(Next("!pinger")));         // not the whole word
        Assert.Empty(engine.Evaluate(Next("say !ping")));       // not at the start
    }

    [Fact]
    public void Command_Arguments_Become_Placeholders()
    {
        var engine = Engine(
            "trigger:\n  - command: echo\naction:\n  - reply: \"{arg1}|{arg2}|{args}\"\n");

        var run = Assert.Single(engine.Evaluate(Text("!echo alpha beta gamma")));
        Assert.Equal("alpha|beta|alpha beta gamma", run.Expansion.ExpandMessage(run.Actions[0].Text));
    }

    [Fact]
    public void Regex_Captures_Become_Placeholders()
    {
        var engine = Engine(
            "trigger:\n  - text: \"^!wx (\\\\w+)$\"\naction:\n  - reply: \"weather for {cap1}\"\n");

        var run = Assert.Single(engine.Evaluate(Text("!wx london")));
        Assert.Equal("weather for london", run.Expansion.ExpandMessage(run.Actions[0].Text));
    }

    [Fact]
    public void A_Channel_Message_Is_Answered_On_Its_Channel_Not_As_A_DM()
    {
        var engine = Engine("trigger:\n  - command: ping\naction:\n  - reply: \"pong\"\n");

        var run = Assert.Single(engine.Evaluate(Text("!ping", direct: false, channel: "Backup")));
        Assert.Equal(0u, run.Actions[0].ToNode);          // broadcast
        Assert.Equal("Backup", run.Actions[0].ChannelName);
    }

    // ----- conditions ---------------------------------------------------------

    [Fact]
    public void Scope_Direct_Ignores_Channel_Traffic()
    {
        var engine = Engine(
            "trigger:\n  - command: ping\ncondition:\n  - scope: direct\naction:\n  - reply: \"pong\"\n");

        Assert.Single(engine.Evaluate(Text("!ping", direct: true)));
        Assert.Empty(engine.Evaluate(Text("!ping", direct: false)));
    }

    [Fact]
    public void Not_Channel_Answers_Everywhere_Except_The_Named_Ones()
    {
        var engine = Engine(
            "trigger:\n  - command: ping\ncondition:\n  - not_channel: [Test, Backup]\naction:\n  - reply: \"pong\"\n");

        Assert.Single(engine.Evaluate(Text("!ping", direct: false, channel: "LongFast")));
        Assert.Empty(engine.Evaluate(Text("!ping", direct: false, channel: "Test")));
        // Case-insensitive, like channel:, since the name is matched against
        // whatever was typed into channel Settings.
        Assert.Empty(engine.Evaluate(Text("!ping", direct: false, channel: "backup")));
    }

    [Fact]
    public void The_Primary_Token_Names_The_Primary_By_Role_In_A_Condition()
    {
        var engine = Engine(
            "trigger:\n  - command: ping\ncondition:\n  - channel: \"{primary}\"\naction:\n  - reply: \"pong\"\n");

        // Whatever the primary happens to be called.
        Assert.Single(engine.Evaluate(
            Text("!ping", direct: false, channel: "LongFast") with { IsPrimaryChannel = true }));
        Assert.Empty(engine.Evaluate(
            Text("!ping", direct: false, channel: "Test") with { IsPrimaryChannel = false }));
    }

    [Fact]
    public void A_Channel_List_May_Mix_The_Token_With_Names()
    {
        // The thing scope: cannot express at all: the primary plus a named
        // channel, in one condition.
        const string yaml =
            "trigger:\n  - command: ping\ncondition:\n  - channel: [\"{primary}\", Backup]\naction:\n  - reply: \"p\"\n";

        Assert.Single(Engine(yaml).Evaluate(
            Text("!ping", direct: false, channel: "LongFast") with { IsPrimaryChannel = true }));
        Assert.Single(Engine(yaml).Evaluate(
            Text("!ping", direct: false, channel: "Backup") with { IsPrimaryChannel = false }));
        Assert.Empty(Engine(yaml).Evaluate(
            Text("!ping", direct: false, channel: "Test") with { IsPrimaryChannel = false }));
    }

    [Fact]
    public void Not_Channel_Excludes_The_Primary_By_Role_Too()
    {
        var engine = Engine(
            "trigger:\n  - command: ping\ncondition:\n  - not_channel: \"{primary}\"\naction:\n  - reply: \"p\"\n");

        Assert.Empty(engine.Evaluate(
            Text("!ping", direct: false, channel: "LongFast") with { IsPrimaryChannel = true }));
        Assert.Single(engine.Evaluate(
            Text("!ping", direct: false, channel: "Test") with { IsPrimaryChannel = false }));
    }

    [Fact]
    public void Not_Channel_Is_Vacuously_True_Off_Channel()
    {
        // A direct message arrives on no channel at all, so it is not on the
        // excluded one — the same way not_from: holds for a timer with no
        // sender. channel: never matches a DM either, so the two stay each
        // other's inverse rather than both refusing it.
        var engine = Engine(
            "trigger:\n  - command: ping\ncondition:\n  - not_channel: [Test]\naction:\n  - reply: \"pong\"\n");

        Assert.Single(engine.Evaluate(Text("!ping", direct: true, channel: "")));
    }

    [Fact]
    public void Channel_And_Not_Channel_Are_Inverses_On_Channel_Traffic()
    {
        // A fresh engine per case: one script answering the same node three
        // times in a row would be held back by its own per-node cooldown, and
        // that would look like the condition deciding.
        foreach (var channel in new[] { "Test", "LongFast", "Backup" })
        {
            var evt = Text("!ping", direct: false, channel: channel);

            bool matchedOnly = Engine(
                "trigger:\n  - command: ping\ncondition:\n  - channel: [Test]\naction:\n  - reply: \"p\"\n")
                .Evaluate(evt).Count == 1;
            bool matchedExcept = Engine(
                "trigger:\n  - command: ping\ncondition:\n  - not_channel: [Test]\naction:\n  - reply: \"p\"\n")
                .Evaluate(evt).Count == 1;

            Assert.NotEqual(matchedOnly, matchedExcept);
        }
    }

    [Fact]
    public void Snr_Threshold_Fails_Closed_When_The_Packet_Carried_None()
    {
        var engine = Engine(
            "trigger:\n  - command: ping\ncondition:\n  - snr_above: 0\naction:\n  - reply: \"pong\"\n");

        Assert.Single(engine.Evaluate(Text("!ping", snr: 5)));
        Assert.Empty(engine.Evaluate(Text("!ping", snr: -5)));
        // The safe direction for something that keys up a transmitter.
        Assert.Empty(engine.Evaluate(Text("!ping", snr: null)));
    }

    [Fact]
    public void Every_Condition_Has_To_Hold()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
            condition:
              - scope: direct
              - hops_below: 2
              - channel: [LongFast]
            action:
              - reply: "pong"
            """);

        Assert.Single(engine.Evaluate(Text("!ping", hops: 1)));
        Assert.Empty(engine.Evaluate(Text("!ping", hops: 3)));
        Assert.Empty(engine.Evaluate(Text("!ping", channel: "Other")));
    }

    [Fact]
    public void From_And_NotFrom_Select_Senders()
    {
        var allow = Engine("trigger:\n  - command: p\ncondition:\n  - from: [\"!a1b2c3d4\"]\naction:\n  - reply: \"x\"\n");
        Assert.Single(allow.Evaluate(Text("!p", from: Peer)));
        Assert.Empty(allow.Evaluate(Text("!p", from: 0x99999999)));

        var deny = Engine("trigger:\n  - command: p\ncondition:\n  - not_from: [\"!a1b2c3d4\"]\naction:\n  - reply: \"x\"\n");
        Assert.Empty(deny.Evaluate(Text("!p", from: Peer)));
        Assert.Single(deny.Evaluate(Text("!p", from: 0x99999999)));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(12, true)]
    [InlineData(21, true)]
    [InlineData(23, false)]
    public void Between_Bounds_A_Daytime_Window(int hour, bool expected)
    {
        var engine = Engine(
            "trigger:\n  - command: p\ncondition:\n  - between: \"10:00-22:00\"\naction:\n  - reply: \"x\"\n");

        var evt = Text("!p") with { At = new DateTimeOffset(2026, 8, 14, hour, 0, 0, TimeSpan.Zero) };
        Assert.Equal(expected, engine.Evaluate(evt).Count == 1);
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(3, true)]
    [InlineData(12, false)]
    public void Between_Wraps_Past_Midnight(int hour, bool expected)
    {
        var engine = Engine(
            "trigger:\n  - command: p\ncondition:\n  - between: \"22:00-06:00\"\naction:\n  - reply: \"x\"\n");

        var evt = Text("!p") with { At = new DateTimeOffset(2026, 8, 14, hour, 0, 0, TimeSpan.Zero) };
        Assert.Equal(expected, engine.Evaluate(evt).Count == 1);
    }

    // ----- loop and safety guards ---------------------------------------------

    [Fact]
    public void Our_Own_Traffic_Never_Triggers_Anything()
    {
        var engine = Engine("trigger:\n  - command: ping\naction:\n  - reply: \"pong\"\n");

        // The guard that makes a two-node feedback loop impossible rather than
        // merely unlikely.
        Assert.Empty(engine.Evaluate(Text("!ping", from: Me)));
    }

    [Fact]
    public void A_Disabled_Script_Is_Not_Armed()
    {
        var engine = new ScriptEngine();
        const string yaml = "trigger:\n  - command: ping\naction:\n  - reply: \"pong\"\n";
        engine.Load(
            [new ScriptFile("a.yaml", "a.yaml", yaml, Enabled: false, ScriptParser.Parse(yaml))],
            Noon);

        Assert.Equal(0, engine.ArmedCount);
        Assert.Empty(engine.Evaluate(Text("!ping")));
    }

    [Fact]
    public void A_Broken_Script_Is_Not_Armed()
    {
        var engine = new ScriptEngine();
        const string yaml = "triggers:\n  - relpy: nope\n";
        engine.Load(
            [new ScriptFile("a.yaml", "a.yaml", yaml, Enabled: true, ScriptParser.Parse(yaml))],
            Noon);

        Assert.Equal(0, engine.ArmedCount);
    }

    [Fact]
    public void Scripts_Fire_In_The_Order_They_Are_Listed()
    {
        var engine = Engine(
            "alias: first\ntrigger:\n  - command: p\naction:\n  - reply: \"1\"\nlimits:\n  cooldown: 0.001s\n",
            "alias: second\ntrigger:\n  - command: p\naction:\n  - reply: \"2\"\nlimits:\n  cooldown: 0.001s\n");

        var runs = engine.Evaluate(Text("!p"));
        Assert.Equal(2, runs.Count);
        Assert.Equal("first", runs[0].Alias);
        Assert.Equal("second", runs[1].Alias);
    }

    // ----- limits -------------------------------------------------------------

    [Fact]
    public void The_Cooldown_Holds_A_Script_Back()
    {
        var engine = Engine(
            "trigger:\n  - command: p\naction:\n  - reply: \"x\"\nlimits:\n  cooldown: 60s\n  per_node: false\n");

        Assert.Single(engine.Evaluate(Text("!p") with { At = Noon }));
        Assert.Empty(engine.Evaluate(Text("!p") with { At = Noon.AddSeconds(30) }));
        Assert.Single(engine.Evaluate(Text("!p") with { At = Noon.AddSeconds(61) }));
    }

    [Fact]
    public void A_Per_Node_Cooldown_Does_Not_Mute_The_Script_For_Everyone()
    {
        var engine = Engine(
            "trigger:\n  - command: p\naction:\n  - reply: \"x\"\nlimits:\n  cooldown: 60s\n  per_node: true\n");

        Assert.Single(engine.Evaluate(Text("!p", from: Peer) with { At = Noon }));
        // Same node, still cooling down.
        Assert.Empty(engine.Evaluate(Text("!p", from: Peer) with { At = Noon.AddSeconds(5) }));
        // A different node gets its own clock.
        Assert.Single(engine.Evaluate(Text("!p", from: 0x22222222) with { At = Noon.AddSeconds(5) }));
    }

    [Fact]
    public void The_Hourly_Cap_Applies()
    {
        var engine = Engine(
            "trigger:\n  - command: p\naction:\n  - reply: \"x\"\nlimits:\n  cooldown: 1s\n  per_node: false\n  max_per_hour: 3\n");

        for (int i = 0; i < 3; i++)
            Assert.Single(engine.Evaluate(Text("!p") with { At = Noon.AddMinutes(i * 5) }));

        Assert.Empty(engine.Evaluate(Text("!p") with { At = Noon.AddMinutes(20) }));
        // The window rolls, so an hour after the first it is allowed again.
        Assert.Single(engine.Evaluate(Text("!p") with { At = Noon.AddMinutes(61) }));
    }

    [Fact]
    public void The_Global_Budget_Outranks_A_Scripts_Own_Generous_Limits()
    {
        // The point of the global budget: a script cannot raise it from inside
        // its own file.
        var engine = Engine(
            "trigger:\n  - command: p\naction:\n  - reply: \"x\"\nlimits:\n  cooldown: 0.001s\n  per_node: false\n  max_per_hour: 1000\n");
        engine.Limiter.GlobalMaxPerHour = 5;

        int fired = 0;
        for (int i = 0; i < 20; i++)
            fired += engine.Evaluate(Text("!p") with { At = Noon.AddSeconds(i) }).Count;

        Assert.Equal(5, fired);
    }

    [Fact]
    public void A_Log_Only_Script_Does_Not_Spend_Airtime_Budget()
    {
        var engine = Engine(
            "alias: quiet\ntrigger:\n  - command: p\naction:\n  - log: \"saw a ping\"\nlimits:\n  cooldown: 0.001s\n  per_node: false\n  max_per_hour: 1000\n");
        engine.Limiter.GlobalMaxPerHour = 3;

        int fired = 0;
        for (int i = 0; i < 10; i++)
            fired += engine.Evaluate(Text("!p") with { At = Noon.AddSeconds(i) }).Count;

        // Transmits nothing, so the budget that rations the channel is untouched.
        Assert.Equal(10, fired);
        Assert.Equal(0, engine.Limiter.FiredInLastHour(Noon.AddSeconds(10)));
    }

    [Fact]
    public void Reloading_Clears_Cooldowns()
    {
        const string yaml = "trigger:\n  - command: p\naction:\n  - reply: \"x\"\nlimits:\n  cooldown: 1h\n";
        var engine = Engine(yaml);
        Assert.Single(engine.Evaluate(Text("!p")));
        Assert.Empty(engine.Evaluate(Text("!p")));

        // A user who has just finished editing expects to be able to test.
        engine.Load(
            [new ScriptFile("s0.yaml", "s0.yaml", yaml, Enabled: true, ScriptParser.Parse(yaml))],
            Noon);
        Assert.Single(engine.Evaluate(Text("!p")));
    }

    // ----- scheduled triggers -------------------------------------------------

    [Fact]
    public void An_Every_Trigger_Does_Not_Fire_The_Instant_Scripts_Load()
    {
        var engine = Engine(
            "trigger:\n  - every: 1h\naction:\n  - send:\n      channel: LongFast\n      text: \"beacon\"\n");

        // Otherwise every enabled beacon transmits at once on startup.
        Assert.Empty(engine.Tick(Noon, Self));
        Assert.Empty(engine.Tick(Noon.AddMinutes(59), Self));
        Assert.Single(engine.Tick(Noon.AddMinutes(61), Self));
    }

    [Fact]
    public void A_Long_Sleep_Produces_One_Catch_Up_Firing_Not_Many()
    {
        var engine = Engine(
            "trigger:\n  - every: 1h\naction:\n  - send:\n      channel: LongFast\n      text: \"beacon\"\nlimits:\n  max_per_hour: 100\n");

        // Laptop slept for six hours; six back-to-back beacons would be rude.
        Assert.Single(engine.Tick(Noon.AddHours(6), Self));
        Assert.Empty(engine.Tick(Noon.AddHours(6).AddMinutes(1), Self));
    }

    [Fact]
    public void An_At_Trigger_Fires_Once_A_Day()
    {
        var engine = Engine(
            "trigger:\n  - at: \"18:00\"\naction:\n  - send:\n      channel: LongFast\n      text: \"evening\"\nlimits:\n  max_per_hour: 100\n");

        Assert.Empty(engine.Tick(Noon, Self));
        Assert.Single(engine.Tick(Noon.AddHours(6).AddMinutes(1), Self));
        Assert.Empty(engine.Tick(Noon.AddHours(7), Self));
        Assert.Single(engine.Tick(Noon.AddDays(1).AddHours(6).AddMinutes(1), Self));
    }

    [Fact]
    public void A_Timer_Event_Skips_Actions_That_Need_A_Sender()
    {
        var engine = Engine(
            """
            trigger:
              - every: 1m
            action:
              - reply: "nobody to reply to"
              - send:
                  channel: LongFast
                  text: "but this is fine"
            """);

        var run = Assert.Single(engine.Tick(Noon.AddMinutes(2), Self));
        var action = Assert.Single(run.Actions);
        Assert.Equal(ScriptActionKind.Send, action.Kind);
    }

    [Fact]
    public void Conditions_Needing_A_Sender_Fail_Closed_On_A_Timer()
    {
        var engine = Engine(
            "trigger:\n  - every: 1m\ncondition:\n  - scope: direct\naction:\n  - send:\n      channel: L\n      text: \"x\"\n");

        Assert.Empty(engine.Tick(Noon.AddMinutes(2), Self));
    }

    // ----- reactions and new nodes --------------------------------------------

    [Fact]
    public void A_Reaction_Trigger_Can_Match_One_Emoji_Or_Any()
    {
        var specific = Engine("trigger:\n  - reaction: \"👍\"\naction:\n  - log: \"thumbed\"\n");
        var any = Engine("trigger:\n  - reaction: any\naction:\n  - log: \"reacted\"\n");

        var thumb = Text("👍") with { Kind = ScriptEventKind.Reaction, Emoji = "👍" };
        var heart = Text("❤") with { Kind = ScriptEventKind.Reaction, Emoji = "❤" };

        Assert.Single(specific.Evaluate(thumb));
        Assert.Empty(specific.Evaluate(heart));
        Assert.Single(any.Evaluate(heart));
    }

    [Fact]
    public void A_Text_Trigger_Does_Not_Fire_On_A_Reaction()
    {
        var engine = Engine("trigger:\n  - text: \".\"\naction:\n  - log: \"x\"\n");

        Assert.Empty(engine.Evaluate(Text("👍") with { Kind = ScriptEventKind.Reaction, Emoji = "👍" }));
        Assert.Empty(engine.Evaluate(Text("hello") with { Kind = ScriptEventKind.NewNode }));
        Assert.Single(engine.Evaluate(Text("hello")));
    }

    [Fact]
    public void A_New_Node_Trigger_Resolves_Its_Send()
    {
        var engine = Engine(
            """
            trigger:
              - new_node: true
            action:
              - delay: 30s
              - send:
                  to: "{from.id}"
                  text: "Welcome, {from.long}."
            """);

        var evt = new ScriptEvent
        {
            Kind = ScriptEventKind.NewNode,
            FromNode = Peer,
            FromLong = "Peer Node",
            Self = Self,
            At = Noon,
        };

        var run = Assert.Single(engine.Evaluate(evt));
        Assert.Equal(TimeSpan.FromSeconds(30), run.Actions[0].Delay);
        Assert.Equal(Peer, run.Actions[1].ToNode);
        Assert.Equal("Welcome, Peer Node.", run.Expansion.ExpandMessage(run.Actions[1].Text));
    }

    [Fact]
    public void A_Send_Whose_Placeholder_Is_Not_A_Node_Id_Is_Skipped_With_A_Reason()
    {
        var engine = Engine(
            "trigger:\n  - command: p\naction:\n  - send:\n      to: \"{arg1}\"\n      text: \"hi\"\n");

        string? diagnostic = null;
        engine.Diagnostic += line => diagnostic = line;

        // {arg1} expands to "banana", which is not a node id.
        Assert.Empty(engine.Evaluate(Text("!p banana")));
        Assert.NotNull(diagnostic);
        Assert.Contains("is not a node id", diagnostic);
    }

    [Fact]
    public void A_When_Gate_Rides_Along_To_Be_Decided_As_The_Sequence_Runs()
    {
        // Two answers, opposite gates: the pair is how a script chooses, since
        // a require: could only stop. Which one holds cannot be settled here —
        // a gate may read {http.*} from a fetch that has not happened yet — so
        // the engine carries both and the runner decides.
        var engine = Engine(
            """
            trigger:
              - command: p
            action:
              - reply: "heard you direct"
                when:
                  value: "{hops}"
                  equals: 0
              - reply: "{hops} hops out"
                when:
                  value: "{hops}"
                  above: 0
            """);

        var run = Assert.Single(engine.Evaluate(Text("!p", hops: 2)));
        Assert.Equal(2, run.Actions.Count);

        Assert.False(run.Actions[0].When!.Holds(run.Expansion, out var skipped));
        Assert.Contains("\"2\" equals \"0\"", skipped);

        Assert.True(run.Actions[1].When!.Holds(run.Expansion, out _));
        Assert.Equal("2 hops out", run.Expansion.ExpandMessage(run.Actions[1].Text));
    }

    [Fact]
    public void An_Overlong_Reply_Is_Clamped_To_What_The_Radio_Carries()
    {
        var engine = Engine(
            "trigger:\n  - command: p\naction:\n  - reply: \"{args}\"\n");

        var run = Assert.Single(engine.Evaluate(Text("!p " + new string('x', 400))));
        Assert.Equal(200, System.Text.Encoding.UTF8.GetByteCount(run.Expansion.ExpandMessage(run.Actions[0].Text)));
    }
}
