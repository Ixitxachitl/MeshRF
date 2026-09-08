// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace MeshRF.Mesh;

/// <summary>
/// What a receiver measured about one reception: how far the signal stood
/// above the noise, and how strong it was.
/// </summary>
/// <remarks>
/// <para>The unit travels with the level because it is not the same on every
/// receiver, and the two have been separated before. A packet radio measures
/// real received power and reports dBm. An SDR measures a level relative to
/// its converter's full scale and reports dBFS, and nothing in the chain says
/// what that is in absolute terms — the analogue gain in front of it moves
/// every reading. Neither can be converted to the other without a calibration
/// this app does not have, so what is carried is the number that was actually
/// measured, labelled with what it is.</para>
/// <para><see cref="SnrDb"/> is chip-level SNR, the figure Meshtastic firmware
/// and the SX1262 report, which is normally negative — LoRa decodes below the
/// noise floor. It means the same thing whichever receiver produced it.</para>
/// </remarks>
/// <param name="SnrDb">Chip-level signal-to-noise ratio, or null when the
/// receiver did not measure one.</param>
/// <param name="Rssi">Signal level in the unit <see cref="RssiIsDbm"/> names,
/// or null when none was measured.</param>
/// <param name="RssiIsDbm">True when <see cref="Rssi"/> is absolute power in
/// dBm; false when it is a full-scale-relative level in dBFS.</param>
public readonly record struct SignalReading(float? SnrDb, float? Rssi, bool RssiIsDbm)
{
    /// <summary>A reception nothing was measured about: one handed in over
    /// MQTT, which reached us over a wire and says nothing about any air.
    /// </summary>
    public static readonly SignalReading None = new(null, null, false);

    /// <summary>The unit <see cref="Rssi"/> is in, for a label.</summary>
    public string RssiUnit => RssiIsDbm ? "dBm" : "dBFS";

    /// <summary>The level with its unit, or null when there is no level.
    /// The one place the two are put together, so nothing formats a dBFS
    /// reading as dBm by writing the suffix itself.</summary>
    public string? RssiText => Rssi is float v
        ? $"{v:0} {RssiUnit}"
        : null;

    // "snr=-12.5dB", not the dB inside a "dBFS" or "dBm" that follows a level.
    private static readonly Regex SnrRegex = new(
        @"snr=(?<snr>-?\d+(?:\.\d+)?)dB(?![Fm])", RegexOptions.Compiled);

    private static readonly Regex RssiRegex = new(
        @"rssi=(?<rssi>-?\d+(?:\.\d+)?)(?<unit>dBFS|dBm)", RegexOptions.Compiled);

    /// <summary>
    /// Reads what a demodulator's preamble line measured.
    /// </summary>
    /// <remarks>
    /// The receiver reports through a line of text rather than a structured
    /// callback, so this is where a reception's figures enter the managed
    /// side. Either may be absent — a line from a core built before they were
    /// reported, or a receiver that measures neither — and an absent figure is
    /// unknown, not zero. A level with no unit on it is not a level: it is the
    /// mistake this type exists to prevent, so it is discarded rather than
    /// guessed at.
    /// </remarks>
    public static SignalReading FromPreambleLine(string line)
    {
        float? snr = null;
        var sm = SnrRegex.Match(line);
        if (sm.Success && float.TryParse(sm.Groups["snr"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            snr = s;

        var rm = RssiRegex.Match(line);
        if (!rm.Success || !float.TryParse(rm.Groups["rssi"].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
            return new SignalReading(snr, null, false);

        return new SignalReading(snr, r, rm.Groups["unit"].Value == "dBm");
    }
}
