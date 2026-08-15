// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

public class ScriptTemplateTests
{
    private static readonly ScriptEvent Sample = new()
    {
        Text = "!wx london now",
        FromNode = 0xa1b2c3d4,
        FromShort = "PEER",
        FromLong = "Peer Node",
        Channel = "LongFast",
        IsDirect = true,
        SnrDb = -7.25,
        RssiDbm = -104.6,
        Hops = 2,
        Self = new ScriptSelf(0x11111111, "ME", "My Node", 101),
        At = new DateTimeOffset(2026, 8, 14, 9, 5, 0, TimeSpan.Zero),
    };

    [Theory]
    [InlineData("{msg.text}", "!wx london now")]
    [InlineData("{from.id}", "!a1b2c3d4")]
    [InlineData("{from.short}", "PEER")]
    [InlineData("{from.long}", "Peer Node")]
    [InlineData("{channel}", "LongFast")]
    [InlineData("{snr}", "-7.3")]
    [InlineData("{rssi}", "-105")]
    [InlineData("{hops}", "2")]
    [InlineData("{time}", "09:05")]
    [InlineData("{date}", "2026-08-14")]
    [InlineData("{my.id}", "!11111111")]
    [InlineData("{my.short}", "ME")]
    [InlineData("{my.long}", "My Node")]
    public void Placeholders_Expand(string template, string expected)
    {
        Assert.Equal(expected, ScriptTemplate.Expand(template, Sample));
    }

    [Fact]
    public void The_Mains_Sentinel_Is_Not_Reported_As_A_Percentage()
    {
        // 101 is the "powered from mains" value this app reports, so printing
        // "101%" would be a lie.
        Assert.Equal("mains", ScriptTemplate.Expand("{node.battery}", Sample));

        var onBattery = Sample with { Self = Sample.Self with { BatteryPct = 64 } };
        Assert.Equal("64", ScriptTemplate.Expand("{node.battery}", onBattery));

        var unknown = Sample with { Self = Sample.Self with { BatteryPct = null } };
        Assert.Equal("?", ScriptTemplate.Expand("{node.battery}", unknown));
    }

    [Fact]
    public void A_Missing_Measurement_Reads_As_A_Question_Mark()
    {
        // An empty gap in a sentence reads like a bug; "?" reads like missing data.
        var noSignal = Sample with { SnrDb = null, RssiDbm = null };
        Assert.Equal("? dB / ? dBm", ScriptTemplate.Expand("{snr} dB / {rssi} dBm", noSignal));
    }

    [Fact]
    public void An_Unknown_Placeholder_Is_Left_Exactly_As_Written()
    {
        // The editor already warned. Sending it literally makes the mistake
        // visible, where blanking it would hide it.
        Assert.Equal("hi {from.shrt}", ScriptTemplate.Expand("hi {from.shrt}", Sample));
    }

    [Fact]
    public void Braces_That_Are_Not_Placeholders_Are_Left_Alone()
    {
        Assert.Equal("a { b } c {}", ScriptTemplate.Expand("a { b } c {}", Sample));
    }

    [Fact]
    public void Numbered_Placeholders_Index_Args_And_Captures()
    {
        var args = new[] { "london", "now" };
        var captures = new[] { "cap-one", "cap-two" };

        Assert.Equal("london now", ScriptTemplate.Expand("{arg1} {arg2}", Sample, args, captures));
        Assert.Equal("cap-one", ScriptTemplate.Expand("{cap1}", Sample, args, captures));
        // Out of range is empty, not an error: a script written for two
        // arguments should still send something when given one.
        Assert.Equal("", ScriptTemplate.Expand("{arg9}", Sample, args, captures));
        Assert.Equal("", ScriptTemplate.Expand("{arg1}", Sample, args: null));
    }

    [Theory]
    [InlineData("!ping", new string[0])]
    [InlineData("!ping ", new string[0])]
    [InlineData("!echo one", new[] { "one" })]
    [InlineData("!echo  one   two ", new[] { "one", "two" })]
    public void Arguments_Split_On_Whitespace_After_The_Command(string text, string[] expected)
    {
        Assert.Equal(expected, ScriptTemplate.SplitArguments(text));
    }

    [Fact]
    public void Clamping_Never_Splits_A_Character_In_Half()
    {
        // Every emoji is 4 UTF-8 bytes, so a naive cut at 200 would land inside
        // one and leave a broken glyph on the far end.
        var text = string.Concat(Enumerable.Repeat("😀", 60));
        var clamped = ScriptTemplate.ClampToPayload(text);

        Assert.True(Encoding.UTF8.GetByteCount(clamped) <= 200);
        Assert.Equal(200 / 4, clamped.EnumerateRunes().Count());
        // Round-trips cleanly, which it would not if a sequence were cut.
        Assert.Equal(clamped, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(clamped)));
    }

    [Fact]
    public void Short_Text_Passes_Through_Untouched()
    {
        Assert.Equal("pong", ScriptTemplate.ClampToPayload("pong"));
    }
}
