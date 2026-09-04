// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Waypoints;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Waypoint.expire "never expires" semantics: the official Meshtastic clients
/// use Int.MAX_VALUE (2147483647) for a fresh waypoint with no expiration, but
/// still treat 0 as "never expires" too for backward compatibility with older
/// waypoints. WaypointRecord must recognise both as non-expiring.
/// </summary>
public class WaypointRecordTests
{
    private static WaypointRecord MakeWaypoint(uint expireEpoch) => new()
    {
        FromNode = 1,
        WaypointId = 1,
        Latitude = 1,
        Longitude = 1,
        ExpireEpoch = expireEpoch,
    };

    [Fact]
    public void ZeroExpireNeverExpires()
    {
        var wp = MakeWaypoint(0);
        Assert.False(wp.HasExpiry);
        Assert.False(wp.IsExpired);
        Assert.Null(wp.ExpireTime);
        Assert.Equal("ACTIVE", wp.ExpiryStatus);
    }

    [Fact]
    public void SentinelMaxValueNeverExpires()
    {
        var wp = MakeWaypoint(WaypointRecord.NeverExpiresEpoch);
        Assert.False(wp.HasExpiry);
        Assert.False(wp.IsExpired);
        Assert.Null(wp.ExpireTime);
        Assert.Equal("ACTIVE", wp.ExpiryStatus);
    }

    [Fact]
    public void PastEpochIsExpired()
    {
        var wp = MakeWaypoint(1); // 1970-01-01T00:00:01Z — always in the past
        Assert.True(wp.HasExpiry);
        Assert.True(wp.IsExpired);
        Assert.NotNull(wp.ExpireTime);
        Assert.Equal("EXPIRED", wp.ExpiryStatus);
    }

    [Fact]
    public void FutureEpochIsNotExpired()
    {
        uint future = (uint)DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var wp = MakeWaypoint(future);
        Assert.True(wp.HasExpiry);
        Assert.False(wp.IsExpired);
        Assert.NotNull(wp.ExpireTime);
        Assert.Equal("ACTIVE", wp.ExpiryStatus);
    }

    /// <summary>
    /// The channel a waypoint is tied to, as the list and the map tooltip show
    /// it. Not decoration: this is the name a resend looks up and the room a
    /// geofence crossing is posted into, so a row that cannot say which channel
    /// it belongs to cannot explain either failing.
    /// </summary>
    [Fact]
    public void ChannelTextNamesThePrimaryByRoleWhenTheRecordHasNoName()
    {
        // A default-preset primary has no name of its own, so the field is
        // legitimately empty on a marker that came in on one.
        Assert.Equal("(primary)", new WaypointRecord { Channel = "" }.ChannelText);
        Assert.Equal("(primary)", new WaypointRecord { Channel = "   " }.ChannelText);

        Assert.Equal("LongFast", new WaypointRecord { Channel = "LongFast" }.ChannelText);
    }

    /// <summary>
    /// The recipient of a directed marker. Says who draws it, not who can read
    /// it — a directed waypoint still travels under a channel key, so it is
    /// independent of both the channel and the lock.
    /// </summary>
    [Fact]
    public void ADirectedWaypointNamesItsRecipient()
    {
        var directed = new WaypointRecord { Channel = "LongFast", ToNode = 0xa1b2c3d4 };

        Assert.True(directed.IsDirected);
        Assert.Equal("!a1b2c3d4", directed.ToId);
        // The channel it travelled under is still the channel.
        Assert.Equal("LongFast", directed.ChannelText);
    }

    [Fact]
    public void ABroadcastWaypointNamesNobody()
    {
        var broadcast = new WaypointRecord { Channel = "LongFast" };

        Assert.False(broadcast.IsDirected);
        // Empty rather than "!00000000": the column should be blank on the
        // rows that went to a whole channel, which is most of them.
        Assert.Equal(string.Empty, broadcast.ToId);
    }

    /// <summary>Addressing and locking are different questions, and a marker
    /// can answer them differently.</summary>
    [Fact]
    public void AddressingIsIndependentOfTheLock()
    {
        var wp = new WaypointRecord
        {
            ToNode = 0xa1b2c3d4,
            LockedTo = 0xdeadbeef,
            ViewerNodeNum = 0xdeadbeef,
        };

        Assert.Equal("!a1b2c3d4", wp.ToId);
        Assert.True(wp.IsLockedToUs);
    }
}
