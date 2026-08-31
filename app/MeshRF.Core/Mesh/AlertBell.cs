// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// The bell character Meshtastic uses to mark a message as an alert.
/// </summary>
/// <remarks>
/// Firmware's external notification module scans a received message for
/// <c>ASCII_BELL</c> (0x07) and, when its <c>alert_bell</c> options are on,
/// sounds the buzzer or vibrates for that message alone - see
/// <c>ExternalNotificationModule.cpp</c>. It is an ordinary character in the
/// message text rather than a flag on the packet, so it costs a byte of the
/// payload and travels wherever the text does.
/// </remarks>
public static class AlertBell
{
    /// <summary>ASCII BEL, firmware's <c>ASCII_BELL</c>. Written as an escape
    /// rather than the literal control character, which no editor shows.</summary>
    public const char Character = '\u0007';

    /// <summary>The bell on its own. This is what marks a message as an alert;
    /// it is non-printing, so it is never seen.</summary>
    public const string Text = "\u0007";

    /// <summary>
    /// The bell emoji, which firmware draws as a bell icon on the device screen
    /// (see <c>emotes.cpp</c>). This is what the compose bar's bell button
    /// inserts and what everyone sees.
    /// </summary>
    /// <remarks>
    /// The control character is deliberately not put in the box being typed in:
    /// it has no glyph, so a font without one draws a placeholder box and the
    /// writer sees tofu next to their bell. The bell is added on the way out
    /// instead, by <see cref="ForTransmission"/>.
    /// </remarks>
    public const string Glyph = "\U0001F514";

    /// <summary>Whether this message asks the receiver to alert. Null and empty
    /// carry no bell, which is what makes this safe to call on any decoded
    /// text. The emoji alone is not an alert - only the control character is.</summary>
    public static bool IsIn(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(Character);

    /// <summary>
    /// The text as it should go on the air: a message showing a bell carries
    /// the character that sounds one.
    /// </summary>
    /// <remarks>
    /// The rule is the visible one - a bell in the message rings the reader -
    /// rather than a flag held beside the text, which would desync the moment
    /// the writer edited what they had typed. Already-belled text is returned
    /// untouched, so this is safe to apply more than once.
    /// </remarks>
    public static string ForTransmission(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (IsIn(text) || !text.Contains(Glyph, StringComparison.Ordinal)) return text;
        return Text + text;
    }

    /// <summary>The text as it should be shown, with the control character
    /// taken out. It has no glyph, and a font without one draws a placeholder
    /// box rather than nothing at all.</summary>
    public static string StripFrom(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace(Text, string.Empty);
}
