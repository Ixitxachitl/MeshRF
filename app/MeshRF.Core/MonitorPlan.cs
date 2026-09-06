// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// Decides what the receiver listens to: the primary, plus every preset the
/// region supports whose default-slot channel fits inside the capture at the
/// selected sample rate. The capture need not be centred on the primary: its
/// centre can slide, so the spread lies below or above the primary, as long
/// as the primary stays inside it.
/// </summary>
/// <remarks>
/// Pure and deterministic, so the Monitors window can show the outcome of a
/// setting before the receiver starts and a test can pin it down. The device
/// tables it needs, the HackRF's baseband filter widths, are mirrored here
/// rather than asked of the native core, because the window works without a
/// core handle.
/// </remarks>
public static class MonitorPlan
{
    /// <summary>One channel to demodulate.</summary>
    /// <param name="Preset">What these settings amount to, or null when they
    /// amount to no preset at all.</param>
    public sealed record Listener(LoraPreset? Preset, bool IsCustom, byte Sf, uint BwHz, byte Cr,
                                  double FreqMHz, bool IsPrimary)
    {
        public double BandwidthMHz => BwHz / 1e6;
        public double LowEdgeMHz => FreqMHz - BandwidthMHz / 2;
        public double HighEdgeMHz => FreqMHz + BandwidthMHz / 2;
    }

    public enum LeftOutReason
    {
        /// <summary>The user unticked it.</summary>
        Excluded,
        /// <summary>Its channel does not fit the capture at this rate.</summary>
        OutOfRange,
        /// <summary>The region cannot hold the preset's bandwidth.</summary>
        Unsupported,
        /// <summary>The primary is already receiving that channel, on those
        /// modem settings, so it is the same mesh.</summary>
        IsPrimary,
    }

    /// <summary>A preset not listened for, and why. <paramref name="FitsAtRateHz"/>
    /// names the lowest offered rate whose capture could hold it beside the
    /// primary, when there is one.</summary>
    public sealed record LeftOut(LoraPreset Preset, double FreqMHz, LeftOutReason Reason, uint? FitsAtRateHz);

    /// <summary>What the receiver should be started with.</summary>
    /// <param name="DeviceCenterMHz">Where the radio is tuned.</param>
    /// <param name="CenterOffsetKHz">The centre relative to the primary.</param>
    /// <param name="Listeners">Primary first.</param>
    /// <param name="UsableHalfSpanMHz">How far either side of the centre a
    /// channel may reach at this device and rate.</param>
    public sealed record Result(double DeviceCenterMHz, double CenterOffsetKHz,
                                IReadOnlyList<Listener> Listeners, IReadOnlyList<LeftOut> LeftOut,
                                double UsableHalfSpanMHz);

    /// <summary>The toolbar configuration.</summary>
    public sealed record Primary(LoraPreset Preset, bool IsCustom, byte Sf, uint BwHz, byte Cr, double FreqMHz);

    // The MAX2837 baseband filter widths libhackrf can select. The HackRF
    // backend asks for the widest one below the sample rate, so at 2.4 MS/s
    // the capture is really 1.75 MHz wide, not 2.4.
    private static readonly uint[] HackRfFilterHz =
    {
        1_750_000, 2_500_000, 3_500_000, 5_000_000, 5_500_000, 6_000_000, 7_000_000, 8_000_000,
        9_000_000, 10_000_000, 12_000_000, 14_000_000, 15_000_000, 20_000_000, 24_000_000, 28_000_000,
    };

    /// <summary>The baseband filter the HackRF runs at <paramref name="rateHz"/>:
    /// the widest below the rate, as libhackrf's round-down rule picks it.</summary>
    public static uint HackRfBasebandFilterHz(uint rateHz)
    {
        uint best = HackRfFilterHz[0];
        foreach (var f in HackRfFilterHz)
            if (f < rateHz) best = f;
        return best;
    }

    /// <summary>How far either side of the centre a channel may reach, in
    /// MHz. Inside the analogue filter with a tenth to spare on a HackRF;
    /// well inside the tuner's roll-off on an RTL-SDR; the whole Nyquist span
    /// for anything else.</summary>
    public static double UsableHalfSpanMHz(RadioDeviceKind kind, uint rateHz) => kind switch
    {
        RadioDeviceKind.HackRf => 0.9 * HackRfBasebandFilterHz(rateHz) / 2.0 / 1e6,
        RadioDeviceKind.RtlSdr => 0.4 * rateHz / 1e6,
        _ => rateHz / 2.0 / 1e6,
    };

    /// <summary>The frequency a preset's default slot lands on in a region:
    /// what a node on that preset with an unrenamed primary channel uses.</summary>
    public static double DefaultSlotFrequencyMHz(Region region, LoraPreset preset) =>
        ChannelPlan.FrequencyMHz(region, preset, ChannelPlan.DefaultSlot(region, preset));

