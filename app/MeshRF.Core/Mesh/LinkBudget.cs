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

    /// <summary>
    /// How far a link reaches before it has spent a given number of decibels —
    /// the log-distance model run backwards.
    /// </summary>
    /// <param name="lossDb">The loss budget to spend.</param>
    /// <param name="exponent">Path-loss exponent; 2 is free space. A fitted
    /// value from <see cref="PathLossFit"/> gives the range this station
    /// actually gets rather than the range a vacuum would.</param>
    /// <param name="offsetDb">Constant term of the same model.</param>
    public static double RangeForLossDb(
        double lossDb, double frequencyMhz, double exponent = 2.0, double offsetDb = 0)
    {
        if (exponent <= 0)
            throw new ArgumentOutOfRangeException(nameof(exponent), "the exponent has to be positive");

        double spent = lossDb - offsetDb - FreeSpacePathLossAtOneMetreDb(frequencyMhz);
        return Math.Pow(10, spent / (10 * exponent));
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

    /// <summary>
    /// The chance a packet decodes at a given margin.
    ///
    /// A link does not switch from working to not working at a threshold: it
    /// fades by a few decibels minute to minute, so a path sitting on its own
    /// sensitivity is a coin toss rather than a wall. This is the logistic
    /// curve that reading implies — a half at zero margin, nearly certain a few
    /// decibels above, nearly hopeless a few below.
    /// </summary>
    /// <param name="spreadDb">How quickly the odds change with margin, which is
    /// really how much the path fades. Three decibels suits the slow fading of
    /// a fixed link; somewhere windy or mobile spreads wider.</param>
    public static double DecodeProbability(double marginDb, double spreadDb = 3.0)
    {
        if (spreadDb <= 0)
            throw new ArgumentOutOfRangeException(nameof(spreadDb), "the spread has to be positive");

        // Saturating rather than overflowing: a margin of a few hundred
        // decibels is arithmetic a caller can reach, and Exp of it is infinity.
        double z = Math.Clamp(marginDb / spreadDb, -40, 40);
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    /// <summary>How many decibels of headroom the link has over the point where
    /// packets stop decoding. Zero is the cliff edge, not a working link:
    /// fading alone moves a path by several decibels minute to minute.</summary>
    public static double MarginDb(
        double receivedPowerDbm, int spreadingFactor, double bandwidthKhz,
        double noiseFigureDb = DefaultNoiseFigureDb) =>
        receivedPowerDbm - SensitivityDbm(spreadingFactor, bandwidthKhz, noiseFigureDb);
}
