// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Which meshes a script answers, and which it speaks on.
/// </summary>
/// <remarks>
/// The station can be listening to several presets at once, so every event the
/// engine sees now carries the mesh it was heard on. The rule that matters most
/// here is the default: a script naming no mesh answers the primary alone,
/// which is what every script written before <c>mesh:</c> existed meant — and
/// what the shipped samples still rely on.
/// </remarks>
public class ScriptMeshTargetTests
{
    private const uint Me = 0x11111111;
    private const uint Peer = 0xa1b2c3d4;

    private static readonly ScriptSelf Self = new(Me, "ME", "My Node", 101);
    private static readonly DateTimeOffset Noon = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static ScriptEngine Engine(string yaml)
    {
        var parse = ScriptParser.Parse(yaml);
        Assert.True(parse.IsValid, $"did not parse: {parse.FirstError}");
        var engine = new ScriptEngine();
        engine.Load([new ScriptFile("s.yaml", Path.Combine("x", "s.yaml"), yaml, Enabled: true, parse)], Noon);
        return engine;
    }

    /// <param name="mesh">Empty is the primary's, which is how every event
    /// arrived before there was more than one mesh to arrive on.</param>
    /// <param name="minute">Minutes past noon, so two firings of one script
    /// are not both refused by its cooldown.</param>
    private static ScriptEvent Text(string body, string mesh = "", int minute = 0) =>
        new()
        {
            Kind = ScriptEventKind.Text,
            Text = body,
            FromNode = Peer,
            FromShort = "PEER",
            FromLong = "Peer Node",
            Channel = "Test",
            Mesh = mesh.Length == 0 ? "MediumFast" : mesh,
            IsPrimaryMesh = mesh.Length == 0,
            IsDirect = true,
            SnrDb = 5,
            Hops = 0,
            PacketId = 0xdeadbeef,
            Self = Self,
            At = Noon.AddMinutes(minute),
        };

    private const string Ping =
        """
        trigger:
          - command: ping
        action:
          - reply: "pong"
        """;

    [Fact]
    public void A_Script_Naming_No_Mesh_Answers_The_Primary_Alone()
    {
        var engine = Engine(Ping);

        Assert.Single(engine.Evaluate(Text("!ping")));
        Assert.Empty(engine.Evaluate(Text("!ping", "LongFast")));
    }