    /// <summary>
    /// Builds the plan. With the feature off, or on a hardware modem, the
    /// result is the primary alone at zero offset: exactly the single-channel
    /// receiver.
    /// </summary>
    /// <param name="availableRatesHz">The rates the device offers, so a preset
    /// left out for range can say which rate would bring it in.</param>
    /// <param name="centerOffsetKHz">Null picks the offset that takes in the
    /// most channels; a value is clamped so the primary stays inside.</param>
    public static Result Build(Region region, Primary primary, RadioDeviceKind kind, uint rateHz,
                               IReadOnlyList<uint> availableRatesHz, bool enabled,
                               IReadOnlyCollection<string> excludedPresets, double? centerOffsetKHz)
    {
        bool wideLora = ChannelPlan.IsWideLora(region);
        // Hand-set parameters that amount to a preset are named for it: they
        // are the same mesh, and calling them nothing but "custom" hid which
        // mesh the station was actually on.
        LoraPreset? primaryPreset = primary.IsCustom
            ? LoraParamsHelper.TryPresetFor(primary.Sf, primary.BwHz / 1000.0, wideLora, out var matched)
                ? matched : null
            : primary.Preset;
        var primaryListener = new Listener(primaryPreset, primary.IsCustom,
                                           primary.Sf, primary.BwHz, primary.Cr, primary.FreqMHz, IsPrimary: true);
        double half = UsableHalfSpanMHz(kind, rateHz);

        if (!enabled || kind == RadioDeviceKind.Sx1262)
            return new Result(primary.FreqMHz, 0, new[] { primaryListener }, Array.Empty<LeftOut>(), half);

        bool wide = wideLora;
        var candidates = new List<Listener>();
        var leftOut = new List<LeftOut>();
        foreach (var preset in Enum.GetValues<LoraPreset>())
        {
            if (!ChannelPlan.Supports(region, preset))
            {
                leftOut.Add(new LeftOut(preset, 0, LeftOutReason.Unsupported, null));
                continue;
            }
            double f = DefaultSlotFrequencyMHz(region, preset);
            var p = LoraParamsHelper.FromPreset(preset, wide);
            uint bw = (uint)Math.Round(p.BwKhz * 1000.0);
            // A mesh is a frequency and a way of demodulating it, so the
            // preset the primary is already receiving is the one whose
            // channel and modem settings match — not the one whose name it
            // happens to carry. Hand-set parameters that still amount to
            // LongFast are still LongFast, and listening for it again would
            // put a second demodulator on the identical channel and decode
            // every packet there twice. The same preset on another slot is a
            // different mesh and is still a candidate.
            if (Math.Abs(f - primary.FreqMHz) < 1e-6 && p.Sf == primary.Sf && bw == primary.BwHz)
            {
                leftOut.Add(new LeftOut(preset, f, LeftOutReason.IsPrimary, null));
                continue;
            }
            if (excludedPresets.Contains(preset.ToString()))
            {
                leftOut.Add(new LeftOut(preset, f, LeftOutReason.Excluded, null));
                continue;
            }
            candidates.Add(new Listener(preset, false, p.Sf, bw, p.Cr, f, IsPrimary: false));
        }

        // The window may slide only as far as keeps the primary inside it.
        double reach = Math.Max(0, half - primaryListener.BandwidthMHz / 2);
        double offsetMHz;
        if (centerOffsetKHz is { } manual)
        {
            offsetMHz = Math.Clamp(manual / 1000.0, -reach, reach);
        }
        else
        {
            // Every position where a candidate's edge meets the window's edge
            // is a candidate centre; between two such positions the set of
            // channels inside does not change, so nothing is missed.
            var centres = new List<double> { primary.FreqMHz };
            foreach (var c in candidates)
            {
                centres.Add(c.LowEdgeMHz + half);
                centres.Add(c.HighEdgeMHz - half);
            }
            double bestOffset = 0;
            int bestCount = -1;
            foreach (var centre in centres)
            {
                double off = centre - primary.FreqMHz;
                if (off < -reach - 1e-9 || off > reach + 1e-9) continue;
                off = Math.Clamp(off, -reach, reach);
                int count = candidates.Count(c => Fits(c, primary.FreqMHz + off, half));
                if (count > bestCount || (count == bestCount && Math.Abs(off) < Math.Abs(bestOffset)))
                {
                    bestCount = count;
                    bestOffset = off;
                }
            }
            offsetMHz = bestOffset;
        }

        // Rounded to the kilohertz, which is what the radio is asked to tune
        // to. That rounding can carry the centre a fraction past the point
        // where the primary's own channel still fits, so step back inside if
        // it did: half a kilohertz never costs a listener, and the primary
        // falling out of its own capture would.
        double centreMHz = Math.Round((primary.FreqMHz + offsetMHz) * 1000.0) / 1000.0;
        double lowest = primary.FreqMHz - reach;
        double highest = primary.FreqMHz + reach;
        if (centreMHz < lowest) centreMHz = Math.Ceiling(lowest * 1000.0) / 1000.0;
        else if (centreMHz > highest) centreMHz = Math.Floor(highest * 1000.0) / 1000.0;
        var listeners = new List<Listener> { primaryListener };
        foreach (var c in candidates)
        {
            if (Fits(c, centreMHz, half))
            {
                listeners.Add(c);
                continue;
            }
            leftOut.Add(new LeftOut(c.Preset!.Value, c.FreqMHz, LeftOutReason.OutOfRange,
                                    RateThatFits(kind, availableRatesHz, primaryListener, c)));
        }

        return new Result(centreMHz, Math.Round((centreMHz - primary.FreqMHz) * 1000.0, 3),
                          listeners, leftOut, half);
    }

    private static bool Fits(Listener c, double centreMHz, double halfMHz) =>
        Math.Abs(c.FreqMHz - centreMHz) + c.BandwidthMHz / 2 <= halfMHz + 1e-9;

    /// <summary>The lowest offered rate whose capture can hold both the
    /// primary and <paramref name="c"/>, wherever it is centred.</summary>
    private static uint? RateThatFits(RadioDeviceKind kind, IReadOnlyList<uint> rates, Listener primary, Listener c)
    {
        double low = Math.Min(primary.LowEdgeMHz, c.LowEdgeMHz);
        double high = Math.Max(primary.HighEdgeMHz, c.HighEdgeMHz);
        double needHalf = (high - low) / 2;
        foreach (var r in rates.OrderBy(r => r))
            if (UsableHalfSpanMHz(kind, r) >= needHalf - 1e-9) return r;
        return null;
    }
}
