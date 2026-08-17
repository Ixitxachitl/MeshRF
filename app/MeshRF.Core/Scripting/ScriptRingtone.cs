// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// A <c>ring:</c> action: sound the ringtone locally. Transmits nothing — this
/// is for getting the operator's attention when something a script watches for
/// happens, so it is deliberately separate from the alerts that ordinary
/// traffic raises.
/// </summary>
public sealed record ScriptRingtone
{
    /// <summary>
    /// RTTTL to play. Empty means the ringtone configured in the app, which is
    /// what an operator who just wants "make a noise" expects to hear.
    /// </summary>
    public string Tune { get; init; } = string.Empty;

    /// <summary>
    /// Loudness as a percentage, 0-100. Null means the app's configured volume,
    /// so a script that says nothing about volume cannot be louder than the
    /// alerts the operator already tuned.
    /// </summary>
    public int? VolumePercent { get; init; }

    /// <summary>True when the app's configured ringtone is to be used.</summary>
    public bool UsesConfiguredTune => string.IsNullOrWhiteSpace(Tune);
}