    [Fact]
    public void A_Named_Mesh_Answers_That_One_And_Not_The_Primary()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
                mesh: LongFast
            action:
              - reply: "pong"
            """);

        Assert.Single(engine.Evaluate(Text("!ping", "LongFast")));
        Assert.Empty(engine.Evaluate(Text("!ping")));
    }

    [Fact]
    public void A_List_Answers_Every_Mesh_In_It()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
                mesh: ["{primary}", LongFast]
            action:
              - reply: "pong"
            """);

        Assert.Single(engine.Evaluate(Text("!ping")));
        Assert.Single(engine.Evaluate(Text("!ping", "LongFast", minute: 5)));
        Assert.Empty(engine.Evaluate(Text("!ping", "MediumSlow", minute: 10)));
    }

    [Fact]
    public void Any_Answers_Whichever_Mesh_Asked()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
                mesh: any
            action:
              - reply: "pong"
            """);

        Assert.Single(engine.Evaluate(Text("!ping")));
        Assert.Single(engine.Evaluate(Text("!ping", "ShortTurbo", minute: 5)));
    }

    /// <summary>The primary is matched by role, so a script keeps answering it
    /// after the station has been retuned to another preset.</summary>
    [Fact]
    public void The_Primary_Token_Follows_The_Station_Rather_Than_A_Preset_Name()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
                mesh: "{primary}"
            action:
              - reply: "pong"
            """);

        var retuned = Text("!ping", "LongFast") with { IsPrimaryMesh = true };
        Assert.Single(engine.Evaluate(retuned));
    }

    /// <summary>Each trigger narrows on its own, so one script can answer a
    /// command everywhere and a bare word only at home.</summary>
    [Fact]
    public void Meshes_Are_Per_Trigger()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
                mesh: any
              - text: hello
            action:
              - reply: "pong"
            """);

        Assert.Single(engine.Evaluate(Text("!ping", "LongFast")));
        Assert.Empty(engine.Evaluate(Text("hello", "LongFast", minute: 5)));
        Assert.Single(engine.Evaluate(Text("hello", minute: 10)));
    }

    [Fact]
    public void A_Run_Carries_The_Mesh_Its_Trigger_Arrived_On()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
                mesh: any
            action:
              - reply: "pong"
            """);

        var run = Assert.Single(engine.Evaluate(Text("!ping", "LongFast")));
        Assert.Equal("LongFast", run.Mesh);
        // A reply answers where it was asked, so it names no mesh of its own.
        Assert.Empty(Assert.Single(run.Actions).Meshes ?? []);
    }

    [Fact]
    public void A_Send_Carries_The_Meshes_It_Named()
    {
        var engine = Engine(
            """
            trigger:
              - command: ping
            action:
              - send:
                  channel: Alerts
                  mesh: [LongFast, mediumslow]
                  text: "heads up"
            """);

        var action = Assert.Single(Assert.Single(engine.Evaluate(Text("!ping"))).Actions);
        // Stored as the preset spells itself, whatever case the script used.
        Assert.Equal(["LongFast", "MediumSlow"], action.Meshes);
        Assert.Contains("on LongFast, MediumSlow", action.Describe(n => $"!{n:x8}", "heads up"));
    }

    /// <summary>A schedule is heard on nothing, so its run names no mesh and
    /// the app sends on the primary — where a scheduled beacon has always
    /// gone.</summary>
    [Fact]
    public void A_Schedule_Names_No_Mesh()
    {
        var engine = Engine(
            """
            trigger:
              - every: 1m
            action:
              - send:
                  channel: Alerts
                  text: "beacon"
            """);

        var run = Assert.Single(engine.Tick(Noon.AddMinutes(2), Self));
        Assert.Equal(string.Empty, run.Mesh);
        Assert.Empty(Assert.Single(run.Actions).Meshes ?? []);
    }

    // ----- what the parser accepts and refuses --------------------------------

    private static ScriptProblem SingleError(ScriptParseResult result)
    {
        var errors = result.Problems.Where(p => p.Severity == ScriptProblemSeverity.Error).ToList();
        Assert.Single(errors);
        return errors[0];
    }

    [Fact]
    public void A_Mesh_That_Is_Not_A_Preset_Is_An_Error_With_A_Suggestion()
    {
        var error = SingleError(ScriptParser.Parse(
            """
            trigger:
              - command: ping
                mesh: LongFst
            action:
              - reply: "pong"
            """));

        Assert.Contains("LongFst", error.Message);
        Assert.Contains("LongFast", error.Message);
    }

    [Fact]
    public void A_Placeholder_Other_Than_Primary_Is_Refused()
    {
        var error = SingleError(ScriptParser.Parse(
            """
            trigger:
              - command: ping
                mesh: "{from.id}"
            action:
              - reply: "pong"
            """));

        Assert.Contains("{primary}", error.Message);
    }

    [Fact]
    public void A_Schedule_Refuses_A_Mesh_And_Says_Where_One_Belongs()
    {
        var error = SingleError(ScriptParser.Parse(
            """
            trigger:
              - every: 10m
                mesh: LongFast
            action:
              - send:
                  channel: Alerts
                  text: "beacon"
            """));

        Assert.Contains("every:", error.Message);
        Assert.Contains("send:", error.Message);
    }

    [Fact]
    public void A_Reply_Refuses_A_Mesh_And_Says_Where_One_Belongs()
    {
        var error = SingleError(ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - reply: "pong"
                mesh: LongFast
            """));

        Assert.Contains("reply:", error.Message);
        Assert.Contains("send:", error.Message);
    }

    /// <summary>A DM follows its addressee, so naming meshes for one says two
    /// contradictory things about where it goes.</summary>
    [Fact]
    public void A_Send_Refuses_To_Address_A_Node_And_A_Mesh_At_Once()
    {
        var error = SingleError(ScriptParser.Parse(
            """
            trigger:
              - command: ping
            action:
              - send:
                  to: "{from.id}"
                  mesh: LongFast
                  text: "hello"
            """));

        Assert.Contains("mesh:", error.Message);
    }

    [Fact]
    public void A_Waypoint_Takes_A_Mesh_Beside_Its_Channel()
    {
        var result = ScriptParser.Parse(
            """
            trigger:
              - command: mark
            action:
              - waypoint:
                  lat: home
                  name: "Here"
                  channel: Alerts
                  mesh: any
                  expires: 1h
            """);

        Assert.True(result.IsValid, result.FirstError?.Message);
        Assert.Equal([ScriptMeshes.AnyToken], Assert.Single(result.Script!.Actions).Meshes);
    }

    [Fact]
    public void An_Empty_Mesh_Is_An_Error_Rather_Than_A_Silent_Default()
    {
        var error = SingleError(ScriptParser.Parse(
            """
            trigger:
              - command: ping
                mesh: []
            action:
              - reply: "pong"
            """));

        Assert.Contains("mesh:", error.Message);
    }
}
