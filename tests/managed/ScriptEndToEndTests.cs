// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The library and the engine together: files on disk, through the same
/// enable/reorder path the Scripts window uses, into resolved runs.
/// </summary>
public class ScriptEndToEndTests : IDisposable
{
    private const uint Peer = 0xa1b2c3d4;
    private static readonly ScriptSelf Self = new(0x11111111, "ME", "My Node", 101);
    private static readonly DateTimeOffset Noon = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir;
    private readonly ScriptLibrary _library;

    public ScriptEndToEndTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MeshRF.ScriptE2E", Guid.NewGuid().ToString("n"));
        _library = new ScriptLibrary(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    private ScriptEngine Armed()
    {
        var engine = new ScriptEngine();
        engine.Load(_library.Load(), Noon);
        return engine;
    }

    /// <summary>The wording the first run would actually send. Expansion
    /// happens at execution time, so a run carries templates until asked.</summary>
    private static string Reply(IReadOnlyList<ScriptRun> runs) =>
        runs[0].Expansion.ExpandMessage(runs[0].Actions[0].Text);

    private static ScriptEvent Ping(uint from = Peer) => new()
    {
        Kind = ScriptEventKind.Text,
        Text = "!ping",
        FromNode = from,
        FromShort = "PEER",
        FromLong = "Peer Node",
        Channel = "LongFast",
        IsDirect = true,
        SnrDb = 7,
        Hops = 0,
        PacketId = 0xdeadbeef,
        Self = Self,
        At = Noon,
    };

    [Fact]
    public void The_Starter_Template_Works_As_Soon_As_It_Is_Enabled()
    {
        // The template is the first thing a user sees, and shipping one that
        // does not actually fire would be a poor introduction.
        var fileName = _library.Create("greeter");
        Assert.Equal(0, Armed().ArmedCount);   // created disabled

        _library.SetEnabled(fileName, true);
        var engine = Armed();

        Assert.Equal(1, engine.ArmedCount);
        var run = Assert.Single(engine.Evaluate(Ping()));
        var action = Assert.Single(run.Actions);
        Assert.Equal(ScriptActionKind.Reply, action.Kind);
        Assert.Equal("pong — 7 dB over 0 hops", run.Expansion.ExpandMessage(action.Text));
        Assert.Equal(Peer, action.ToNode);
    }

    [Fact]
    public void The_Enable_Toggle_Arms_And_Disarms_Without_Touching_The_Rest_Of_The_File()
    {
        Write("ping.yaml",
            """
            # A comment that must survive being toggled.
            enabled: true
            trigger:
              - command: ping
            action:
              - reply: "pong"
            """);

        Assert.Equal(1, Armed().ArmedCount);

        _library.SetEnabled("ping.yaml", false);
        Assert.Equal(0, Armed().ArmedCount);
        Assert.Contains("# A comment that must survive being toggled.",
                        File.ReadAllText(Path.Combine(_dir, "ping.yaml")));

        _library.SetEnabled("ping.yaml", true);
        Assert.Equal(1, Armed().ArmedCount);
    }

    [Fact]
    public void Reordering_Changes_The_Order_Scripts_Run_In()
    {
        const string body =
            "trigger:\n  - command: ping\naction:\n  - reply: \"{0}\"\nlimits:\n  cooldown: 0.001s\n";
        Write("a.yaml", "enabled: true\nalias: A\n" + string.Format(body, "from A"));
        Write("b.yaml", "enabled: true\nalias: B\n" + string.Format(body, "from B"));

        _library.SetOrder(["a.yaml", "b.yaml"]);
        Assert.Equal(["A", "B"], Armed().Evaluate(Ping()).Select(r => r.Alias).ToArray());

        _library.SetOrder(["b.yaml", "a.yaml"]);
        Assert.Equal(["B", "A"], Armed().Evaluate(Ping()).Select(r => r.Alias).ToArray());
    }

    [Fact]
    public void One_Broken_File_Does_Not_Stop_The_Others_Arming()
    {
        Write("broken.yaml", "enabled: true\ntriggers:\n  - relpy: nope\n");
        Write("good.yaml", "enabled: true\ntrigger:\n  - command: ping\naction:\n  - reply: \"pong\"\n");

        var engine = Armed();

        Assert.Equal(1, engine.ArmedCount);
        Assert.Equal(["good.yaml"], engine.ArmedNames.ToArray());
        Assert.Single(engine.Evaluate(Ping()));
    }

    [Fact]
    public void Deleting_A_Script_Disarms_It()
    {
        Write("ping.yaml", "enabled: true\ntrigger:\n  - command: ping\naction:\n  - reply: \"pong\"\n");
        Assert.Equal(1, Armed().ArmedCount);

        _library.Delete("ping.yaml");
        Assert.Equal(0, Armed().ArmedCount);
    }

    [Fact]
    public void Editing_A_Script_Takes_Effect_On_The_Next_Load()
    {
        Write("ping.yaml", "enabled: true\ntrigger:\n  - command: ping\naction:\n  - reply: \"first\"\n");
        Assert.Equal("first", Reply(Armed().Evaluate(Ping())));

        _library.Save("ping.yaml", "enabled: true\ntrigger:\n  - command: ping\naction:\n  - reply: \"second\"\n");
        Assert.Equal("second", Reply(Armed().Evaluate(Ping())));
    }
}
