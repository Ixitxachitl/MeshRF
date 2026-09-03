// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

public class ScriptLibraryTests : IDisposable
{
    private readonly string _dir;
    private readonly ScriptLibrary _library;

    public ScriptLibraryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MeshRF.ScriptTests", Guid.NewGuid().ToString("n"));
        _library = new ScriptLibrary(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void WriteFile(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    private string ReadFile(string name) => File.ReadAllText(Path.Combine(_dir, name));

    [Fact]
    public void A_New_Script_Is_Valid_And_Disabled()
    {
        var fileName = _library.Create("My Script");

        Assert.Equal("my-script.yaml", fileName);
        var file = Assert.Single(_library.Load());
        Assert.True(file.Parse.IsValid, $"template did not parse: {file.Parse.FirstError}");
        // Off by default: a script that started transmitting the moment it was
        // created would be a nasty surprise.
        Assert.False(file.Enabled);
    }

    [Fact]
    public void Duplicate_Names_Are_Numbered()
    {
        Assert.Equal("ping.yaml", _library.Create("ping"));
        Assert.Equal("ping-2.yaml", _library.Create("ping"));
        Assert.Equal("ping-3.yaml", _library.Create("ping"));
    }

    [Theory]
    [InlineData("Auto Reply!", "auto-reply")]
    [InlineData("  spaced  out  ", "spaced-out")]
    // Separators are dropped rather than folded to a dash, so nothing that
    // could be read as a path survives into the file name.
    [InlineData("../../etc/passwd", "etcpasswd")]
    [InlineData("C:\\Windows\\system32", "cwindowssystem32")]
    [InlineData("!!!", "")]
    public void Names_Are_Sanitised(string input, string expected)
    {
        Assert.Equal(expected, ScriptLibrary.Sanitize(input));
    }

    [Fact]
    public void Enabling_Preserves_Comments_And_Formatting()
    {
        const string original =
            """
            # My careful notes about this script.
            # Second line of them.

            enabled: false   # leave this off until tested

            trigger:
              - command: ping
            action:
              - reply: "pong"
            """;
        WriteFile("ping.yaml", original);

        _library.SetEnabled("ping.yaml", true);

        var updated = ReadFile("ping.yaml");
        Assert.Equal(original.Replace("enabled: false", "enabled: true"), updated);
        // Specifically: the trailing comment on that very line survives.
        Assert.Contains("enabled: true   # leave this off until tested", updated);
    }

    [Fact]
    public void Enabling_A_File_Without_The_Key_Inserts_It_Below_The_Header()
    {
        WriteFile("ping.yaml",
            """
            # Header comment.

            trigger:
              - command: ping
            action:
              - reply: "pong"
            """);

        _library.SetEnabled("ping.yaml", true);

        var lines = ReadFile("ping.yaml").Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("# Header comment.", lines[0]);
        Assert.Equal("enabled: true", lines[2]);
        Assert.True(ScriptParser.Parse(ReadFile("ping.yaml")).IsValid);
    }

    [Fact]
    public void Enabled_Is_Read_Back_From_Disk()
    {
        WriteFile("ping.yaml", "enabled: true\ntrigger:\n  - command: ping\naction:\n  - reply: \"x\"\n");
        Assert.True(Assert.Single(_library.Load()).Enabled);

        _library.SetEnabled("ping.yaml", false);
        Assert.False(Assert.Single(_library.Load()).Enabled);
    }

    [Fact]
    public void A_Nested_Key_Named_Enabled_Is_Not_Mistaken_For_The_Top_Level_One()
    {
        // Anchored at column 0, so indented look-alikes are left alone.
        WriteFile("ping.yaml",
            """
            enabled: false
            trigger:
              - command: ping
            action:
              - send:
                  channel: enabled
                  text: "x"
            """);

        _library.SetEnabled("ping.yaml", true);

        var text = ReadFile("ping.yaml");
        Assert.StartsWith("enabled: true", text);
        Assert.Contains("channel: enabled", text);
    }

    [Fact]
    public void Order_Round_Trips_And_Drives_Load()
    {
        foreach (var name in new[] { "alpha", "beta", "gamma" }) _library.Create(name);

        _library.SetOrder(["gamma.yaml", "alpha.yaml", "beta.yaml"]);

        Assert.Equal(
            ["gamma.yaml", "alpha.yaml", "beta.yaml"],
            _library.Load().Select(f => f.FileName).ToArray());
    }

    [Fact]
    public void Files_Missing_From_The_Order_Sidecar_Run_Last_In_Name_Order()
    {
        _library.Create("alpha");
        _library.SetOrder(["alpha.yaml"]);

        // Dropped into the folder by hand, so the sidecar has never heard of it.
        WriteFile("zulu.yaml", "trigger:\n  - command: z\naction:\n  - reply: \"z\"\n");
        WriteFile("mike.yaml", "trigger:\n  - command: m\naction:\n  - reply: \"m\"\n");

        Assert.Equal(
            ["alpha.yaml", "mike.yaml", "zulu.yaml"],
            _library.Load().Select(f => f.FileName).ToArray());
    }

    [Fact]
    public void Deleting_Removes_The_File_And_Its_Order_Entry()
    {
        _library.Create("alpha");
        _library.Create("beta");

        _library.Delete("alpha.yaml");

        Assert.Equal(["beta.yaml"], _library.Load().Select(f => f.FileName).ToArray());
        Assert.False(File.Exists(Path.Combine(_dir, "alpha.yaml")));
    }

    [Fact]
    public void An_Unparseable_File_Still_Lists_With_Its_Reason()
    {
        // One bad file must not empty the list, or a typo would look like data
        // loss rather than a mistake to fix.
        WriteFile("broken.yaml", "trigger:\n\t- command: ping\n");
        WriteFile("good.yaml", "trigger:\n  - command: ping\naction:\n  - reply: \"x\"\n");

        var files = _library.Load();

        Assert.Equal(2, files.Count);
        var broken = files.Single(f => f.FileName == "broken.yaml");
        Assert.False(broken.Parse.IsValid);
        Assert.NotNull(broken.Parse.FirstError);
        Assert.True(files.Single(f => f.FileName == "good.yaml").Parse.IsValid);
    }

    [Fact]
    public void Display_Name_Prefers_The_Alias()
    {
        WriteFile("a.yaml", "alias: Nice name\ntrigger:\n  - command: p\naction:\n  - reply: \"x\"\n");
        WriteFile("b.yaml", "trigger:\n  - command: p\naction:\n  - reply: \"x\"\n");

        var files = _library.Load();
        Assert.Equal("Nice name", files.Single(f => f.FileName == "a.yaml").DisplayName);
        Assert.Equal("b", files.Single(f => f.FileName == "b.yaml").DisplayName);
    }

    [Fact]
    public void Yml_Extension_Is_Read_Too()
    {
        WriteFile("legacy.yml", "trigger:\n  - command: p\naction:\n  - reply: \"x\"\n");
        Assert.Equal("legacy.yml", Assert.Single(_library.Load()).FileName);
    }

    [Fact]
    public void The_Samples_Are_Installed_Into_An_Empty_Folder_And_Arrive_Inert()
    {
        var installed = _library.InstallSamples();

        Assert.Equal(
            ["ask-chatgpt.yaml", "geofence-welcome.yaml", "lightning-sync.yaml", "ping.yaml",
             "quick-ping.yaml", "sos.yaml", "test-hops.yaml", "weather.yaml", "wildfire-sync.yaml"],
            installed.OrderBy(n => n, StringComparer.Ordinal));

        // Every one parses and is switched off, which is what makes installing
        // them on someone's behalf a safe thing to do.
        foreach (var file in _library.Load())
        {
            Assert.True(file.Parse.IsValid, $"{file.FileName}: {file.Parse.FirstError}");
            Assert.False(file.Enabled, $"{file.FileName} was installed enabled");
        }
    }

    [Fact]
    public void The_Samples_Are_Installed_Once_And_A_Deleted_One_Stays_Deleted()
    {
        _library.InstallSamples();
        _library.Delete("ping.yaml");

        Assert.Empty(_library.InstallSamples());
        Assert.DoesNotContain(_library.Load(), f => f.FileName == "ping.yaml");
    }

    [Fact]
    public void A_Folder_That_Already_Has_Scripts_Is_Left_Alone()
    {
        // An upgrade must not drop six files into a set someone has curated.
        WriteFile("mine.yaml", "trigger:\n  - command: p\naction:\n  - reply: \"x\"\n");

        Assert.Empty(_library.InstallSamples());
        Assert.Equal("mine.yaml", Assert.Single(_library.Load()).FileName);

        // And it is marked, so the samples do not appear later if that one
        // script is removed.
        _library.Delete("mine.yaml");
        Assert.Empty(_library.InstallSamples());
    }
}
