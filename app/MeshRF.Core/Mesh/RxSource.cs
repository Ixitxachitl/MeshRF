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
/// <param name="BwHz">The channel's width, as the listener was started with
/// it. Carried rather than looked up from the preset, because a hand-made
/// listener has whatever width it was given and no preset to ask.</param>
/// <param name="MeshName">What this mesh is called: a preset's name, or the
/// name the operator gave a hand-made listener. The one thing that decides
/// which channel list a frame belongs to, which tab it lands on, and what a
/// node records as where it was heard — so it is carried rather than derived,
/// since two hand-made listeners derive the same nothing.</param>
/// <param name="Preset">The preset these settings amount to, or null when
/// they amount to none — hand-set parameters that match a preset carry it,
/// since they are the same mesh.</param>
/// <param name="IsCustom">True when the parameters were typed in rather than
/// chosen. Says how they were arrived at, not what they are.</param>
/// <param name="FreqMHz">Channel centre in MHz.</param>
/// <param name="FromDownlink">True for a frame the MQTT bridge handed in: it
/// is routed as the primary's, and its <c>MeshName</c> is the mesh of the
/// channel it arrived sealed with. What it does not have is a frequency worth
/// recording — it was heard on no radio, so it says nothing about what its
/// sender is tuned to.</param>
public sealed record RxSource(int Listener, string MeshName, LoraPreset? Preset, bool IsCustom,
                              double FreqMHz, uint BwHz = 0, bool FromDownlink = false)
{
    public bool IsPrimary => Listener == 0;

    /// <summary>Mesh and frequency together, for log lines and the JSON
    /// feed: "LongFast 906.875".</summary>
    public string Tag => HeardOn.Tag(MeshName, FreqMHz);

    /// <summary>A listener on a preset, named for it.</summary>
    public static RxSource ForPreset(int listener, LoraPreset preset, double freqMHz) =>
        new(listener, preset.ToString(), preset, false, freqMHz);

    /// <param name="preset">What the primary's settings amount to, or null
    /// when they amount to no preset at all.</param>
    /// <param name="name">What the primary's mesh is called. Empty takes the
    /// preset's name, or <see cref="HeardOn.Custom"/> when it names none.</param>
    public static RxSource Primary(LoraPreset? preset, bool isCustom, double freqMHz, string name = "") =>
        new(0, string.IsNullOrEmpty(name) ? HeardOn.Name(preset) : name, preset, isCustom, freqMHz);
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
