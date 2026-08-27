// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>The space a label would occupy, in the pixel space shared by every
/// output tile drawn from one source tile at one zoom.</summary>
public readonly record struct LabelBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Centred on a point, which is how a point label sits over the
    /// feature it names before any anchor offset is applied.</summary>
    public static LabelBox Centered(double centerX, double centerY, double width, double height) =>
        new(centerX - width / 2, centerY - height / 2, width, height);

    public LabelBox Inflated(double by) =>
        new(X - by, Y - by, Width + by * 2, Height + by * 2);

    public LabelBox Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    public bool Intersects(in LabelBox other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>Whether any of this box falls inside a tile of the given size
    /// whose top-left sits at the given offset in the shared space.</summary>
    public bool IntersectsTile(double offsetX, double offsetY, double size) =>
        Intersects(new LabelBox(offsetX, offsetY, size, size));
}

/// <summary>Decides which labels get drawn when they would overlap.
///
/// Labels are offered in the order the style lists their layers, and the first
/// to claim a piece of the map keeps it. That makes placement a function of the
/// source tile and the zoom alone, so every output tile magnified from one
/// parent reaches the same answer and a name spanning a tile edge is drawn
/// consistently by both sides rather than by one and not the other.</summary>
public sealed class LabelCollisionMap(double padding = 2.0)
{
    private readonly List<LabelBox> _placed = [];

    /// <summary>Boxes that won their space, in the order they were accepted.</summary>
    public IReadOnlyList<LabelBox> Placed => _placed;

    public int Count => _placed.Count;

    /// <summary>Claims the space for a label, or reports that something already
    /// has it. A box with no area never collides and is never placed, so an
    /// empty label cannot block a real one.</summary>
    public bool TryPlace(in LabelBox box)
    {
        if (box.Width <= 0 || box.Height <= 0) return false;

        var padded = box.Inflated(padding);
        foreach (var other in _placed)
            if (padded.Intersects(other)) return false;

        _placed.Add(padded);
        return true;
    }

    public void Clear() => _placed.Clear();
}
