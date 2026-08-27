// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

public class LabelCollisionTests
{
    // -- Boxes --------------------------------------------------------------

    [Fact]
    public void CenteredBoxSurroundsItsPoint()
    {
        var b = LabelBox.Centered(100, 50, 40, 10);
        Assert.Equal(80, b.X);
        Assert.Equal(45, b.Y);
        Assert.Equal(120, b.Right);
        Assert.Equal(55, b.Bottom);
    }

    [Fact]
    public void BoxesTouchingEdgeToEdgeDoNotIntersect()
    {
        var a = new LabelBox(0, 0, 10, 10);
        var b = new LabelBox(10, 0, 10, 10);
        Assert.False(a.Intersects(b));
        Assert.False(b.Intersects(a));

        var overlapping = new LabelBox(9.5, 0, 10, 10);
        Assert.True(a.Intersects(overlapping));
    }

    [Fact]
    public void TileIntersectionFindsLabelsHangingOverAnEdge()
    {
        // A label straddling the boundary between two tiles must be seen by
        // both, so each can draw its half.
        var straddling = new LabelBox(250, 100, 40, 12);

        Assert.True(straddling.IntersectsTile(0, 0, 256));       // left tile
        Assert.True(straddling.IntersectsTile(256, 0, 256));     // right tile
        Assert.False(straddling.IntersectsTile(0, 256, 256));    // below
    }

    // -- Placement ----------------------------------------------------------

    [Fact]
    public void TheFirstLabelToClaimSpaceKeepsIt()
    {
        var map = new LabelCollisionMap(padding: 0);

        Assert.True(map.TryPlace(new LabelBox(0, 0, 50, 10)));
        Assert.False(map.TryPlace(new LabelBox(25, 0, 50, 10)));   // overlaps
        Assert.True(map.TryPlace(new LabelBox(60, 0, 50, 10)));    // clear
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void PaddingKeepsLabelsApartEvenWhenTheirBoxesDoNot()
    {
        var padded = new LabelCollisionMap(padding: 4);
        Assert.True(padded.TryPlace(new LabelBox(0, 0, 10, 10)));
        // Six pixels clear: fine unpadded, too close once padded.
        Assert.False(padded.TryPlace(new LabelBox(16, 0, 10, 10)));

        var bare = new LabelCollisionMap(padding: 0);
        Assert.True(bare.TryPlace(new LabelBox(0, 0, 10, 10)));
        Assert.True(bare.TryPlace(new LabelBox(16, 0, 10, 10)));
    }

    [Fact]
    public void AnEmptyBoxIsNeitherPlacedNorAllowedToBlock()
    {
        var map = new LabelCollisionMap(padding: 0);

        Assert.False(map.TryPlace(new LabelBox(0, 0, 0, 10)));
        Assert.False(map.TryPlace(new LabelBox(0, 0, 10, 0)));
        Assert.Equal(0, map.Count);

        // The space it would have taken is still free.
        Assert.True(map.TryPlace(new LabelBox(0, 0, 10, 10)));
    }

    [Fact]
    public void PlacementIsDeterministicForTheSameOfferOrder()
    {
        LabelBox[] offers =
        [
            new(0, 0, 50, 10), new(25, 0, 50, 10), new(60, 0, 50, 10),
            new(0, 5, 50, 10), new(200, 200, 30, 10),
        ];

        static bool[] Run(LabelBox[] boxes)
        {
            var map = new LabelCollisionMap(padding: 2);
            var results = new bool[boxes.Length];
            for (int i = 0; i < boxes.Length; i++) results[i] = map.TryPlace(boxes[i]);
            return results;
        }

        // Two tiles drawn from the same parent must reach the same answer,
        // or a name crossing their shared edge appears on one side only.
        Assert.Equal(Run(offers), Run(offers));
    }

    [Fact]
    public void ClearReleasesEverything()
    {
        var map = new LabelCollisionMap(padding: 0);
        map.TryPlace(new LabelBox(0, 0, 50, 10));
        map.Clear();

        Assert.Equal(0, map.Count);
        Assert.True(map.TryPlace(new LabelBox(0, 0, 50, 10)));
    }

    [Fact]
    public void PlacedBoxesCarryTheirPaddingSoCallersSeeTheClaimedSpace()
    {
        var map = new LabelCollisionMap(padding: 3);
        map.TryPlace(new LabelBox(10, 10, 20, 20));

        var placed = Assert.Single(map.Placed);
        Assert.Equal(7, placed.X);
        Assert.Equal(26, placed.Width);
    }
}
