// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// The LoRa link budget: what a receiver should hear from a transmitter, and
/// how much of a margin that leaves over the demodulator's floor.
///
/// Everything here is the textbook model — thermal noise, free-space spreading,
/// and the spreading factor's processing gain. Nothing in it knows about
/// terrain, foliage, buildings or fading, so a prediction from this alone is
/// the best case for a path. The obstruction loss is a separate term the caller
/// supplies, which is what <see cref="Map.LinkProfile"/> computes.
/// </summary>
/// <remarks>
/// Follows the model used by MeshLab RF
/// (https://github.com/HarukiToreda/MeshLab-RF, MIT licensed,
/// Copyright (c) 2026 HarukiToreda).
/// </remarks>
public static class LinkBudget
{
    /// <summary>Johnson-Nyquist noise in a 1 Hz bandwidth at room temperature.
    /// </summary>
    public const double ThermalNoiseDbmPerHz = -174.0;

    /// <summary>How much the receiver's own front end adds to that floor.
    /// Six decibels is the usual figure quoted for the SX126x family; a
    /// noisy site is worse and no radio is better.</summary>
    public const double DefaultNoiseFigureDb = 6.0;

    /// <summary>The noise the demodulator sees, which grows with the bandwidth
    /// it has to listen across. This is why the wide presets are less sensitive
    /// than the narrow ones at the same spreading factor.</summary>
    public static double NoiseFloorDbm(double bandwidthKhz, double noiseFigureDb = DefaultNoiseFigureDb)
    {
        if (bandwidthKhz <= 0)
            throw new ArgumentOutOfRangeException(nameof(bandwidthKhz), "bandwidth has to be positive");
        return ThermalNoiseDbmPerHz + 10 * Math.Log10(bandwidthKhz * 1000.0) + noiseFigureDb;
    }

    /// <summary>How far below the noise floor LoRa can still demodulate at a
    /// spreading factor. Each step up the ladder doubles the symbol length and
    /// buys about 2.5 dB, which is the whole reason the slow presets reach
    /// further.</summary>
    public static double RequiredSnrDb(int spreadingFactor) => spreadingFactor switch
    {
        5 => -2.5,
        6 => -5.0,
        7 => -7.5,
        8 => -10.0,
        9 => -12.5,
        10 => -15.0,
        11 => -17.5,
        12 => -20.0,
        _ => throw new ArgumentOutOfRangeException(nameof(spreadingFactor),
                 $"spreading factor {spreadingFactor} is outside the LoRa range of 5 to 12"),
    };

    /// <summary>The weakest signal this modem configuration can decode.</summary>
    public static double SensitivityDbm(
        int spreadingFactor, double bandwidthKhz, double noiseFigureDb = DefaultNoiseFigureDb) =>
        NoiseFloorDbm(bandwidthKhz, noiseFigureDb) + RequiredSnrDb(spreadingFactor);

    /// <summary>Free-space loss over one metre. The reference every log-distance
    /// model is anchored to: loss at range d is this plus 10 n log₁₀(d), and
    /// free space is the case n = 2.</summary>
    public static double FreeSpacePathLossAtOneMetreDb(double frequencyMhz)
    {
        if (frequencyMhz <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequencyMhz), "frequency has to be positive");

        // 20 log₁₀(4π f / c), with the frequency taken in MHz.
        return 20 * Math.Log10(frequencyMhz) + 20 * Math.Log10(4 * Math.PI * 1e6 / 299_792_458.0);
    }

    /// <summary>Free-space spreading loss. The floor under every other loss
    /// term: no path does better than this.</summary>
    public static double FreeSpacePathLossDb(double distanceM, double frequencyMhz)
    {
        if (distanceM <= 0)
            throw new ArgumentOutOfRangeException(nameof(distanceM), "distance has to be positive");

        // Derived from the one-metre reference rather than from the more
        // familiar 32.44 dB km/MHz form, so the two cannot drift apart: the
        // log-distance fit is anchored to the same number.
        return FreeSpacePathLossAtOneMetreDb(frequencyMhz) + 20 * Math.Log10(distanceM);
    }

    /// <summary>What arrives at the receiver's input.</summary>
    /// <param name="txPowerDbm">Conducted power at the transmitter's antenna port.</param>
    /// <param name="txGainDbi">Transmitting antenna gain, net of its feedline.</param>
    /// <param name="rxGainDbi">Receiving antenna gain, net of its feedline.</param>
    /// <param name="pathLossDb">Free-space loss over the distance.</param>
    /// <param name="excessLossDb">Everything the free-space term does not cover
    /// — diffraction over terrain, foliage, walls.</param>
    public static double ReceivedPowerDbm(
        double txPowerDbm, double txGainDbi, double rxGainDbi,
        double pathLossDb, double excessLossDb = 0) =>
        txPowerDbm + txGainDbi + rxGainDbi - pathLossDb - excessLossDb;

    /// <summary>Signal-to-noise ratio a received power works out to. Reported
    /// SNR from a radio is this same quantity, so a prediction and a
    /// measurement can be compared directly.</summary>
    public static double SnrDb(
        double receivedPowerDbm, double bandwidthKhz, double noiseFigureDb = DefaultNoiseFigureDb) =>
        receivedPowerDbm - NoiseFloorDbm(bandwidthKhz, noiseFigureDb);

    /// <summary>How many decibels of headroom the link has over the point where
    /// packets stop decoding. Zero is the cliff edge, not a working link:
    /// fading alone moves a path by several decibels minute to minute.</summary>
    public static double MarginDb(
        double receivedPowerDbm, int spreadingFactor, double bandwidthKhz,
        double noiseFigureDb = DefaultNoiseFigureDb) =>
        receivedPowerDbm - SensitivityDbm(spreadingFactor, bandwidthKhz, noiseFigureDb);
}
