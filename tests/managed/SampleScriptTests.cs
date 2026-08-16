// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The scripts under samples/scripts are documentation people copy and run, so
/// they have to stay valid as the vocabulary moves. A rename in the parser that
/// silently invalidated them would otherwise only be found by whoever pasted
/// one in and watched it fail.
/// </summary>
public class SampleScriptTests
{
    /// <summary>Walks up from the test binary to the repository root, which is
    /// wherever the solution file is.</summary>
    private static string SamplesDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshRF.sln")))
                dir = dir.Parent;

            Assert.NotNull(dir);
            var samples = Path.Combine(dir!.FullName, "samples", "scripts");
            Assert.True(Directory.Exists(samples), $"samples directory not found at {samples}");
            return samples;
        }
    }

    public static TheoryData<string> SampleFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.GetFiles(SamplesDirectory, "*.yaml"))
                data.Add(Path.GetFileName(path));
            return data;
        }
    }

    [Fact]
    public void The_Library_Is_Not_Empty()
    {
        // Guards the discovery above: a broken path would make every theory
        // below vacuously pass.
        Assert.NotEmpty(Directory.GetFiles(SamplesDirectory, "*.yaml"));
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Parses_Without_Errors(string fileName)
    {
        var result = ScriptParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName)));

        Assert.True(result.IsValid, $"{fileName}: {result.FirstError}");
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Ships_Disabled(string fileName)
    {
        // Copying a sample into the scripts folder must not start it
        // transmitting. Reading the raw file rather than the parsed script,
        // because that is what the Scripts window's toggle reads.
        var text = File.ReadAllText(Path.Combine(SamplesDirectory, fileName));

        Assert.False(ScriptParser.Parse(text).Script!.Enabled, $"{fileName} ships enabled");
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Is_Throttled_And_Explains_Itself(string fileName)
    {
        var script = ScriptParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName))).Script!;

        Assert.False(string.IsNullOrWhiteSpace(script.Alias), $"{fileName} has no alias to show in the list");
        Assert.True(script.Limits.MaxPerHour > 0, $"{fileName} has no hourly ceiling");
        Assert.True(script.Limits.Cooldown > TimeSpan.Zero, $"{fileName} has no cooldown");
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_That_Places_A_Waypoint_Gives_It_An_Expiry(string fileName)
    {
        var script = ScriptParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName))).Script!;

        foreach (var waypoint in script.Actions.Select(a => a.Waypoint).OfType<ScriptWaypoint>())
        {
            // An automated marker nobody clears stays on everyone's map.
            Assert.True(waypoint.Expires > TimeSpan.Zero, $"{fileName} places a waypoint that never expires");
        }
    }
}
