// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Where a frame was heard: which listener demodulated it, and what that
/// listener was tuned to. Travels with the frame through the router to every
/// host call, so a reply can go back out on the same settings and a node can
/// record what preset it was heard on.
/// </summary>
/// <param name="Listener">Index in the listener set the receiver was started
/// with; 0 is the primary.</param>
/// <param name="Preset">The preset these settings amount to, or null when
/// they amount to none — hand-set parameters that match a preset carry it,
/// since they are the same mesh.</param>
/// <param name="IsCustom">True when the parameters were typed in rather than
/// chosen. Says how they were arrived at, not what they are.</param>
/// <param name="FreqMHz">Channel centre in MHz.</param>
/// <param name="FromDownlink">True for a frame the MQTT bridge handed in:
/// it is handled as the primary's, but it was heard on no radio, so it says
/// nothing about what its sender is tuned to.</param>
public sealed record RxSource(int Listener, LoraPreset? Preset, bool IsCustom, double FreqMHz,
                              bool FromDownlink = false)
{
    public bool IsPrimary => Listener == 0;

    /// <summary>The preset's name, or <see cref="HeardOn.Custom"/>. What a
    /// node stores as where it was heard.</summary>
    public string PresetName => HeardOn.Name(Preset);

    /// <summary>Preset and frequency together, for log lines and the JSON
    /// feed: "LongFast 906.875".</summary>
    public string Tag => HeardOn.Tag(PresetName, FreqMHz);

    /// <param name="preset">What the primary's settings amount to, or null
    /// when they amount to no preset at all.</param>
    public static RxSource Primary(LoraPreset? preset, bool isCustom, double freqMHz) =>
        new(0, preset, isCustom, freqMHz);
}

/// <summary>
/// The one place the "heard on" strings are made, so the node column, the
/// filter, the log tags and the JSON feed agree on them.
/// </summary>
public static class HeardOn
{
    /// <summary>What a node heard on a custom SF/BW/CR primary is recorded
    /// as: no preset names those settings.</summary>
    public const string Custom = "Custom";

    public static string Name(LoraPreset? preset) =>
        preset is null ? Custom : preset.Value.ToString();

    public static string Tag(string presetName, double freqMHz) =>
        $"{presetName} {freqMHz.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}";
}
