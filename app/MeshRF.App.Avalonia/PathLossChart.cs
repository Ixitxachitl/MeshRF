// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Every direct neighbour plotted as how much worse than free space it was
/// heard, against how far away it is, with the fitted model through them.
///
/// Excess over free space rather than raw path loss, and a logarithmic distance
/// axis: together those make free space a flat line at zero and the fitted
/// model a straight one, so the fit is read as a slope off the horizontal
/// rather than guessed at inside a curve every point lies near.
/// </summary>
public sealed class PathLossChart : Control
{
    private static readonly IBrush IncludedFill = new SolidColorBrush(Color.Parse("#4FC3F7"));
    private static readonly Pen ExcludedPen = new(new SolidColorBrush(Color.Parse("#777777")), 1.2);
    private static readonly Pen FitPen = new(new SolidColorBrush(Color.Parse("#FFB74D")), 1.8);
    private static readonly Pen FreeSpacePen =
        new(new SolidColorBrush(Color.Parse("#66BB6A")), 1.2) { DashStyle = new DashStyle([4, 3], 0) };
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.Parse("#55FFFFFF")), 1.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#22FFFFFF")), 1.0);
    private static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#AAAAAA"));

    private static readonly Typeface LabelTypeface = new(FontFamily.Default);

    private const double LeftPad = 46;
    private const double RightPad = 12;
    private const double TopPad = 12;
    private const double BottomPad = 26;

    private IReadOnlyList<(PathLossObservation Observation, bool Included)> _points = [];
    private PathLossFit? _fit;
    private double _frequencyMhz = 906.875;
    private UnitSystem _units = UnitSystem.Metric;

    public void Show(
        IReadOnlyList<(PathLossObservation Observation, bool Included)> points,
        PathLossFit? fit, double frequencyMhz, UnitSystem units)
    {
        _points = points;
        _fit = fit;
        _frequencyMhz = frequencyMhz;
        _units = units;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_points.Count == 0) return;

        double w = Bounds.Width, h = Bounds.Height;
        double plotW = w - LeftPad - RightPad;
        double plotH = h - TopPad - BottomPad;
        if (plotW <= 4 || plotH <= 4) return;

        double Excess(PathLossObservation o) =>
            o.PropagationLossDb - LinkBudget.FreeSpacePathLossDb(o.DistanceM, _frequencyMhz);

        double minLog = double.MaxValue, maxLog = double.MinValue;
        double minY = 0, maxY = 0; // free space is always on the chart
        foreach (var (observation, _) in _points)
        {
            double logD = Math.Log10(observation.DistanceM);
            minLog = Math.Min(minLog, logD);
            maxLog = Math.Max(maxLog, logD);
            double excess = Excess(observation);
            minY = Math.Min(minY, excess);
            maxY = Math.Max(maxY, excess);
        }

        // A single neighbour, or several all at one range, would collapse the
        // axis onto a point.
        if (maxLog - minLog < 0.15) { minLog -= 0.15; maxLog += 0.15; }
        double logPad = (maxLog - minLog) * 0.08;
        minLog -= logPad;
        maxLog += logPad;

        if (_fit is { } fit)
        {
            minY = Math.Min(minY, fit.ExcessOverFreeSpaceDb(Math.Pow(10, minLog)));
            maxY = Math.Max(maxY, fit.ExcessOverFreeSpaceDb(Math.Pow(10, maxLog)));
        }
        if (maxY - minY < 6) { maxY += 3; minY -= 3; }
        double yPad = (maxY - minY) * 0.1;
        minY -= yPad;
        maxY += yPad;

        double X(double distanceM) =>
            LeftPad + (Math.Log10(distanceM) - minLog) / (maxLog - minLog) * plotW;
        double Y(double db) => TopPad + (1.0 - (db - minY) / (maxY - minY)) * plotH;

        DrawGrid(context, minLog, maxLog, minY, maxY, plotW, plotH, X, Y);

        // Free space, the line every point is measured against.
        double zero = Y(0);
        if (zero >= TopPad && zero <= TopPad + plotH)
        {
            context.DrawLine(FreeSpacePen, new Point(LeftPad, zero), new Point(LeftPad + plotW, zero));
            var label = new FormattedText("free space", CultureInfo.CurrentCulture,
                                          FlowDirection.LeftToRight, LabelTypeface, 10,
                                          FreeSpacePen.Brush!);
            context.DrawText(label, new Point(LeftPad + 4, zero - label.Height - 1));
        }

        if (_fit is { } fitted)
        {
            double nearD = Math.Pow(10, minLog), farD = Math.Pow(10, maxLog);
            context.DrawLine(FitPen,
                new Point(X(nearD), Y(fitted.ExcessOverFreeSpaceDb(nearD))),
                new Point(X(farD), Y(fitted.ExcessOverFreeSpaceDb(farD))));
        }

        // Points last, so a neighbour never disappears under the fitted line.
        foreach (var (observation, included) in _points)
        {
            var at = new Point(X(observation.DistanceM), Y(Excess(observation)));
            if (included) context.DrawEllipse(IncludedFill, null, at, 3.5, 3.5);
            else context.DrawEllipse(null, ExcludedPen, at, 3.5, 3.5);
        }
    }

    private void DrawGrid(
        DrawingContext context, double minLog, double maxLog, double minY, double maxY,
        double plotW, double plotH, Func<double, double> x, Func<double, double> y)
    {
        double floor = TopPad + plotH;
        context.DrawLine(AxisPen, new Point(LeftPad, TopPad), new Point(LeftPad, floor));
        context.DrawLine(AxisPen, new Point(LeftPad, floor), new Point(LeftPad + plotW, floor));

        // Excess loss, in whole decibels.
        foreach (double db in Ticks(minY, maxY, 5))
        {
            double py = y(db);
            if (py < TopPad - 1 || py > floor + 1) continue;
            context.DrawLine(GridPen, new Point(LeftPad, py), new Point(LeftPad + plotW, py));
            var text = new FormattedText($"{db:0} dB", CultureInfo.CurrentCulture,
                                         FlowDirection.LeftToRight, LabelTypeface, 10, AxisText);
            context.DrawText(text, new Point(LeftPad - 5 - text.Width, py - text.Height / 2));
        }

        // Distance ticks at 1, 2 and 5 per decade, which is what a log axis
        // wants — even spacing in metres would crowd the near end to nothing.
        for (int decade = (int)Math.Floor(minLog); decade <= Math.Ceiling(maxLog); decade++)
        {
            foreach (int step in new[] { 1, 2, 5 })
            {
                double metres = step * Math.Pow(10, decade);
                double logD = Math.Log10(metres);
                if (logD < minLog || logD > maxLog) continue;

                double px = x(metres);
                context.DrawLine(GridPen, new Point(px, TopPad), new Point(px, floor));
                var text = new FormattedText(
                    DisplayUnits.FormatShortDistance(metres, _units), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LabelTypeface, 10, AxisText);
                context.DrawText(text, new Point(px - text.Width / 2, floor + 4));
            }
        }
    }

    /// <summary>Round tick values spanning a range, as in the link profile's
    /// elevation axis.</summary>
    private static IEnumerable<double> Ticks(double lo, double hi, int target)
    {
        double span = hi - lo;
        if (span <= 0 || target < 1) yield break;

        double rough = span / target;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double normalised = rough / magnitude;
        double step = (normalised <= 1 ? 1 : normalised <= 2 ? 2 : normalised <= 5 ? 5 : 10) * magnitude;

        // Normalised through zero: a tick landing on negative zero prints as
        // "-0", which reads as a bug in the axis rather than as the origin.
        for (double t = Math.Ceiling(lo / step) * step; t <= hi + step * 1e-9; t += step)
            yield return t == 0 ? 0 : t;
    }
}
