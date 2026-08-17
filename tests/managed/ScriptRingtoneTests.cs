// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

public class ScriptRingtoneTests
{
    private static ScriptAction ParseSingleAction(string actionYaml)
    {
        var result = ScriptParser.Parse(
            "trigger:\n  - command: ping\naction:\n" + actionYaml);
        Assert.True(result.IsValid, string.Join("; ", result.Problems.Select(p => p.Message)));
        return Assert.Single(result.Script!.Actions);
    }

    private static ScriptProblem SingleError(ScriptParseResult result)
    {
        var errors = result.Problems.Where(p => p.Severity == ScriptProblemSeverity.Error).ToList();
        Assert.Single(errors);
        return errors[0];
    }

    [Fact]
    public void RingDefault_UsesTheConfiguredTuneAndVolume()
    {
        var action = ParseSingleAction("  - ring: default\n");

        Assert.Equal(ScriptActionKind.Ring, action.Kind);
        var ring = Assert.IsType<ScriptRingtone>(action.Ringtone);
        Assert.True(ring.UsesConfiguredTune);
        Assert.Null(ring.VolumePercent);
    }

    [Fact]
    public void RingScalar_TakesTheTextAsRtttl()
    {
        var action = ParseSingleAction("  - ring: \"alert:d=4,o=5,b=120:c,e,g\"\n");

        var ring = Assert.IsType<ScriptRingtone>(action.Ringtone);
        Assert.Equal("alert:d=4,o=5,b=120:c,e,g", ring.Tune);
        Assert.False(ring.UsesConfiguredTune);
        Assert.Null(ring.VolumePercent);
    }

    [Fact]
    public void RingMapping_TakesTuneAndVolume()
    {
        var action = ParseSingleAction(
            "  - ring:\n      tune: \"alert:d=4,o=5,b=120:c,e,g\"\n      volume: 40\n");

        var ring = Assert.IsType<ScriptRingtone>(action.Ringtone);
        Assert.Equal("alert:d=4,o=5,b=120:c,e,g", ring.Tune);
        Assert.Equal(40, ring.VolumePercent);
    }

    [Fact]
    public void RingMapping_VolumeAloneKeepsTheConfiguredTune()
    {
        var action = ParseSingleAction("  - ring:\n      volume: 100\n");

        var ring = Assert.IsType<ScriptRingtone>(action.Ringtone);
        Assert.True(ring.UsesConfiguredTune);
        Assert.Equal(100, ring.VolumePercent);
    }

    [Fact]
    public void RingMapping_TuneDefaultIsTheSameAsOmittingIt()
    {
        var action = ParseSingleAction("  - ring:\n      tune: default\n      volume: 10\n");

        var ring = Assert.IsType<ScriptRingtone>(action.Ringtone);
        Assert.True(ring.UsesConfiguredTune);
        Assert.Equal(10, ring.VolumePercent);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("loud")]
    [InlineData("7.5")]
    public void RingVolume_OutsideZeroToOneHundredIsAnError(string volume)
    {
        var result = ScriptParser.Parse(
            $"trigger:\n  - command: ping\naction:\n  - ring:\n      volume: {volume}\n");

        Assert.False(result.IsValid);
        Assert.Contains("volume", SingleError(result).Message);
    }

    [Fact]
    public void RingRejectsUnknownOptions()
    {
        var result = ScriptParser.Parse(
            "trigger:\n  - command: ping\naction:\n  - ring:\n      loudness: 40\n");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void RingDoesNotCountAsTransmitting()
    {
        // The airtime budget is for frames on the air. A local noise is not one,
        // so it must not consume a script's transmit allowance.
        var ring = new ResolvedAction(ScriptActionKind.Ring, string.Empty, 0, string.Empty, 0,
                                      TimeSpan.Zero, Ringtone: new ScriptRingtone());
        Assert.False(ring.Transmits);

        var reply = new ResolvedAction(ScriptActionKind.Reply, "hi", 0, string.Empty, 0, TimeSpan.Zero);
        Assert.True(reply.Transmits);
    }
}
