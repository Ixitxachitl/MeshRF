// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeshRF.Map;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The skyline as it would look from the antenna: a full turn of compass across
/// the width, elevation angle up the side, with the nodes plotted where they
/// would appear against it.
///
/// The silhouette is shaded by how far away the ground defining it is — near
/// ground light and warm, distant ground dark and cool, the way haze does it in
/// a photograph. That is the difference between a ridge worth raising a mast
/// over and a mountain range twenty kilometres off that no mast will beat.
/// </summary>
public sealed class HorizonChart : Control
{
    private static readonly Color NearGround = Color.Parse("#8D7B62");
    private static readonly Color FarGround = Color.Parse("#2B3A44");
    private static readonly Pen SkylinePen = new(new SolidColorBrush(Color.Parse("#C8BCA5")), 1.0);
    private static readonly Pen HorizontalPen =
        new(new SolidColorBrush(Color.Parse("#4FC3F7")), 1.2) { DashStyle = new DashStyle([5, 4], 0) };
    private static readonly Pen AxisPen = new(new SolidColorBrush(Color.Parse("#55FFFFFF")), 1.0);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#22FFFFFF")), 1.0);
    private static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#AAAAAA"));
    private static readonly IBrush VisibleFill = new SolidColorBrush(Color.Parse("#66BB6A"));
    private static readonly IBrush HiddenFill = new SolidColorBrush(Color.Parse("#EF5350"));
    private static readonly IBrush LabelText = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.Parse("#B0202020"));

    private static readonly Typeface LabelTypeface = new(FontFamily.Default);

    private const double LeftPad = 46;
    private const double RightPad = 12;
    private const double TopPad = 10;
    private const double BottomPad = 26;

    private HorizonProfile? _profile;
    private IReadOnlyList<HorizonTarget> _targets = [];
    private UnitSystem _units = UnitSystem.Metric;

    public void Show(HorizonProfile? profile, IReadOnlyList<HorizonTarget> targets, UnitSystem units)
    {
        _profile = profile;
        _targets = targets;
        _units = units;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_profile is not { Points.Count: > 2 } profile) return;

        double w = Bounds.Width, h = Bounds.Height;
        double plotW = w - LeftPad - RightPad;
        double plotH = h - TopPad - BottomPad;
        if (plotW <= 4 || plotH <= 4) return;

        // Horizontal is always on the chart: it is the line everything is read
        // against, and a skyline entirely below it still has to show how far
        // below.
        double minAngle = 0, maxAngle = 0;
        foreach (var p in profile.Points)
        {
            minAngle = Math.Min(minAngle, p.ElevationAngleDeg);
            maxAngle = Math.Max(maxAngle, p.ElevationAngleDeg);
        }
        foreach (var t in _targets)
        {
            minAngle = Math.Min(minAngle, t.ElevationAngleDeg);
            maxAngle = Math.Max(maxAngle, t.ElevationAngleDeg);
        }
        if (maxAngle - minAngle < 0.5) { maxAngle += 0.25; minAngle -= 0.25; }
        double pad = (maxAngle - minAngle) * 0.1;
        minAngle -= pad;
        maxAngle += pad;

        double X(double bearing) => LeftPad + bearing / 360.0 * plotW;
        double Y(double angle) => TopPad + (1.0 - (angle - minAngle) / (maxAngle - minAngle)) * plotH;

        DrawGrid(context, minAngle, maxAngle, plotW, plotH, X, Y);
        DrawSkyline(context, profile, plotW, plotH, X, Y);

        double horizontal = Y(0);
        context.DrawLine(HorizontalPen, new Point(LeftPad, horizontal), new Point(LeftPad + plotW, horizontal));

        DrawTargets(context, X, Y);
    }

    /// <summary>The silhouette, as one filled column per bearing so each can
    /// carry the colour of the ground that made it. A polygon could not: the
    /// distance changes from bearing to bearing, and that is the information
    /// worth showing.</summary>
    private void DrawSkyline(
        DrawingContext context, HorizonProfile profile, double plotW, double plotH,
        Func<double, double> x, Func<double, double> y)
    {
        double floor = TopPad + plotH;
        double columnWidth = plotW / profile.Points.Count + 0.75; // overlap, so no seams show

        foreach (var point in profile.Points)
        {
            double left = x(point.BearingDegrees);
            double top = Math.Clamp(y(point.ElevationAngleDeg), TopPad, floor);

            context.FillRectangle(
                new SolidColorBrush(GroundColour(point.DistanceM, profile.RadiusM)),
                new Rect(left, top, columnWidth, floor - top));
        }

        // The skyline itself over the top of the shading, so the profile reads
        // as an edge rather than as where the fill runs out.
        var edge = new StreamGeometry();
        using (var ctx = edge.Open())
        {
            ctx.BeginFigure(new Point(x(0), y(profile.Points[0].ElevationAngleDeg)), isFilled: false);
            foreach (var point in profile.Points)
                ctx.LineTo(new Point(x(point.BearingDegrees), y(point.ElevationAngleDeg)));
            ctx.LineTo(new Point(x(360), y(profile.Points[0].ElevationAngleDeg)));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, SkylinePen, edge);
    }

    /// <summary>Aerial perspective: near ground warm and light, distant ground
    /// cool and dark. Interpolated on the logarithm of the distance, because
    /// the difference between 200 m and 2 km matters far more than the one
    /// between 12 km and 14 km.</summary>
    private static Color GroundColour(double distanceM, double radiusM)
    {
        double near = 100, far = Math.Max(near * 2, radiusM);
        double t = Math.Clamp(
            Math.Log10(Math.Max(distanceM, near) / near) / Math.Log10(far / near), 0, 1);

        return Color.FromRgb(
            (byte)(NearGround.R + (FarGround.R - NearGround.R) * t),
            (byte)(NearGround.G + (FarGround.G - NearGround.G) * t),
            (byte)(NearGround.B + (FarGround.B - NearGround.B) * t));
    }

    /// <summary>Nodes where they would appear against the skyline. Labels are
    /// dropped rather than overlapped: a stack of unreadable names says less
    /// than a few readable ones over a row of dots.</summary>
    private void DrawTargets(DrawingContext context, Func<double, double> x, Func<double, double> y)
    {
        double lastLabelRight = double.NegativeInfinity;

        foreach (var target in _targets.OrderBy(t => t.BearingDegrees))
        {
            double px = x(target.BearingDegrees);
            double py = y(target.ElevationAngleDeg);
            var fill = target.IsVisible ? VisibleFill : HiddenFill;

            context.DrawEllipse(fill, null, new Point(px, py), 3.5, 3.5);

            var label = new FormattedText(
                target.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelText);
            double left = px + 6;
            if (left < lastLabelRight) continue;

            context.FillRectangle(LabelBackground,
                new Rect(left - 2, py - label.Height / 2, label.Width + 4, label.Height));
            context.DrawText(label, new Point(left, py - label.Height / 2));
            lastLabelRight = left + label.Width + 6;
        }
    }

    private void DrawGrid(
        DrawingContext context, double minAngle, double maxAngle, double plotW, double plotH,
        Func<double, double> x, Func<double, double> y)
    {
        double floor = TopPad + plotH;
        context.DrawLine(AxisPen, new Point(LeftPad, TopPad), new Point(LeftPad, floor));
        context.DrawLine(AxisPen, new Point(LeftPad, floor), new Point(LeftPad + plotW, floor));

        foreach (double angle in AxisTicks.Between(minAngle, maxAngle, 6))
        {
            double py = y(angle);
            if (py < TopPad - 1 || py > floor + 1) continue;

            context.DrawLine(GridPen, new Point(LeftPad, py), new Point(LeftPad + plotW, py));
            var text = new FormattedText($"{angle:0.##}°", CultureInfo.CurrentCulture,
                                         FlowDirection.LeftToRight, LabelTypeface, 10, AxisText);
            context.DrawText(text, new Point(LeftPad - 5 - text.Width, py - text.Height / 2));
        }

        // The compass, in the points a person would actually say.
        (double Bearing, string Name)[] compass =
        [
            (0, "N"), (45, "NE"), (90, "E"), (135, "SE"),
            (180, "S"), (225, "SW"), (270, "W"), (315, "NW"), (360, "N"),
        ];

        foreach (var (bearing, name) in compass)
        {
            double px = x(bearing);
            context.DrawLine(GridPen, new Point(px, TopPad), new Point(px, floor));
            var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                         LabelTypeface, 11, AxisText);
            context.DrawText(text, new Point(px - text.Width / 2, floor + 4));
        }
    }
}
