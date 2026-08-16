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

    private static ScriptParseResult ParseSample(string fileName) =>
        ScriptParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName)));

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Ships_Disabled(string fileName)
    {
        // Copying a sample into the scripts folder must not start it
        // transmitting, whichever kind of file it is.
        Assert.False(ParseSample(fileName).Enabled, $"{fileName} ships enabled");
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Is_Throttled_And_Explains_Itself(string fileName)
    {
        var parse = ParseSample(fileName);
        Assert.False(string.IsNullOrWhiteSpace(parse.Alias), $"{fileName} has no alias to show in the list");

        if (parse.Sync is { } sync)
        {
            // A feed's throttle is how often it reads; it sends only what
            // actually changed, so there is no per-hour ceiling to set.
            Assert.True(sync.Every >= TimeSpan.FromMinutes(1), $"{fileName} polls too fast");
            return;
        }

        var script = parse.Script!;
        Assert.True(script.Limits.MaxPerHour > 0, $"{fileName} has no hourly ceiling");
        Assert.True(script.Limits.Cooldown > TimeSpan.Zero, $"{fileName} has no cooldown");
    }

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void A_Sample_Leaves_No_Waypoint_Nobody_Can_Clear(string fileName)
    {
        var parse = ParseSample(fileName);

        if (parse.Sync is { } sync)
        {
            // A mirrored marker is allowed to outlive a clock, because the sync
            // retires it when the record goes. What it must not be is both
            // unexpiring and locked, which would leave it on every map with
            // nobody able to remove it.
            Assert.True(sync.Waypoint.Expires > TimeSpan.Zero || !sync.Waypoint.LockToMe,
                $"{fileName} mirrors a waypoint that never expires and is locked");
            return;
        }

        foreach (var waypoint in parse.Script!.Actions.Select(a => a.Waypoint).OfType<ScriptWaypoint>())
        {
            // A script has no idea when its marker stops being true, so it has
            // to give one an expiry.
            Assert.True(waypoint.Expires > TimeSpan.Zero, $"{fileName} places a waypoint that never expires");
        }
    }
}
