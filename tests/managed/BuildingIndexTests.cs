// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// What a path meets on its way through the buildings, and what that costs.
/// </summary>
public class BuildingIndexTests
{
    private static readonly GeoPoint Origin = new(44.9778, -93.2650);

    /// <summary>A square footprint of a given size, centred a distance east of
    /// the origin.</summary>
    private static Footprint Block(double eastM, double sizeM)
    {
        var centre = CoverageMap.Along(Origin, 90, eastM);
        double half = sizeM / 2;

        var nw = CoverageMap.Along(CoverageMap.Along(centre, 0, half), 270, half);
        var ne = CoverageMap.Along(CoverageMap.Along(centre, 0, half), 90, half);
        var se = CoverageMap.Along(CoverageMap.Along(centre, 180, half), 90, half);
        var sw = CoverageMap.Along(CoverageMap.Along(centre, 180, half), 270, half);

        return new Footprint([nw, ne, se, sw]);
    }

    /// <summary>A path due east, through where the blocks are.</summary>
    private static (GeoPoint From, GeoPoint To) EastTo(double metres) =>
        (Origin, CoverageMap.Along(Origin, 90, metres));

    [Fact]
    public void APathThroughOpenGroundCrossesNothing()
    {
        var index = new BuildingIndex([Block(500, 40)]);
        var (from, to) = (Origin, CoverageMap.Along(Origin, 0, 1000)); // due north

        Assert.Equal(BuildingCrossing.None, index.AlongPath(from, to));
    }

    [Fact]
    public void APathThroughOneBuildingCrossesItOnce()
    {
        var index = new BuildingIndex([Block(500, 40)]);
        var (from, to) = EastTo(1000);

        var crossing = index.AlongPath(from, to);

        Assert.Equal(1, crossing.Count);
        Assert.InRange(crossing.MetresInside, 20, 60);
    }

    [Fact]
    public void EachBuildingOnThePathIsCountedOnce()
    {
        var index = new BuildingIndex([Block(300, 40), Block(600, 40), Block(900, 40)]);

        Assert.Equal(3, index.AlongPath(EastTo(1200).From, EastTo(1200).To).Count);
    }

    [Fact]
    public void AWallIsChargedOnceHoweverFinelyThePathIsWalked()
    {
        // The property that makes this safe to accumulate along a sweep: a
        // step that begins inside a footprint has not entered it again.
        var index = new BuildingIndex([Block(500, 120)]);
        var (from, to) = EastTo(1000);

        Assert.Equal(1, index.AlongPath(from, to, stepM: 100).Count);
        Assert.Equal(1, index.AlongPath(from, to, stepM: 10).Count);
        Assert.Equal(1, index.AlongPath(from, to, stepM: 2).Count);
    }

    [Fact]
    public void ALongerWayThroughCostsMoreInside()
    {
        var narrow = new BuildingIndex([Block(500, 40)]).AlongPath(EastTo(1000).From, EastTo(1000).To);
        var wide = new BuildingIndex([Block(500, 200)]).AlongPath(EastTo(1000).From, EastTo(1000).To);

        Assert.Equal(narrow.Count, wide.Count);
        Assert.True(wide.MetresInside > narrow.MetresInside * 3,
            $"200 m of building gave {wide.MetresInside:F0} m against {narrow.MetresInside:F0} m");
    }

    [Fact]
    public void APathStoppingShortOfABuildingNeverReachesIt()
    {
        var index = new BuildingIndex([Block(900, 40)]);

        Assert.Equal(0, index.AlongPath(EastTo(500).From, EastTo(500).To).Count);
    }

    [Fact]
    public void APathOfNoLengthCrossesNothing()
    {
        var index = new BuildingIndex([Block(500, 40)]);

        Assert.Equal(BuildingCrossing.None, index.AlongPath(Origin, Origin));
    }

    [Fact]
    public void AnEmptyIndexIsFreeToAsk()
    {
        Assert.Equal(BuildingCrossing.None, BuildingIndex.Empty.AlongPath(EastTo(5000).From, EastTo(5000).To));
        Assert.Equal(0, BuildingIndex.Empty.Count);
    }

    // -- Point in footprint -------------------------------------------------

    [Fact]
    public void TheMiddleOfAFootprintIsInsideItAndTheOutsideIsNot()
    {
        var block = Block(500, 100);

        Assert.True(BuildingIndex.Contains(block, CoverageMap.Along(Origin, 90, 500)));
        Assert.False(BuildingIndex.Contains(block, CoverageMap.Along(Origin, 90, 800)));
        Assert.False(BuildingIndex.Contains(block, CoverageMap.Along(Origin, 0, 500)));
    }

    [Fact]
    public void AConcaveFootprintDoesNotSwallowItsOwnCourtyard()
    {
        // A U-shaped building: the gap between the arms is outside, which a
        // bounding box or a convex test would get wrong.
        GeoPoint At(double eastM, double northM) =>
            CoverageMap.Along(CoverageMap.Along(Origin, 90, eastM), 0, northM);

        var u = new Footprint([
            At(0, 0), At(100, 0), At(100, 100), At(70, 100),
            At(70, 30), At(30, 30), At(30, 100), At(0, 100),
        ]);

        Assert.True(BuildingIndex.Contains(u, At(15, 60)));   // in the left arm
        Assert.True(BuildingIndex.Contains(u, At(85, 60)));   // in the right arm
        Assert.False(BuildingIndex.Contains(u, At(50, 70)));  // in the gap between
    }

    // -- Loss ---------------------------------------------------------------

    [Fact]
    public void EveryCrossingCostsTheFlatCharge()
    {
        var model = new BuildingLossModel(PerCrossingDb: 10.8, PerHundredMetresInsideDb: 0);

        Assert.Equal(0, model.LossDb(BuildingCrossing.None));
        Assert.Equal(10.8, model.LossDb(new BuildingCrossing(1, 0)), 6);
        Assert.Equal(32.4, model.LossDb(new BuildingCrossing(3, 0)), 6);
    }

    [Fact]
    public void DistanceInsideCostsOnTopOfIt()
    {
        var model = new BuildingLossModel(PerCrossingDb: 10, PerHundredMetresInsideDb: 0.3);

        Assert.Equal(10.6, model.LossDb(new BuildingCrossing(1, 200)), 6);
    }

    [Fact]
    public void TheDefaultsAreTheOnesTheyWereFittedFrom()
    {
        // MeshLab RF's paired field survey. Recorded here so a change to them
        // is a deliberate act rather than a drift.
        var model = new BuildingLossModel();

        Assert.Equal(10.8, model.PerCrossingDb);
        Assert.Equal(0.3, model.PerHundredMetresInsideDb);
    }
}
