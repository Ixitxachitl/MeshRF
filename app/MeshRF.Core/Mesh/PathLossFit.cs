// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>One direct neighbour, reduced to what the fit needs.
/// <paramref name="PropagationLossDb"/> is the loss between antenna ports that
/// distance has to account for: everything measured, less the terrain
/// diffraction already explained by the profile.</summary>
public readonly record struct PathLossSample(
    uint NodeNum, double DistanceM, double PropagationLossDb);

/// <summary>
/// A log-distance path-loss model fitted to what this station has actually
/// heard: <c>L(d) = L(1 m) + 10 n log₁₀(d) + c</c>.
///
/// The exponent <c>n</c> is how fast signal falls off around here. Free space
/// is 2; open ground runs a little above it, and suburban clutter reaches 3 to
/// 4. The offset <c>c</c> catches whatever is constant across every link —
/// principally the gap between the peers' real transmit power and the figure
/// assumed for them, which the mesh never reports.
///
/// That split is the point. A constant error in the assumed peer power moves
/// <c>c</c> and leaves <c>n</c> alone, so the exponent stays meaningful even
/// when the absolute calibration cannot be trusted. Read <c>n</c> as the
/// finding and <c>c</c> as the bookkeeping.
/// </summary>
/// <remarks>
/// This is the standing-in for the walked survey MeshLab RF calibrates its
/// building loss from (https://github.com/HarukiToreda/MeshLab-RF, MIT,
/// Copyright (c) 2026 HarukiToreda). A simulator has no ground truth and has to
/// go and measure some; a live client is receiving it continuously.
/// </remarks>
public sealed record PathLossFit(
    double Exponent,
    double OffsetDb,
    double RmsResidualDb,
    int SampleCount,
    bool ExponentFitted,
    bool OffsetFitted,
    double FurthestSampleM = 0,
    bool RangesInconsistent = false)
{
    /// <summary>The exponent of free space, and the value the fit falls back to
    /// when the samples cannot pin one down.</summary>
    public const double FreeSpaceExponent = 2.0;

    /// <summary>Below four neighbours the two parameters cannot be separated
    /// from the noise in single-packet SNR readings, so the exponent is held at
    /// free space and only the offset is measured.</summary>
    public const int MinSamplesForExponent = 4;

    /// <summary>How far apart the near and far neighbours have to be, in
    /// decades of distance, before a slope through them means anything. A
    /// factor of two is the least that separates a real trend from the scatter
    /// of a handful of readings.</summary>
    public const double MinLogDistanceSpread = 0.3;

    /// <summary>How far past its furthest neighbour a fitted model may be
    /// believed. A log-distance model is an extrapolation the moment it leaves
    /// the ranges it was measured over; three times is a stretch a careful
    /// person would accept, and a hundred times is arithmetic, not evidence.
    /// </summary>
    public const double ExtrapolationFactor = 3.0;

    /// <summary>The same allowance when the exponent was never measured. A fit
    /// holding the exponent at free space knows one thing — how strong signals
    /// were at about one range — and knows nothing whatever about how they fall
    /// off, which is precisely what carrying it to a longer range asks of it.
    /// </summary>
    public const double UnfittedExtrapolationFactor = 1.5;

    /// <summary>The furthest range this model has any business being asked
    /// about, or zero when the neighbours it came from are not known.</summary>
    public double CredibleRangeM =>
        FurthestSampleM <= 0
            ? 0
            : FurthestSampleM * (ExponentFitted ? ExtrapolationFactor : UnfittedExtrapolationFactor);

    /// <summary>Whether an exponent is one a real environment could produce.
    /// </summary>
    /// <remarks>Static so a stored calibration can be checked before being
    /// applied to anything, without rebuilding the fit that produced it.
    /// </remarks>
    public static bool IsPlausibleExponent(double exponent) => exponent is >= 1.5 and <= 6.0;

    /// <summary>Whether the fitted exponent is inside the range real
    /// environments produce. Outside it the samples are telling a story about
    /// bad positions or a directional antenna rather than about propagation.
    /// </summary>
    public bool IsPlausible => IsPlausibleExponent(Exponent);

    /// <summary>Total path loss this model predicts at a range.</summary>
    public double PathLossDb(double distanceM, double frequencyMhz) =>
        LinkBudget.FreeSpacePathLossAtOneMetreDb(frequencyMhz)
        + 10 * Exponent * Math.Log10(distanceM)
        + OffsetDb;

    /// <summary>How much worse than free space the model expects a path to be.
    /// This is the term a prediction built on free space alone is missing.
    /// </summary>
    public double ExcessOverFreeSpaceDb(double distanceM) =>
        10 * (Exponent - FreeSpaceExponent) * Math.Log10(distanceM) + OffsetDb;

    /// <summary>
    /// Fits the model to a set of neighbours by least squares, or returns null
    /// when there is nothing to fit.
    /// </summary>
    public static PathLossFit? Fit(IReadOnlyList<PathLossSample> samples, double frequencyMhz)
    {
        if (samples.Count == 0) return null;

        double reference = LinkBudget.FreeSpacePathLossAtOneMetreDb(frequencyMhz);

        // y is the loss the distance term has to produce; x is the decibel
        // ruler it is measured against, so the model is a straight line
        // y = n x + c and the exponent is simply its slope.
        var x = new double[samples.Count];
        var y = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].DistanceM <= 0)
                throw new ArgumentException("a neighbour at zero range has no path to fit", nameof(samples));
            x[i] = 10 * Math.Log10(samples[i].DistanceM);
            y[i] = samples[i].PropagationLossDb - reference;
        }

        double spread = (x.Max() - x.Min()) / 10.0; // back to decades
        bool canFitExponent =
            samples.Count >= MinSamplesForExponent && spread >= MinLogDistanceSpread;

        double exponent = FreeSpaceExponent, offset = 0;
        bool fitted = false;
        bool rangesInconsistent = false;

        if (canFitExponent)
        {
            double n = samples.Count;
            double sx = x.Sum(), sy = y.Sum();
            double sxx = 0, sxy = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                sxx += x[i] * x[i];
                sxy += x[i] * y[i];
            }

            double denominator = n * sxx - sx * sx;
            double slope = (n * sxy - sx * sy) / denominator;

            // A slope at or below zero says signal grew stronger the further
            // away the neighbour was, which no environment does. What it
            // actually means is that the ranges are not ranges: nodes sharing
            // a site, or reporting positions coarse or wrong enough that the
            // distances between them are invented.
            //
            // Refused rather than reported, because every quality signal a
            // reader would check looks fine in this case. Points that are all
            // really at one distance sit tightly on whatever line is drawn
            // through them, so the residual comes out small and the fit reads
            // as excellent while the exponent is impossible.
            if (slope > 0)
            {
                exponent = slope;
                offset = (sy - exponent * sx) / n;
                fitted = true;
            }
            else
            {
                rangesInconsistent = true;
            }
        }

        if (!fitted)
        {
            // Not enough to separate the two, so the distance behaviour is left
            // at free space and everything the neighbours show beyond it is
            // reported as the constant. That keeps the total prediction right
            // while saying plainly that the exponent was not measured.
            exponent = FreeSpaceExponent;
            offset = 0;
            for (int i = 0; i < samples.Count; i++) offset += y[i] - exponent * x[i];
            offset /= samples.Count;
        }

        double sumSquares = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            double residual = y[i] - (exponent * x[i] + offset);
            sumSquares += residual * residual;
        }

        return new PathLossFit(
            Exponent: exponent,
            OffsetDb: offset,
            RmsResidualDb: Math.Sqrt(sumSquares / samples.Count),
            SampleCount: samples.Count,
            ExponentFitted: fitted,
            RangesInconsistent: rangesInconsistent,
            OffsetFitted: true,
            FurthestSampleM: samples.Max(s => s.DistanceM));
    }

    /// <summary>What this model gets wrong about one neighbour: positive when
    /// the radio heard it better than the model says it should. A neighbour
    /// well outside the rest is usually a fuzzed position or an antenna
    /// pointing somewhere particular, and is worth dropping from the fit.
    /// </summary>
    public double ResidualDb(PathLossSample sample, double frequencyMhz) =>
        PathLossDb(sample.DistanceM, frequencyMhz) - sample.PropagationLossDb;
}
