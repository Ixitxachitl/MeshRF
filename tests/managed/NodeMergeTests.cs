// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Recognising one radio under two node numbers. Firmware 2.8 derives the
/// number from the public key instead of the MAC, so an upgrading node
/// reappears under a new number and leaves a ghost of the old one on every
/// other node in the mesh — <c>NodeDB::createNewIdentity</c> cleans up only its
/// own database.
///
/// The trap is the opposite mistake: public keys do get duplicated across
/// unrelated radios (a restored backup, a bad keygen), and merging on the key
/// alone would collapse a whole cluster of real nodes into one. The MAC is what
/// keeps the two cases apart, and we can hold one because we take every
/// NodeInfo off the air rather than out of a node's NodeDB.
/// </summary>
public class NodeMergeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _db;

    public NodeMergeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "meshrf-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "nodes.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A key and the node number 2.8 derives from it.</summary>
    private static (string Hex, uint NodeNum) Identity()
    {
        var pub = Curve25519.GetPublicKey(Curve25519.GeneratePrivateKey());
        Assert.True(PkiNodeNumber.TryFromPublicKey(pub, out var num));
        return (Convert.ToHexString(pub), num);
    }

    private static NodeRecord Node(uint num, string name, string mac = "",
                                   string key = "", long lastHeard = 1_788_000_000) => new()
    {
        NodeNum = num,
        LongName = name,
        ShortName = name.Length >= 4 ? name[..4] : name,
        MacAddress = mac,
        PublicKey = key,
        LastHeardEpoch = lastHeard,
    };

    private static NodeTelemetryHistoryRecord Telemetry(uint num, DateTime when, double battery) => new(
        Id: 0, NodeNum: num, TimestampUtc: when,
        BatteryPct: battery, VoltageV: 4.0,
        ChannelUtilPct: null, AirUtilTxPct: null, UptimeSeconds: null,
        TemperatureC: null, RelativeHumidityPct: null, BarometricPressureHpa: null,
        GasResistanceMohm: null, IaqValue: null,
        Pm10Standard: null, Pm25Standard: null, Pm100Standard: null,
        Pm10Environmental: null, Pm25Environmental: null, Pm100Environmental: null,
        Ch1VoltageV: null, Ch1CurrentMa: null, Ch2VoltageV: null, Ch2CurrentMa: null,
        Ch3VoltageV: null, Ch3CurrentMa: null, Signature: $"sig-{num}-{battery}");

    // --- what counts as one radio ------------------------------------------

    [Fact]
    public void OneMacUnderTwoNumbersIsOneRadio()
    {
        // The live case: zyppr-downtown-base, heard on the number its MAC gives
        // and again on the one its key gives, still advertising the same MAC.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "zyppr-downtown-base", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "zyppr-downtown-base", "90:70:69:83:22:34", id.Hex));

        var merges = store.MergeDuplicates();

        Assert.Single(merges);
        Assert.Equal(NodeIdentityMatch.MacAddress, merges[0].Match);
        // The number the key derives is the identity on air, whatever was
        // heard last: neighbours go on relaying the ghost for a while.
        Assert.Equal(id.NodeNum, merges[0].Survivor);
        Assert.Equal(0x69832234u, merges[0].Retired);
        Assert.Equal(1, store.Count());
        Assert.Equal(id.NodeNum, store.All().Single().NodeNum);
    }

    [Fact]
    public void DifferentMacsAreNeverOneRadio()
    {
        // Five real nodes shipping one duplicated public key, each with its own
        // MAC. Merging on the key alone would fuse the lot.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x5b50988f, "Home", "c1:d3:5b:50:98:8f", id.Hex));
        store.Upsert(Node(0x61474ffb, "Home", "d5:3e:61:47:4f:fb", id.Hex));
        store.Upsert(Node(id.NodeNum, "Home", "e3:ec:b5:14:34:60", id.Hex));

        Assert.Empty(store.MergeDuplicates());
        Assert.Equal(3, store.Count());
    }

    [Fact]
    public void AMissingMacDoesNotStandInForADifferentOne()
    {
        // Unknown is not "different": a row recorded before we stored the MAC
        // has to stay mergeable, or the ghosts we already hold never clear.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x0a765833, "Paradise", mac: "", key: id.Hex));
        store.Upsert(Node(id.NodeNum, "Paradise", mac: "de:e6:0a:76:58:33", key: id.Hex));

        var merges = store.MergeDuplicates();

        Assert.Single(merges);
        Assert.Equal(NodeIdentityMatch.PkiUpgrade, merges[0].Match);
        Assert.Equal(id.NodeNum, merges[0].Survivor);
        // And the MAC the surviving row was missing comes across with it.
        Assert.Equal("de:e6:0a:76:58:33", store.Get(id.NodeNum)!.MacAddress);
    }

    [Fact]
    public void OneKeyUnderTwoNamesIsTwoRadios()
    {
        // Neither row has a MAC, so only the key and the name are left to go
        // on — and a shared key across two names is the duplicate-key case,
        // not an upgrade.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x7adc46c9, "Paradise USD", key: id.Hex));
        store.Upsert(Node(id.NodeNum, "Lovelock", key: id.Hex));

        Assert.Empty(store.MergeDuplicates());
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void OneKeyWithNeitherNumberDerivedFromItIsTwoRadios()
    {
        // Two nodes that both landed on a random number after a collision. One
        // key, one name, but nothing that looks like an upgrade — and no MAC to
        // settle it, so leave them alone.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(id.NodeNum ^ 1u, "Alta Orange", key: id.Hex));
        store.Upsert(Node(id.NodeNum ^ 2u, "Alta Orange", key: id.Hex));

        Assert.Empty(store.MergeDuplicates());
    }

    [Fact]
    public void ANamelessRowIsNotMergedOnItsKeyAlone()
    {
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x11223344, "", key: id.Hex));
        store.Upsert(Node(id.NodeNum, "", key: id.Hex));

        Assert.Empty(store.MergeDuplicates());
    }

    // --- what a merge carries across ---------------------------------------

    [Fact]
    public void MergeMovesBothHistoriesOntoTheSurvivor()
    {
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.AddLocationHistory(0x69832234, new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc),
                                 39.05, -121.07, altitudeM: 400);
        store.AddTelemetryHistory(Telemetry(0x69832234, new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc), 80));
        store.AddTelemetryHistory(Telemetry(id.NodeNum, new DateTime(2026, 9, 4, 9, 0, 0, DateTimeKind.Utc), 62));

        store.MergeDuplicates();

        Assert.Single(store.LocationHistory(id.NodeNum));
        Assert.Equal(2, store.TelemetryHistory(id.NodeNum).Count);
        Assert.Empty(store.TelemetryHistory(0x69832234));
    }

    [Fact]
    public void MergeKeepsTheEarlierFirstHeard()
    {
        var id = Identity();
        using var store = new NodeStore(_db);
        // first_heard_epoch is written from the timestamp on the insert that
        // creates the row, so the ghost's is the older of the two.
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex, lastHeard: 1_788_000_000));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex, lastHeard: 1_788_500_000));

        store.MergeDuplicates();

        Assert.Equal(1_788_000_000, store.Get(id.NodeNum)!.FirstHeardEpoch);
    }

    [Fact]
    public void MergeCarriesTheChoicesTheUserMadeAboutTheGhost()
    {
        // Muting a radio means muting the radio, not the number it used to use.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.SetMuteRtttl(0x69832234, true);
        store.SetFavorite(0x69832234, true);

        store.MergeDuplicates();

        var kept = store.Get(id.NodeNum)!;
        Assert.True(kept.MuteRtttl);
        Assert.True(kept.Favorite);
    }

    [Fact]
    public void MergeDoesNotOverwriteTheSurvivorWithTheGhostsLastReading()
    {
        // The ghost is still being relayed by neighbours that have not caught
        // up, so its row can hold a position and a battery level. They describe
        // an old packet; the surviving row is the identity actually on air.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(new NodeRecord
        {
            NodeNum = 0x69832234, LongName = "Ridge", MacAddress = "90:70:69:83:22:34",
            PublicKey = id.Hex, Latitude = 39.0, Longitude = -121.0, BatteryPct = 80,
            HwModel = "HELTEC_V4", LastHeardEpoch = 1_788_000_000,
        });
        store.Upsert(new NodeRecord
        {
            NodeNum = id.NodeNum, LongName = "Ridge", MacAddress = "90:70:69:83:22:34",
            PublicKey = id.Hex, Latitude = 39.5, Longitude = -121.5, BatteryPct = 62,
            LastHeardEpoch = 1_788_500_000,
        });

        store.MergeDuplicates();

        var kept = store.Get(id.NodeNum)!;
        Assert.Equal(39.5, kept.Latitude);
        Assert.Equal((byte)62, kept.BatteryPct);
        // An identity field the surviving row never learned does come across.
        Assert.Equal("HELTEC_V4", kept.HwModel);
    }

    // --- the alias the merge leaves behind ----------------------------------

    [Fact]
    public void TheRetiredNumberResolvesToTheSurvivor()
    {
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));

        store.MergeDuplicates();

        Assert.Equal(id.NodeNum, store.Resolve(0x69832234));
        Assert.Equal([0x69832234u], store.AliasesOf(id.NodeNum));
        // A number nothing was merged into comes back as itself.
        Assert.Equal(0xdeadbeefu, store.Resolve(0xdeadbeef));
    }

    [Fact]
    public void ASecondMergeRepointsTheFirstAliasRatherThanChainingIt()
    {
        // A radio that renumbers twice: the first ghost has to end up pointing
        // at the final row, or Resolve would have to walk a chain.
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x11111111, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 1));
        store.Upsert(Node(0x22222222, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 2));
        store.MergeDuplicates();

        store.Upsert(Node(0x33333333, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 3));
        store.MergeDuplicates();

        Assert.Equal(0x33333333u, store.Resolve(0x11111111));
        Assert.Equal(0x33333333u, store.Resolve(0x22222222));
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void ARetiredNumberIsReleasedWhenItAnswersWithADifferentMac()
    {
        // A MAC is only what a node claims, and the mesh does contain nodes
        // claiming made-up ones — so a merge can be wrong. When the retired
        // number turns out to be a different radio after all, it has to be able
        // to come back, or one bad merge would silently swallow it for good.
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x11111111, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 2));
        store.Upsert(Node(0x22222222, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 1));
        store.MergeDuplicates();
        Assert.Equal(0x11111111u, store.Resolve(0x22222222));

        store.Upsert(Node(0x22222222, "Impostor", "11:22:33:44:55:66", lastHeard: 9));

        Assert.Equal(0x22222222u, store.Resolve(0x22222222));
        Assert.Equal(2, store.Count());
        Assert.Equal("Impostor", store.Get(0x22222222)!.LongName);
    }

    [Fact]
    public void AnOrdinaryPacketDoesNotReleaseARetiredNumber()
    {
        // Only contradicting identity releases it. A relayed position or
        // telemetry packet carries neither a MAC nor a key, and must not be
        // read as evidence of anything.
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x11111111, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 2));
        store.Upsert(Node(0x22222222, "Wanderer", "aa:bb:cc:dd:ee:ff", lastHeard: 1));
        store.MergeDuplicates();

        store.Upsert(new NodeRecord { NodeNum = 0x22222222, Latitude = 39.0, Longitude = -121.0 });

        Assert.Equal(0x11111111u, store.Resolve(0x22222222));
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void MergingOneNodeLeavesEveryOtherRowAlone()
    {
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(0xdeadbeef, "Valley Base", "01:02:03:04:05:06"));

        store.MergeDuplicatesOf(id.NodeNum);

        Assert.NotNull(store.Get(0xdeadbeef));
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void APacketOnTheRetiredNumberLandsOnTheSurvivorInsteadOfRaisingTheGhost()
    {
        // Neighbours that have not caught up go on relaying the old identity,
        // so this is the ordinary case, not an edge one: without it every
        // relayed packet would insert the row the merge just removed.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.MergeDuplicates();

        store.RecordSighting(0x69832234, rssiDbm: -101, snrDb: -7.5f, hopsAway: 3);
        store.AddLocationHistory(0x69832234, new DateTime(2026, 9, 5, 8, 0, 0, DateTimeKind.Utc),
                                 39.05, -121.07, altitudeM: 400);

        Assert.Equal(1, store.Count());
        var kept = store.Get(id.NodeNum)!;
        Assert.Equal(-101, kept.RssiDbm);
        Assert.Single(store.LocationHistory(id.NodeNum));
    }

    [Fact]
    public void TheGhostsNumberStillFindsTheNode()
    {
        // A stored message, a script or an open tab can hold the old number.
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.MergeDuplicates();

        Assert.Equal(id.NodeNum, store.Get(0x69832234)!.NodeNum);
    }

    [Fact]
    public void AliasesSurviveARestart()
    {
        var id = Identity();
        using (var store = new NodeStore(_db))
        {
            store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
            store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));
            store.MergeDuplicates();
        }

        using var reopened = new NodeStore(_db);
        Assert.Equal(id.NodeNum, reopened.Resolve(0x69832234));
        reopened.RecordSighting(0x69832234, rssiDbm: -99);
        Assert.Equal(1, reopened.Count());
    }

    [Fact]
    public void FindDuplicatesReportsWithoutChangingAnything()
    {
        var id = Identity();
        using var store = new NodeStore(_db);
        store.Upsert(Node(0x69832234, "Ridge", "90:70:69:83:22:34", id.Hex));
        store.Upsert(Node(id.NodeNum, "Ridge", "90:70:69:83:22:34", id.Hex));

        Assert.Single(store.FindDuplicates());
        Assert.Equal(2, store.Count());
        Assert.Equal(0x69832234u, store.Resolve(0x69832234));
    }
}
