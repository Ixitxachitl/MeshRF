// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Which lock a waypoint carries, as the list's lock column reads it. The
/// column is a glyph and two dots over these three flags, so getting them
/// right is the whole of it.
/// </summary>
public class WaypointLockTests
{
    private const uint Us = 0x11111111;
    private const uint Them = 0xa1b2c3d4;

    private static WaypointRecord Waypoint(uint lockedTo, uint viewer = Us) =>
        new() { Name = "Trailhead", LockedTo = lockedTo, ViewerNodeNum = viewer };

    [Fact]
    public void An_Unlocked_Waypoint_Shows_Nothing()
    {
        var wp = Waypoint(lockedTo: 0);

        Assert.False(wp.IsLocked);
        Assert.False(wp.IsLockedToUs);
        Assert.False(wp.IsLockedToAnother);
    }

    [Fact]
    public void Our_Own_Lock_Reads_As_Ours()
    {
        var wp = Waypoint(lockedTo: Us);

        Assert.True(wp.IsLocked);
        Assert.True(wp.IsLockedToUs);
        Assert.False(wp.IsLockedToAnother);
    }

    [Fact]
    public void Somebody_Elses_Lock_Reads_As_Theirs()
    {
        var wp = Waypoint(lockedTo: Them);

        Assert.True(wp.IsLocked);
        Assert.False(wp.IsLockedToUs);
        Assert.True(wp.IsLockedToAnother);
        Assert.Equal("!a1b2c3d4", wp.LockedToId);
    }

    [Fact]
    public void The_Two_Dots_Are_Never_Both_Shown()
    {
        // They drive two Ellipses in the same cell, so an overlap would draw
        // one on top of the other rather than fail visibly.
        foreach (uint lockedTo in new[] { 0u, Us, Them, 0xdeadbeefu })
        {
            var wp = Waypoint(lockedTo);
            Assert.False(wp.IsLockedToUs && wp.IsLockedToAnother);
            Assert.Equal(wp.IsLocked, wp.IsLockedToUs || wp.IsLockedToAnother);
        }
    }

    [Fact]
    public void Taking_A_New_Identity_Moves_The_Lock_To_Us()
    {
        // The host re-stamps every row when this node's number changes, and a
        // lock that named the number we have just taken is now our own.
        var wp = Waypoint(lockedTo: Them, viewer: Us);
        Assert.True(wp.IsLockedToAnother);

        wp.ViewerNodeNum = Them;

        Assert.True(wp.IsLockedToUs);
        Assert.False(wp.IsLockedToAnother);
    }

    [Fact]
    public void An_Unknown_Viewer_Makes_Every_Lock_Somebody_Elses()
    {
        // Before this node has an identity there is no lock that can be ours,
        // and claiming one would offer an edit that firmware would ignore.
        var wp = Waypoint(lockedTo: Them, viewer: 0);

        Assert.True(wp.IsLockedToAnother);
        Assert.False(wp.IsLockedToUs);
    }
}
