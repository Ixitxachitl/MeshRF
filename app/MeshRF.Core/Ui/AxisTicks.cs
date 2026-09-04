// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// Round numbers to label an axis with.
///
/// Shared by every chart in the app rather than copied into each: they had
/// three identical copies, and the negative-zero rule below was fixed in one of
/// them first while the other two kept printing "-0".
/// </summary>
public static class AxisTicks
{
    /// <summary>
    /// Ticks spanning a range at a step of 1, 2 or 5 times a power of ten, so
    /// the labels read as numbers a person would have chosen.
    /// </summary>
    /// <param name="target">Roughly how many ticks are wanted. The step is
    /// rounded to a friendly one, so the count lands near this rather than on
    /// it.</param>
    public static IEnumerable<double> Between(double lo, double hi, int target)
    {
        double span = hi - lo;
        if (span <= 0 || target < 1 || double.IsNaN(span) || double.IsInfinity(span))
            yield break;

        double rough = span / target;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double normalised = rough / magnitude;
        double step = (normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10) * magnitude;

        for (double t = Math.Ceiling(lo / step) * step; t <= hi + step * 1e-9; t += step)
        {
            // Normalised through zero: a tick landing on negative zero prints
            // as "-0", which reads as a bug in the axis rather than as the
            // origin.
            yield return t == 0 ? 0 : t;
        }
    }
}
