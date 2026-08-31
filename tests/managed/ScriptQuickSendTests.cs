// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// A quick_send trigger puts a button on the Quick send bar and fires when it
/// is pressed. The press is matched by label, so a button runs only the scripts
/// that asked for it, and it carries a destination but no sender.
/// </summary>
public class ScriptQuickSendTests
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

    private static ScriptEvent Press(string label, string channel = "LongFast",
                                     uint toNode = 0, bool primary = true) =>
        new()
        {
            Kind = ScriptEventKind.QuickSend,
            QuickSendName = label,
            ToNode = toNode,
            IsDirect = toNode != 0,
            Channel = toNode != 0 ? string.Empty : channel,
            IsPrimaryChannel = toNode == 0 && primary,
            Self = Self,
            At = Noon,
        };

    private const string PingButton = """
        trigger:
          - quick_send: Ping
        action:
          - send:
              text: "ping"
        """;

    // ----- parsing -----

    [Fact]
    public void ParsesButtonLabel()
    {
        var parse = ScriptParser.Parse(PingButton);

        Assert.True(parse.IsValid, parse.FirstError?.Message);
        var trigger = Assert.Single(parse.Script!.Triggers);
        Assert.Equal(ScriptTriggerKind.QuickSend, trigger.Kind);
        Assert.Equal("Ping", trigger.Pattern);
    }

    [Fact]
    public void DestinationDefaultsToAsking()
    {
        var parse = ScriptParser.Parse(PingButton);

        var trigger = Assert.Single(parse.Script!.Triggers);
        Assert.Equal(ScriptTrigger.QuickSendAsk, trigger.Destination);
        Assert.True(trigger.AsksForDestination);
    }

    [Fact]
    public void DestinationCanNameAChannel()
    {
        var parse = ScriptParser.Parse("""
            trigger:
              - quick_send: Ping
                to: Emergency
            action:
              - send:
                  text: "ping"
            """);

        Assert.True(parse.IsValid, parse.FirstError?.Message);
        var trigger = Assert.Single(parse.Script!.Triggers);
        Assert.Equal("Emergency", trigger.Destination);
        Assert.False(trigger.AsksForDestination);
    }

    [Fact]
    public void ButtonWithNoLabelIsRejected()
    {
        var parse = ScriptParser.Parse("""
            trigger:
              - quick_send: "  "
            action:
              - send:
                  text: "ping"
            """);

        Assert.False(parse.IsValid);
        Assert.Contains("quick_send", parse.FirstError!.Value.Message);
    }

    [Fact]
    public void UnknownTriggerOptionIsRejected()
    {
        var parse = ScriptParser.Parse("""
            trigger:
              - quick_send: Ping
                destination: Emergency
            action:
              - send:
                  text: "ping"
            """);

        Assert.False(parse.IsValid);
    }

    // ----- the bar -----

    [Fact]
    public void EngineListsTheButtonAScriptAsksFor()
    {
        var engine = Engine(PingButton);

        var button = Assert.Single(engine.QuickSendButtons);
        Assert.Equal("Ping", button.Label);
        Assert.True(button.Asks);
        Assert.Equal("s0.yaml", button.FileName);
    }

    [Fact]
    public void ScriptWithoutTheTriggerAddsNoButton()
    {
        var engine = Engine("""
            trigger:
              - command: ping
            action:
              - reply: "pong"
            """);

        Assert.Empty(engine.QuickSendButtons);
    }

    // ----- firing -----

    [Fact]
    public void PressingTheButtonRunsItsScript()
    {
        var engine = Engine(PingButton);

        var run = Assert.Single(engine.Evaluate(Press("Ping")));
        Assert.Equal("s0.yaml", run.ScriptName);
    }

    [Fact]
    public void PressingOneButtonLeavesAnotherAlone()
    {
        var engine = Engine(PingButton, """
            trigger:
              - quick_send: Weather
            action:
              - send:
                  text: "sunny"
            """);

        var run = Assert.Single(engine.Evaluate(Press("Weather")));
        Assert.Equal("s1.yaml", run.ScriptName);
    }

    [Fact]
    public void TwoScriptsSharingALabelBothRun()
    {
        var engine = Engine(PingButton, PingButton);

        Assert.Equal(2, engine.Evaluate(Press("Ping")).Count);
    }

    [Fact]
    public void AnEventThatIsNotAPressDoesNotFireTheButton()
    {
        var engine = Engine(PingButton);

        Assert.Empty(engine.Tick(Noon.AddDays(1), Self));
    }

    // ----- destination -----

    [Fact]
    public void UnaddressedSendGoesToTheChosenChannel()
    {
        var engine = Engine(PingButton);

        var run = Assert.Single(engine.Evaluate(Press("Ping", channel: "Emergency")));
        var action = Assert.Single(run.Actions);
        Assert.Equal(0u, action.ToNode);
        Assert.Equal("Emergency", action.ChannelName);
    }

    [Fact]
    public void UnaddressedSendGoesToTheChosenPeer()
    {
        var engine = Engine(PingButton);

        var run = Assert.Single(engine.Evaluate(Press("Ping", toNode: Peer)));
        var action = Assert.Single(run.Actions);
        Assert.Equal(Peer, action.ToNode);
    }

    [Fact]
    public void AnExplicitChannelStillWins()
    {
        var engine = Engine("""
            trigger:
              - quick_send: Ping
            action:
              - send:
                  channel: Fixed
                  text: "ping"
            """);

        var run = Assert.Single(engine.Evaluate(Press("Ping", channel: "Emergency")));
        Assert.Equal("Fixed", Assert.Single(run.Actions).ChannelName);
    }

    // ----- conditions -----

    [Theory]
    [InlineData("from: \"!a1b2c3d4\"")]
    [InlineData("snr_above: 0")]
    [InlineData("hops_below: 4")]
    [InlineData("favorite: true")]
    [InlineData("has_key: true")]
    public void ConditionsAboutTheSenderFailClosed(string condition)
    {
        // A press has no sender, so a script gated on one must not fire: the
        // alternative is a button that transmits as though somebody had asked.
        var engine = Engine($"""
            trigger:
              - quick_send: Ping
            condition:
              - {condition}
            action:
              - send:
                  text: "ping"
            """);

        Assert.Empty(engine.Evaluate(Press("Ping")));
    }

    [Fact]
    public void ConditionsAboutTheDestinationAreRead()
    {
        var engine = Engine("""
            trigger:
              - quick_send: Ping
            condition:
              - channel: Emergency
            action:
              - send:
                  text: "ping"
            """);

        Assert.Single(engine.Evaluate(Press("Ping", channel: "Emergency")));
        Assert.Empty(engine.Evaluate(Press("Ping", channel: "LongFast")));
    }

    [Fact]
    public void DirectScopeHoldsForAPeerAndNotAChannel()
    {
        var engine = Engine("""
            trigger:
              - quick_send: Ping
            condition:
              - scope: direct
            action:
              - send:
                  text: "ping"
            """);

        Assert.Single(engine.Evaluate(Press("Ping", toNode: Peer)));
        Assert.Empty(engine.Evaluate(Press("Ping")));
    }
}
