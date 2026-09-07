// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MeshRF.Map;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The skyline as it would look from the antenna: compass across the width,
/// elevation angle up the side, with the nodes plotted where they would appear
/// against it.
///
/// Drawn in depth. Each bearing carries not just the skyline but every ridge
/// standing in front of it, and each of those is painted as its own band,
/// shaded by how far away the ground defining it is — near ground light and
/// warm, distant ground dark and cool, the way haze does it in a photograph.
/// So the country reads as ridge behind ridge rather than as one cut-out, and
/// a glance says whether the thing on the skyline is a bank at the end of the
/// street or a mountain range twenty kilometres off that no mast will beat.
///
/// The view turns by dragging and narrows with the wheel, because a whole turn
/// across one chart is two thirds of a degree per pixel: enough to see that a
/// ridge is there, not enough to see which node it hides.
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
    private static readonly Cursor TurnCursor = new(StandardCursorType.SizeWestEast);

    private static readonly string[] CompassPoints =
        ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
         "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    private const double LeftPad = 46;
    private const double RightPad = 12;
    private const double TopPad = 10;
    private const double BottomPad = 26;

    /// <summary>The narrowest view on offer: about what an eye takes in at
    /// once. Closer than this the sweep's own bearings are further apart than
    /// the pixels between them, and the panorama would be drawing detail it
    /// never measured.</summary>
    private const double MinSpanDeg = 45;

    private HorizonProfile? _profile;
    private IReadOnlyList<HorizonTarget> _targets = [];
    private UnitSystem _units = UnitSystem.Metric;

    /// <summary>Where each node was last drawn, so the pointer can find one.
    /// Rebuilt every render: the view turns, and they turn with it.</summary>
    private readonly List<(Point At, HorizonTarget Target)> _dots = [];

    private double _centreBearing = 180;
    private double _spanDeg = 360;
    private bool _turned;
    private bool _dragging;
    private double _lastX;

    public HorizonChart()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = TurnCursor;
    }

    /// <summary>Which bearing sits in the middle of the view, and how much of
    /// the turn is across it. Read by the tests, which have no other way to say
    /// that a drag turned the panorama.</summary>
    public double CentreBearing => _centreBearing;

    public double SpanDegrees => _spanDeg;

    public void Show(HorizonProfile? profile, IReadOnlyList<HorizonTarget> targets, UnitSystem units)
    {
        _profile = profile;
        _targets = targets;
        _units = units;
        InvalidateVisual();
    }

    /// <summary>Back to the whole turn, north at both edges.</summary>
    public void ResetView()
    {
        _centreBearing = 180;
        _spanDeg = 360;
        _turned = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // A control is only under the pointer where it has drawn something, and
        // most of this one is sky. Without a fill over the whole of it, a drag
        // starting anywhere above the skyline lands on nothing and the panorama
        // refuses to turn.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (_profile is not { Points.Count: > 2 } profile) return;

        double plotW = Bounds.Width - LeftPad - RightPad;
        double plotH = Bounds.Height - TopPad - BottomPad;
        if (plotW <= 4 || plotH <= 4) return;

        // Horizontal is always on the chart: it is the line everything is read
        // against, and a skyline entirely below it still has to show how far
        // below.
        //
        // The angle range comes from the whole turn rather than from what is on
        // screen, so turning slides the picture sideways instead of rescaling it
        // under the pointer.
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

        double X(double offset) => LeftPad + (offset / _spanDeg + 0.5) * plotW;
        double Y(double angle) => TopPad + (1.0 - (angle - minAngle) / (maxAngle - minAngle)) * plotH;

        DrawGrid(context, minAngle, maxAngle, plotW, plotH, Y);

        // Half a column of slack past each edge, so a column straddling an edge
        // is painted rather than dropped and left as a gutter of background.
        double half = _spanDeg / 2 + 360.0 / profile.Points.Count;
        var visible = profile.Points
            .Select(p => (Point: p, Offset: Offset(p.BearingDegrees)))
            .Where(p => Math.Abs(p.Offset) <= half)
            .OrderBy(p => p.Offset)
            .ToList();

        using (context.PushClip(new Rect(LeftPad, TopPad, plotW, plotH)))
        {
            DrawSkyline(context, profile, visible, plotW, plotH, X, Y);

            double horizontal = Y(0);
            context.DrawLine(HorizontalPen, new Point(LeftPad, horizontal),
                             new Point(LeftPad + plotW, horizontal));

            DrawTargets(context, plotW, plotH, X, Y);
            DrawDepthKey(context, profile, plotW);
            DrawViewNote(context);
        }
    }

    /// <summary>The silhouette, as one column per bearing built out of the
    /// crests along it: the band under the nearest crest carries the colour of
    /// the ground that made it, the band above it the colour of whatever stands
    /// behind that, and so on up to the skyline. A single polygon could say none
    /// of it — it would flatten a garden wall and the range behind it into one
    /// shape at one distance.</summary>
    private void DrawSkyline(
        DrawingContext context, HorizonProfile profile,
        List<(HorizonPoint Point, double Offset)> visible,
        double plotW, double plotH, Func<double, double> x, Func<double, double> y)
    {
        if (visible.Count == 0) return;

        double floor = TopPad + plotH;
        double columnWidth = 360.0 / profile.Points.Count / _spanDeg * plotW + 0.75; // overlap, so no seams show

        foreach (var (point, offset) in visible)
        {
            double left = x(offset) - columnWidth / 2;
            double bottom = floor;

            var crests = point.Crests;
            for (int i = 0; i < crests.Count; i++)
            {
                bool skyline = i == crests.Count - 1;
                double top = Math.Clamp(y(crests[i].ElevationAngleDeg), TopPad, floor);

                // A band too thin to read is left to the one above it: drawn, it
                // is a stripe of the wrong colour rather than a ridge.
                if (top >= bottom || (!skyline && bottom - top < 1.5)) continue;

                var colour = GroundColour(crests[i].DistanceM, profile.RadiusM);
                context.FillRectangle(new SolidColorBrush(colour),
                                      new Rect(left, top, columnWidth, bottom - top));

                // The near ridge's own edge, catching the light. Without it a
                // band reads as the shading changing its mind rather than as
                // ground ending and further ground standing behind it.
                if (!skyline)
                    context.FillRectangle(new SolidColorBrush(Rim(colour)),
                                          new Rect(left, top, columnWidth, 1));

                bottom = top;
            }
        }

        // The skyline itself over the top of the shading, so the profile reads
        // as an edge rather than as where the fill runs out.
        var edge = new StreamGeometry();
        using (var ctx = edge.Open())
        {
            ctx.BeginFigure(
                new Point(x(visible[0].Offset), y(visible[0].Point.ElevationAngleDeg)), isFilled: false);
            foreach (var (point, offset) in visible)
                ctx.LineTo(new Point(x(offset), y(point.ElevationAngleDeg)));
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

    /// <summary>A crest lit against the ground behind it: its own colour, a
    /// shade brighter.</summary>
    private static Color Rim(Color ground) => Color.FromRgb(
        (byte)Math.Min(255, ground.R + 30),
        (byte)Math.Min(255, ground.G + 30),
        (byte)Math.Min(255, ground.B + 27));

    /// <summary>Nodes where they would appear against the skyline, each named
    /// wherever its name fits: beside the dot for preference, then over or under
    /// it. Two neighbours a degree apart in bearing but a degree apart in
    /// elevation are two readable labels, where a rule that only looked along
    /// the row threw one of them away.
    ///
    /// A name is still dropped when nothing at all is free, since a stack of
    /// unreadable ones says less than a few readable ones over a row of dots.
    /// Hovering the dot names it, so nothing on the chart stays anonymous.
    /// </summary>
    private void DrawTargets(
        DrawingContext context, double plotW, double plotH,
        Func<double, double> x, Func<double, double> y)
    {
        _dots.Clear();
        var taken = new List<Rect>();
        var plot = new Rect(LeftPad, TopPad, plotW, plotH);

        var onScreen = _targets
            .Select(t => (Target: t, Offset: Offset(t.BearingDegrees)))
            .Where(t => Math.Abs(t.Offset) <= _spanDeg / 2)
            .OrderBy(t => t.Offset);

        foreach (var (target, offset) in onScreen)
        {
            double px = x(offset);
            double py = y(target.ElevationAngleDeg);
            var fill = target.IsVisible ? VisibleFill : HiddenFill;

            context.DrawEllipse(fill, null, new Point(px, py), 3.5, 3.5);
            _dots.Add((new Point(px, py), target));

            var label = new FormattedText(
                target.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelText);

            if (FreeBoxFor(px, py, label, plot, taken) is not { } box) continue;

            context.FillRectangle(LabelBackground,
                new Rect(box.X - 2, box.Y, box.Width + 4, box.Height));
            context.DrawText(label, box.TopLeft);

            // Claimed with a margin, so two names clearing each other by a pixel
            // do not read as one.
            taken.Add(box.Inflate(3));
        }
    }

    /// <summary>The first placement inside the chart and clear of the names
    /// already down, or nothing when a name has nowhere left to go.</summary>
    private static Rect? FreeBoxFor(
        double px, double py, FormattedText label, Rect plot, List<Rect> taken)
    {
        double w = label.Width, h = label.Height;

        Rect[] places =
        [
            new(px + 6, py - h / 2, w, h),      // beside it, reading away from the dot
            new(px - 6 - w, py - h / 2, w, h),
            new(px - w / 2, py - 7 - h, w, h),  // over it, where the sky usually is
            new(px - w / 2, py + 7, w, h),
            new(px + 6, py - 7 - h, w, h),      // and the corners, before giving up
            new(px - 6 - w, py - 7 - h, w, h),
            new(px + 6, py + 7, w, h),
            new(px - 6 - w, py + 7, w, h),
        ];

        foreach (var box in places)
        {
            if (!plot.Contains(box)) continue;
            if (taken.Any(t => t.Intersects(box))) continue;
            return box;
        }

        return null;
    }

    /// <summary>The node under the pointer, if one is near enough to be what
    /// the pointer means.</summary>
    private HorizonTarget? Under(Point p)
    {
        HorizonTarget? found = null;
        double nearest = 8 * 8;

        foreach (var (at, target) in _dots)
        {
            double dx = p.X - at.X, dy = p.Y - at.Y;
            double distance = dx * dx + dy * dy;
            if (distance > nearest) continue;

            nearest = distance;
            found = target;
        }

        return found;
    }

    /// <summary>A node in one line: where it stands, and by how much the ground
    /// clears it or hides it — the figure a mast is chosen from.</summary>
    private string Describe(HorizonTarget target)
    {
        string where =
            $"{target.Name} — {DisplayUnits.FormatShortDistance(target.DistanceM, _units)} " +
            $"{CompassName(target.BearingDegrees)}";

        if (double.IsInfinity(target.ClearanceDeg))
            return $"{where}, nothing between";

        return target.IsVisible
            ? $"{where}, clear by {target.ClearanceDeg:0.0}°"
            : $"{where}, hidden by {-target.ClearanceDeg:0.0}°";
    }

    /// <summary>What the shading means, in the units the app is set to. The
    /// depth is the whole point of the drawing, and nothing else on the chart
    /// says that the dark ground is the far ground.</summary>
    private void DrawDepthKey(DrawingContext context, HorizonProfile profile, double plotW)
    {
        const double barW = 76, barH = 6;
        if (plotW < 340) return;

        var near = new FormattedText("near", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                     LabelTypeface, 10, AxisText);
        var far = new FormattedText(DisplayUnits.FormatShortDistance(profile.RadiusM, _units),
                                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                    LabelTypeface, 10, AxisText);

        double width = near.Width + 5 + barW + 5 + far.Width;
        double left = LeftPad + plotW - 8 - width;
        double top = TopPad + 5;

        context.FillRectangle(LabelBackground,
                              new Rect(left - 5, top - 3, width + 10, near.Height + 6));
        context.DrawText(near, new Point(left, top));
        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(NearGround, 0),
                    new GradientStop(FarGround, 1),
                },
            },
            new Rect(left + near.Width + 5, top + (near.Height - barH) / 2, barW, barH));
        context.DrawText(far, new Point(left + near.Width + 5 + barW + 5, top));
    }

    /// <summary>How to turn the panorama, until someone has — and once they
    /// have, where it is pointing, which the compass alone no longer says at a
    /// glance when the view is a narrow slice of the turn.</summary>
    private void DrawViewNote(DrawingContext context)
    {
        string note = _turned
            ? $"looking {CompassName(_centreBearing)} · {_spanDeg:0}° across · double-click to reset"
            : "drag to turn · scroll to zoom";

        var text = new FormattedText(note, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                     LabelTypeface, 10, AxisText);

        context.FillRectangle(LabelBackground,
                              new Rect(LeftPad + 3, TopPad + 2, text.Width + 10, text.Height + 6));
        context.DrawText(text, new Point(LeftPad + 8, TopPad + 5));
    }

    private void DrawGrid(
        DrawingContext context, double minAngle, double maxAngle, double plotW, double plotH,
        Func<double, double> y)
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

        // The compass, as finely as the view is worth marking: the eight points
        // a person would say across the whole turn, degrees between them once it
        // is narrow enough for the difference to be visible.
        double step = _spanDeg switch { <= 30 => 5, <= 60 => 10, <= 120 => 15, <= 240 => 30, _ => 45 };
        double leftEdge = _centreBearing - _spanDeg / 2;

        for (double b = Math.Ceiling(leftEdge / step) * step; b <= leftEdge + _spanDeg + 1e-9; b += step)
        {
            double px = LeftPad + ((b - _centreBearing) / _spanDeg + 0.5) * plotW;
            double bearing = Normalise(b);
            bool named = Math.Abs(bearing % 45) < 1e-6;

            context.DrawLine(GridPen, new Point(px, TopPad), new Point(px, floor));
            var text = new FormattedText(
                named ? CompassPoints[(int)Math.Round(bearing / 22.5) % CompassPoints.Length]
                      : $"{bearing:0}°",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                LabelTypeface, named ? 11 : 9.5, AxisText);
            context.DrawText(text, new Point(px - text.Width / 2, floor + 4));
        }
    }

    // -- Turning the view ---------------------------------------------------

    /// <summary>Where a bearing falls relative to the middle of the view, in
    /// degrees either side of it. Wrapped, so the seam of the turn is wherever
    /// the view has put it rather than always at north.</summary>
    private double Offset(double bearing) => ((bearing - _centreBearing + 540) % 360) - 180;

    private static double Normalise(double bearing) => ((bearing % 360) + 360) % 360;

    private static string CompassName(double bearing) =>
        $"{CompassPoints[(int)Math.Round(Normalise(bearing) / 22.5) % CompassPoints.Length]} " +
        $"{Normalise(bearing):0}°";

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        // A double-click is the way back to the whole turn from wherever the
        // view has been dragged and zoomed to.
        if (e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _lastX = e.GetPosition(this).X;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_dragging)
        {
            double plotW = Bounds.Width - LeftPad - RightPad;
            double dx = p.X - _lastX;
            if (plotW <= 4 || dx == 0) return;
            _lastX = p.X;

            // The country follows the pointer, as if the drag had hold of it.
            Turn(-dx / plotW * _spanDeg);
            return;
        }

        // A node whose name was crowded off the chart is still a dot, and the
        // pointer is how it says which node it is. Only touched when it changes:
        // this runs on every move, and a tooltip is not free.
        string? tip = Under(p) is { } target ? Describe(target) : null;
        if (ToolTip.GetTip(this) as string != tip) ToolTip.SetTip(this, tip);
    }

    /// <summary>Drop the tooltip on the way out, or it stays armed over
    /// whatever is layered on top of the chart.</summary>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (ToolTip.GetTip(this) is not null) ToolTip.SetTip(this, null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        double plotW = Bounds.Width - LeftPad - RightPad;
        if (plotW <= 4 || e.Delta.Y == 0) return;

        // Zoom about the pointer: the bearing under it is the one being looked
        // at, and it should stay where it is rather than slide out of the view.
        double fraction = (e.GetPosition(this).X - LeftPad) / plotW - 0.5;
        double under = _centreBearing + fraction * _spanDeg;

        // Clamped notches: a trackpad can report a whole screenful of scroll in
        // one event, which would go from the turn to the narrowest view and back
        // faster than anyone could follow.
        _spanDeg = Math.Clamp(
            _spanDeg * Math.Pow(1 / 1.3, Math.Clamp(e.Delta.Y, -3, 3)), MinSpanDeg, 360);
        _centreBearing = Normalise(under - fraction * _spanDeg);
        _turned = true;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Arrow keys turn the view a twelfth of what is on screen, which
    /// is a step at any zoom. Home puts it back.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Turn(-_spanDeg / 12); break;
            case Key.Right: Turn(_spanDeg / 12); break;
            case Key.Home: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Turn(double degrees)
    {
        _centreBearing = Normalise(_centreBearing + degrees);
        _turned = true;
        InvalidateVisual();
    }
}
