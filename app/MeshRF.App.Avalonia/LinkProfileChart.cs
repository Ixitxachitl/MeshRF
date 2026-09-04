// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeshRF.Map;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The terrain cross-section between two radios: ground profile, the sight line
/// between the antennas, and the first Fresnel zone around it.
///
/// Drawn immediate-mode for the same reason the map is — a few hundred points
/// into a <see cref="DrawingContext"/> costs less than a retained tree of
/// shapes, and nothing here is hit-tested.
///
/// The terrain is real elevation and the sight line carries the earth's
/// curvature, rather than the other way round. Both conventions are in use; this
/// one keeps the y axis readable as altitude, so a peak on the chart is at the
/// height a map says it is.
/// </summary>
public sealed class LinkProfileChart : Control
{
    private static readonly IBrush GroundFill = new SolidColorBrush(Color.Parse("#3C3A33"));
    private static readonly IBrush BlockedFill = new SolidColorBrush(Color.Parse("#5A3230"));
    private static readonly Pen GroundPen = new(new SolidColorBrush(Color.Parse("#8A8570")), 1.2);
    private static readonly Pen SightPen = new(new SolidColorBrush(Color.Parse("#4FC3F7")), 1.6);
    private static readonly IBrush FresnelFill = new SolidColorBrush(Color.Parse("#4FC3F7"), 0.12);
    private static readonly Pen FresnelPen = new(new SolidColorBrush(Color.Parse("#4FC3F7"), 0.45), 1.0);
    private static readonly Pen ClearancePen =
        new(new SolidColorBrush(Color.Parse("#FFB74D")), 1.0) { DashStyle = new DashStyle([4, 3], 0) };
    private static readonly Pen WorstPen = new(new SolidColorBrush(Color.Parse("#FF7043")), 1.2);
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.Parse("#55FFFFFF")), 1.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#22FFFFFF")), 1.0);
    private static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush AntennaFill = new SolidColorBrush(Color.Parse("#ECEFF1"));

    private static readonly Typeface LabelTypeface = new(FontFamily.Default);

    private const double LeftPad = 52;
    private const double RightPad = 10;
    private const double TopPad = 10;
    private const double BottomPad = 24;

    private LinkProfile? _profile;
    private UnitSystem _units = UnitSystem.Metric;
    private string _fromLabel = string.Empty;
    private string _toLabel = string.Empty;

    public void Show(LinkProfile? profile, UnitSystem units, string fromLabel, string toLabel)
    {
        _profile = profile;
        _units = units;
        _fromLabel = fromLabel;
        _toLabel = toLabel;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_profile is not { } profile || profile.Points.Count < 2) return;

        double w = Bounds.Width, h = Bounds.Height;
        double plotW = w - LeftPad - RightPad;
        double plotH = h - TopPad - BottomPad;
        if (plotW <= 4 || plotH <= 4) return;

        // The vertical extent has to hold the terrain, both antennas and the
        // whole Fresnel zone: clipping the zone would hide exactly the
        // clearance the chart exists to show.
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in profile.Points)
        {
            minY = Math.Min(minY, Math.Min(p.GroundM, p.SightLineM - p.FresnelRadiusM));
            maxY = Math.Max(maxY, Math.Max(p.GroundM, p.SightLineM + p.FresnelRadiusM));
        }
        if (maxY - minY < 1) { maxY += 0.5; minY -= 0.5; }
        double pad = (maxY - minY) * 0.08;
        minY -= pad;
        maxY += pad;

        double X(double distanceM) => LeftPad + distanceM / profile.DistanceM * plotW;
        double Y(double metres) => TopPad + (1.0 - (metres - minY) / (maxY - minY)) * plotH;

        DrawGrid(context, profile, minY, maxY, plotW, plotH, X, Y);

        // The filled zone goes under the terrain, so ground rising into it
        // reads as the zone being eaten away rather than as a tint over the
        // hill.
        DrawFresnelBand(context, profile, X, Y);
        DrawTerrain(context, profile, plotH, X, Y);

        // The 60% line goes over it. Underneath, it spends most of a hilly
        // path buried in the silhouette — and it is the line the verdict is
        // measured against, so it has to be followable end to end.
        DrawPolyline(context, profile, ClearancePen,
            p => p.SightLineM - LinkProfile.FresnelClearanceTarget * p.FresnelRadiusM, X, Y);
        DrawPolyline(context, profile, SightPen, p => p.SightLineM, X, Y);
        DrawWorstPoint(context, profile, X, Y);
        DrawAntennas(context, profile, X, Y);
    }

    /// <summary>Terrain as a filled silhouette, with any stretch that rises
    /// above the sight line filled in the blocked colour. That second fill is
    /// the whole verdict in one glance.</summary>
    private void DrawTerrain(
        DrawingContext context, LinkProfile profile, double plotH,
        Func<double, double> x, Func<double, double> y)
    {
        double floor = TopPad + plotH;

        var ground = new StreamGeometry();
        using (var ctx = ground.Open())
        {
            ctx.BeginFigure(new Point(x(0), floor), isFilled: true);
            foreach (var p in profile.Points) ctx.LineTo(new Point(x(p.DistanceM), y(p.GroundM)));
            ctx.LineTo(new Point(x(profile.DistanceM), floor));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(GroundFill, GroundPen, ground);

        // Each run of samples above the sight line, filled down to the line.
        for (int i = 0; i < profile.Points.Count; i++)
        {
            if (profile.Points[i].ClearanceM >= 0) continue;
            int start = i;
            while (i < profile.Points.Count && profile.Points[i].ClearanceM < 0) i++;
            int end = i - 1;
            if (end <= start) continue;

            var blocked = new StreamGeometry();
            using (var ctx = blocked.Open())
            {
                ctx.BeginFigure(new Point(x(profile.Points[start].DistanceM), y(profile.Points[start].SightLineM)), true);
                for (int j = start; j <= end; j++)
                    ctx.LineTo(new Point(x(profile.Points[j].DistanceM), y(profile.Points[j].GroundM)));
                for (int j = end; j >= start; j--)
                    ctx.LineTo(new Point(x(profile.Points[j].DistanceM), y(profile.Points[j].SightLineM)));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(BlockedFill, null, blocked);
        }
    }

    private void DrawFresnelBand(
        DrawingContext context, LinkProfile profile, Func<double, double> x, Func<double, double> y)
    {
        var band = new StreamGeometry();
        using (var ctx = band.Open())
        {
            ctx.BeginFigure(new Point(x(0), y(profile.Points[0].SightLineM)), isFilled: true);
            foreach (var p in profile.Points)
                ctx.LineTo(new Point(x(p.DistanceM), y(p.SightLineM + p.FresnelRadiusM)));
            for (int i = profile.Points.Count - 1; i >= 0; i--)
            {
                var p = profile.Points[i];
                ctx.LineTo(new Point(x(p.DistanceM), y(p.SightLineM - p.FresnelRadiusM)));
            }
            ctx.EndFigure(true);
        }
        context.DrawGeometry(FresnelFill, FresnelPen, band);
    }

    private void DrawPolyline(
        DrawingContext context, LinkProfile profile, Pen pen,
        Func<ProfilePoint, double> value, Func<double, double> x, Func<double, double> y)
    {
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(x(profile.Points[0].DistanceM), y(value(profile.Points[0]))), false);
            for (int i = 1; i < profile.Points.Count; i++)
                ctx.LineTo(new Point(x(profile.Points[i].DistanceM), y(value(profile.Points[i]))));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, line);
    }

    /// <summary>A dropline at the tightest point on the path, which is the
    /// sample every number in the summary is taken from.</summary>
    private void DrawWorstPoint(
        DrawingContext context, LinkProfile profile, Func<double, double> x, Func<double, double> y)
    {
        var worst = profile.Worst;
        double px = x(worst.DistanceM);
        context.DrawLine(WorstPen, new Point(px, y(worst.SightLineM)), new Point(px, y(worst.GroundM)));
        context.DrawEllipse(WorstPen.Brush, null, new Point(px, y(worst.GroundM)), 2.5, 2.5);
    }

    private void DrawAntennas(
        DrawingContext context, LinkProfile profile, Func<double, double> x, Func<double, double> y)
    {
        DrawAntenna(context, profile.Points[0], x, y, _fromLabel, alignLeft: true);
        DrawAntenna(context, profile.Points[^1], x, y, _toLabel, alignLeft: false);
    }

    private void DrawAntenna(
        DrawingContext context, ProfilePoint end, Func<double, double> x, Func<double, double> y,
        string label, bool alignLeft)
    {
        double px = x(end.DistanceM);
        context.DrawLine(new Pen(AntennaFill, 1.2), new Point(px, y(end.GroundM)), new Point(px, y(end.SightLineM)));
        context.DrawEllipse(AntennaFill, null, new Point(px, y(end.SightLineM)), 3, 3);

        if (string.IsNullOrEmpty(label)) return;
        var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                     LabelTypeface, 11, AntennaFill);
        double tx = alignLeft ? px + 5 : px - 5 - text.Width;
        context.DrawText(text, new Point(tx, Math.Max(TopPad, y(end.SightLineM) - text.Height - 4)));
    }

    private void DrawGrid(
        DrawingContext context, LinkProfile profile, double minY, double maxY,
        double plotW, double plotH, Func<double, double> x, Func<double, double> y)
    {
        bool imperial = DisplayUnits.IsImperial(_units);
        double floor = TopPad + plotH;

        context.DrawLine(AxisPen, new Point(LeftPad, TopPad), new Point(LeftPad, floor));
        context.DrawLine(AxisPen, new Point(LeftPad, floor), new Point(LeftPad + plotW, floor));

        // Elevation, in whatever the user reads altitudes in.
        double loDisplay = imperial ? minY * 3.28083989501312 : minY;
        double hiDisplay = imperial ? maxY * 3.28083989501312 : maxY;
        foreach (double tick in Ticks(loDisplay, hiDisplay, 5))
        {
            double metres = imperial ? tick / 3.28083989501312 : tick;
            double py = y(metres);
            if (py < TopPad - 1 || py > floor + 1) continue;

            context.DrawLine(GridPen, new Point(LeftPad, py), new Point(LeftPad + plotW, py));
            var text = new FormattedText(
                $"{tick:0}{(imperial ? " ft" : " m")}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, 10, AxisText);
            context.DrawText(text, new Point(LeftPad - 5 - text.Width, py - text.Height / 2));
        }

        // Distance along the path.
        double spanDisplay = imperial
            ? profile.DistanceM / 1609.344
            : profile.DistanceM / 1000.0;
        foreach (double tick in Ticks(0, spanDisplay, 5))
        {
            double metres = imperial ? tick * 1609.344 : tick * 1000.0;
            if (metres > profile.DistanceM) continue;
            double px = x(metres);

            context.DrawLine(GridPen, new Point(px, TopPad), new Point(px, floor));
            var text = new FormattedText(
                $"{tick:0.##} {DisplayUnits.DistanceUnitShort(_units)}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, 10, AxisText);
            context.DrawText(text, new Point(px - text.Width / 2, floor + 4));
        }
    }

    /// <summary>Round tick values spanning a range — 1, 2 or 5 times a power of
    /// ten, so the labels read as numbers a person would choose.</summary>
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
