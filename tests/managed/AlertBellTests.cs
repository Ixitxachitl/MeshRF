// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Meshtastic marks a message as an alert with ASCII BEL in the text itself,
/// not with a flag on the packet, so recognising one is a question about the
/// decoded string. Firmware's ExternalNotificationModule does the same scan,
/// and its emote renderer draws the paired bell emoji as an icon.
/// </summary>
public class AlertBellTests
{
    [Fact]
    public void TheCharacterIsAsciiBell()
    {
        // 0x07, firmware's ASCII_BELL. Pinned because the constant is written
        // as an escape that no editor renders, so a typo would be invisible.
        Assert.Equal(7, (int)AlertBell.Character);
        Assert.Equal("\u0007", AlertBell.Text);
    }

    [Fact]
    public void SendingAddsTheBellToTextShowingOne()
    {
        // What the button puts in the box is the visible emoji; the character
        // that actually sounds the alert is added on the way out.
        Assert.Equal("\u0007\U0001F514", AlertBell.ForTransmission(AlertBell.Glyph));
        Assert.Equal("\u0007help \U0001F514", AlertBell.ForTransmission("help \U0001F514"));
        Assert.True(AlertBell.IsIn(AlertBell.ForTransmission(AlertBell.Glyph)));
    }

    [Fact]
    public void SendingLeavesOrdinaryTextAlone()
    {
        // No bell shown, no bell sent: a message never pays for an alert it
        // did not ask for.
        Assert.Equal("just talking", AlertBell.ForTransmission("just talking"));
        Assert.Equal(string.Empty, AlertBell.ForTransmission(null));
        Assert.Equal(string.Empty, AlertBell.ForTransmission(string.Empty));
    }

    [Fact]
    public void SendingIsSafeToApplyTwice()
    {
        // Already-belled text must not collect a second one.
        var once = AlertBell.ForTransmission(AlertBell.Glyph);
        Assert.Equal(once, AlertBell.ForTransmission(once));
    }

    [Fact]
    public void TheGlyphAloneIsNotAnAlert()
    {
        // Someone typing the emoji by hand has not asked anyone's radio to
        // sound, and must not be shown as though they had.
        Assert.False(AlertBell.IsIn(AlertBell.Glyph));
    }

    [Theory]
    [InlineData("\u0007")]
    [InlineData("help\u0007")]
    [InlineData("\u0007help")]
    [InlineData("in the \u0007 middle")]
    public void TextCarryingTheBellIsAnAlert(string text)
    {
        Assert.True(AlertBell.IsIn(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ordinary message")]
    [InlineData("bell")]
    [InlineData("\\u0007")]
    public void OrdinaryTextIsNotAnAlert(string text)
    {
        Assert.False(AlertBell.IsIn(text));
    }

    [Fact]
    public void NullIsNotAnAlert()
    {
        // Called on every decoded message, including ones that carry no text.
        Assert.False(AlertBell.IsIn(null));
    }

    [Fact]
    public void StrippingLeavesTheGlyphAndTheWords()
    {
        // Only the non-printing character goes. The emoji is ordinary message
        // text the sender chose to include.
        Assert.Equal("help\U0001F514", AlertBell.StripFrom("\u0007help\U0001F514"));
        Assert.Equal("plain", AlertBell.StripFrom("plain"));
        Assert.Equal(string.Empty, AlertBell.StripFrom(null));
    }
}
