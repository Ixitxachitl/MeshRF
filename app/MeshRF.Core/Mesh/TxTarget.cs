// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// What a transmission goes out on: modem settings and a frequency, and
/// which listener that is so the transmitter can wait for that channel to
/// be clear. A node on several presets at once has one of these per preset
/// it might answer on.
/// </summary>
/// <param name="Preset">The preset, meaningful when <see cref="Sf"/> is 0.</param>
/// <param name="Sf">Explicit spreading factor, or 0 to take the preset's.</param>
/// <param name="BwHz">Explicit bandwidth, with <see cref="Sf"/>.</param>
/// <param name="Cr">Explicit coding-rate denominator, with <see cref="Sf"/>.</param>
/// <param name="FreqHz">Channel centre.</param>
/// <param name="Listener">Index of the listener on this channel, or -1 when
/// none is; the busy check then falls back to the primary's.</param>
public readonly record struct TxTarget(LoraPreset Preset, byte Sf, uint BwHz, byte Cr, ulong FreqHz, int Listener)
{
    public bool IsCustom => Sf != 0;
    public double FreqMHz => FreqHz / 1e6;

    /// <summary>
    /// Which mesh this goes out on, named the way a listener's settings are.
    /// Two listeners on one preset at different frequencies are two meshes, so
    /// the frequency is part of it; hand-set parameters name no preset.
    /// </summary>
    public string MeshTag => HeardOn.Tag(IsCustom ? HeardOn.Custom : HeardOn.Name(Preset), FreqMHz);

    /// <summary>The preset's own bandwidth, or the explicit one.</summary>
    public uint EffectiveBwHz => IsCustom
        ? BwHz
        : (uint)Math.Round(LoraParamsHelper.FromPreset(Preset).BwKhz * 1000.0);

    /// <summary>A preset on a frequency.</summary>
    public static TxTarget ForPreset(LoraPreset preset, ulong freqHz, int listener) =>
        new(preset, 0, 0, 0, freqHz, listener);

    /// <summary>Explicit modem settings on a frequency.</summary>
    public static TxTarget ForParams(byte sf, uint bwHz, byte cr, ulong freqHz, int listener) =>
        new(LoraPreset.LongFast, sf, bwHz, cr, freqHz, listener);
}
