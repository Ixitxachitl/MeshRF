// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The reconciliation a feed sync exists for: place what is new, resend what
/// changed, retire what has gone.
/// </summary>
public class FeedSyncTests
{
    private const string FileName = "fires.yaml";
    private static readonly DateTimeOffset Noon = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    // Truckee-ish, with a nearby fire ~12 km away and a far one ~600 km away.
    private static readonly ScriptSelf Home = new(1, "ME", "My Node", 101, 39.20882, -120.79279);

    /// <summary>
    /// The sample, with LF endings whatever the file on disk uses. Tests below
    /// vary it by replacing whole lines, and a raw literal carries the source
    /// file's own endings — so without this a \n in a search string quietly
    /// matches nothing and the test passes while testing the unmodified sample.
    /// </summary>
    private static string Yaml => RawYaml.ReplaceLineEndings("\n");

    private const string RawYaml =
        """
        enabled: true
        alias: Fires
        sync:
          every: 15m
          url: "https://api.example.com/fires"
          items: ""
          id: id
          active: is_active
          lat: lat
          lon: lng
          within: 30mi
          watch:
            - data.acreage
            - data.containment
          waypoint:
            name: "Fire: {item.name}"
            description: "{item.data.acreage} acres, {item.data.containment}% contained"
            icon: "🔥"
            radius: 10mi
        """;

    private static FeedSyncEngine Armed(string? yaml = null, FeedSyncStore? store = null,
                                        DateTimeOffset? at = null)
    {
        yaml ??= Yaml;
        var parse = ScriptParser.Parse(yaml);
        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.True(parse.IsSync);

        var engine = new FeedSyncEngine(store);
        engine.Load([new ScriptFile(FileName, FileName, yaml, Enabled: true, parse)], at ?? Noon);
        return engine;
    }

    private static string Feed(params string[] fires) => "[" + string.Join(",", fires) + "]";

    /// <summary>A record shaped like Watch Duty's: lat/lng top level, acreage
    /// and containment nested, plus a date_modified nothing watches.</summary>
    private static string Fire(int id, string name, bool active = true,
                               double lat = 39.31, double lng = -120.84,
                               int acreage = 1200, int containment = 35,
                               bool prescribed = false)
    {
        // Built by concatenation rather than a raw literal: JSON's closing
        // braces collide with interpolation delimiters in every $-count.
        string N(double d) => d.ToString(CultureInfo.InvariantCulture);
        return "{\"id\":" + id
             + ",\"is_active\":" + (active ? "true" : "false")
             + ",\"name\":\"" + name + "\""
             + ",\"lat\":" + N(lat)
             + ",\"lng\":" + N(lng)
             + ",\"date_modified\":\"whenever\""
             + ",\"data\":{\"acreage\":" + acreage + ",\"containment\":" + containment
             + ",\"is_prescribed\":" + (prescribed ? "true" : "false") + "}}";
    }

    // ----- parsing ------------------------------------------------------------

    [Fact]
    public void A_Sync_File_Parses_As_A_Sync_Not_A_Script()
    {
        var parse = ScriptParser.Parse(Yaml);

        Assert.True(parse.IsSync);
        Assert.Null(parse.Script);
        Assert.Equal("Fires", parse.Alias);
        Assert.True(parse.Enabled);

        var sync = parse.Sync!;
        Assert.Equal(TimeSpan.FromMinutes(15), sync.Every);
        Assert.Equal("is_active", sync.ActivePath);
        Assert.Equal(48280, sync.WithinMetres!.Value, 0);
        Assert.Equal(["data.acreage", "data.containment"], sync.WatchPaths.ToArray());
        // Unlocked by default, unlike a script's waypoint: these outlive this
        // node's attention, so recipients must be able to clear them.
        Assert.False(sync.Waypoint.LockToMe);
    }

    [Fact]
    public void A_Marker_Deleted_From_The_Map_Is_Placed_Again_While_The_Record_Lives()
    {
        // The reported case: a fire mirrored, its marker deleted by hand, the
        // fire still burning and unchanged. Without a presence check the
        // record is present, the fingerprint matches and nothing is ever
        // resent, so the marker never comes back.
        var engine = Armed();
        var placed = new HashSet<uint>();
        engine.IsStillPlaced = id => placed.Contains(id);

        var action = Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));
        Assert.Equal(FeedSyncActionKind.Place, action.Kind);
        placed.Add(action.WaypointId);

        // Nothing to do while it is still on the map.
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(15)));

        placed.Remove(action.WaypointId);   // deleted from the waypoint list

        var again = Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(30)));
        Assert.Equal(FeedSyncActionKind.Place, again.Kind);
        Assert.Equal(action.WaypointId, again.WaypointId);
    }

    [Fact]
    public void An_Immutable_Feeds_Marker_Comes_Back_Too()
    {
        // watch: [] says a record never changes, so its marker is placed once
        // and never resent. Deleting it by hand is still worth undoing.
        var yaml = Yaml.Replace("  watch:\n    - data.acreage\n    - data.containment\n", "  watch: []\n");
        var engine = Armed(yaml);
        var placed = new HashSet<uint>();
        engine.IsStillPlaced = id => placed.Contains(id);

        var action = Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));
        placed.Add(action.WaypointId);
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(15)));

        placed.Remove(action.WaypointId);

        Assert.Equal(FeedSyncActionKind.Place,
            Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(30))).Kind);
    }

    [Fact]
    public void Without_The_Presence_Check_Nothing_Changes()
    {
        // No hook wired is the tests' and the headless case: memory alone
        // decides, exactly as before.
        var engine = Armed();
        engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon);

        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(30)));
    }

    [Fact]
    public void Forgetting_A_Feed_Places_Everything_Again()
    {
        var engine = Armed();
        Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(15)));

        engine.Forget(FileName, Noon.AddMinutes(20));

        var action = Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(20)));
        Assert.Equal(FeedSyncActionKind.Place, action.Kind);
    }

    [Fact]
    public void Forgetting_A_Feed_Brings_Its_Next_Poll_Forward()
    {
        // Asking for a resync and then waiting a quarter of an hour reads as
        // nothing having happened.
        var engine = Armed();
        Assert.Single(engine.Due(Noon));                       // the first poll
        Assert.Empty(engine.Due(Noon.AddMinutes(1)));          // not due again yet

        engine.Forget(FileName, Noon.AddMinutes(1));

        Assert.Single(engine.Due(Noon.AddMinutes(1)));
    }

    [Fact]
    public void A_Sync_Can_Name_The_Channel_Its_Markers_Go_Out_On()
    {
        var parse = ScriptParser.Parse(Yaml.Replace("  waypoint:\n", "  waypoint:\n    channel: Fires\n"));

        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.Equal("Fires", parse.Sync!.Waypoint.Channel);
    }

    [Fact]
    public void A_Sync_Can_Address_Its_Markers_To_One_Node()
    {
        var parse = ScriptParser.Parse(
            Yaml.Replace("  waypoint:\n", "  waypoint:\n    to: \"!a1b2c3d4\"\n"));

        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.Equal("!a1b2c3d4", parse.Sync!.Waypoint.To);
    }

    [Fact]
    public void A_Sync_Can_Set_The_Hop_Limit_Its_Markers_Go_Out_At()
    {
        // A feed places far more frames than a script does, so the hops it does
        // not need are the ones most worth not spending.
        var parse = ScriptParser.Parse(Yaml.Replace("  waypoint:\n", "  waypoint:\n    hops: 2\n"));

        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.Equal((byte)2, parse.Sync!.Waypoint.Hops);
    }

    [Fact]
    public void A_Syncs_To_Cannot_Be_A_Placeholder()
    {
        // A feed places its markers unprompted, so there is no message for one
        // to come from — it would expand to nothing on every poll.
        var parse = ScriptParser.Parse(
            Yaml.Replace("  waypoint:\n", "  waypoint:\n    to: \"{from.id}\"\n"));

        Assert.False(parse.IsValid);
        Assert.Contains("literal node id", parse.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Sync_Cannot_Name_A_Node_And_A_Channel_At_Once()
    {
        var parse = ScriptParser.Parse(
            Yaml.Replace("  waypoint:\n", "  waypoint:\n    to: \"!a1b2c3d4\"\n    channel: Fires\n"));

        Assert.False(parse.IsValid);
        Assert.Contains("not both", parse.FirstError!.Value.Message);
    }

    [Fact]
    public void A_Sync_Without_Watch_Warns_That_Nothing_Will_Ever_Update()
    {
        var parse = ScriptParser.Parse(Yaml.Replace(
            "  watch:\n    - data.acreage\n    - data.containment\n", ""));

        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.Contains(parse.Problems, p => p.Message.Contains("never updated"));
    }

    [Fact]
    public void An_Explicit_Empty_Watch_Is_Not_Warned_About()
    {
        // A feed of immutable records — a lightning strike never changes — says
        // so with watch: [], which is different from not having thought about it.
        var parse = ScriptParser.Parse(Yaml.Replace(
            "  watch:\n    - data.acreage\n    - data.containment\n", "  watch: []\n"));

        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.DoesNotContain(parse.Problems, p => p.Message.Contains("never updated"));
    }

    [Theory]
    [InlineData("{time}")]
    [InlineData("{date}")]
    [InlineData("{node.battery}")]
    public void Text_That_Changes_On_Its_Own_Is_Warned_About(string token)
    {
        // One of these re-renders every poll, so every record looks changed and
        // the whole set goes back on the air each time — the thing watch:
        // exists to prevent, arriving by the other door.
        var parse = ScriptParser.Parse(Yaml.Replace(
            "description: \"{item.data.acreage} acres, {item.data.containment}% contained\"",
            $"description: \"seen at {token}\""));

        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.Contains(parse.Problems, p => p.Message.Contains("changes on its own"));
    }

    [Fact]
    public void An_Unchanged_Record_Is_Not_Resent()
    {
        // The regression the warning above guards: with record-derived text
        // only, a poll that brings back the same data says nothing at all.
        var engine = Armed();
        engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon);

        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(15)));
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(30)));
    }

    [Fact]
    public void An_Immutable_Feed_Places_Once_And_Never_Resends()
    {
        // watch: [] says the records are moments, not things with later states.
        // A strike has nothing to catch up with, so a second send could only put
        // the same bytes on the air twice — including the refresh that would
        // otherwise fire at half the expiry.
        var yaml = Yaml
            .Replace("  watch:\n    - data.acreage\n    - data.containment\n", "  watch: []\n")
            .Replace("radius: 10mi", "expires: 1h");
        var parse = ScriptParser.Parse(yaml);
        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.True(parse.Sync!.Immutable);

        var engine = new FeedSyncEngine();
        engine.Load([new ScriptFile(FileName, FileName, yaml, Enabled: true, parse)], Noon);

        Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));

        // Well past half the expiry, where a refresh would normally be due.
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(45)));
        // And even when the record's own fields move.
        Assert.Empty(engine.Reconcile(
            FileName, Feed(Fire(1, "Bear Fire", acreage: 9999)), Home, Noon.AddMinutes(50)));

        // Retirement still works — that is not a resend, it is the end.
        var removed = Assert.Single(engine.Reconcile(FileName, "[]", Home, Noon.AddMinutes(55)));
        Assert.Equal(FeedSyncActionKind.Remove, removed.Kind);
    }

    [Fact]
    public void A_Mutable_Feed_Still_Refreshes_Before_Its_Marker_Lapses()
    {
        // The counterpart: something that can change has to be kept alive, or
        // its marker expires while the thing it describes is still burning.
        var yaml = Yaml.Replace("radius: 10mi", "expires: 1h");
        var parse = ScriptParser.Parse(yaml);
        var engine = new FeedSyncEngine();
        engine.Load([new ScriptFile(FileName, FileName, yaml, Enabled: true, parse)], Noon);

        Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));
        var refreshed = Assert.Single(
            engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(45)));
        Assert.Equal(FeedSyncActionKind.Refresh, refreshed.Kind);
    }

    [Fact]
    public void A_Wrong_Items_Path_Reports_What_Came_Back()
    {
        // A sync has no equivalent of dropping a script's json: block to see the
        // response, so the one message has to carry enough to correct the path.
        var parse = ScriptParser.Parse(Yaml.Replace("items: \"\"", "items: results"));
        var engine = new FeedSyncEngine();
        engine.Load([new ScriptFile(FileName, FileName, "x", Enabled: true, parse)], Noon);

        string? diagnostic = null;
        engine.Diagnostic += d => diagnostic = d;
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));

        Assert.Contains("results is not a list", diagnostic);
        Assert.Contains("Bear Fire", diagnostic);   // the excerpt
    }

    [Fact]
    public void A_Sync_Needs_A_Waypoint_And_A_Position()
    {
        Assert.False(ScriptParser.Parse(
            "sync:\n  url: \"https://x.test/\"\n  id: id\n  lat: lat\n  lon: lng\n").IsValid);
        Assert.False(ScriptParser.Parse(
            "sync:\n  url: \"https://x.test/\"\n  id: id\n  waypoint:\n    name: x\n").IsValid);
    }

    // ----- reconciliation -----------------------------------------------------

    [Fact]
    public void A_New_Record_Is_Placed_Once_And_Not_Again()
    {
        var engine = Armed();

        var first = engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon);
        var action = Assert.Single(first);
        Assert.Equal(FeedSyncActionKind.Place, action.Kind);
        Assert.Equal("Fire: Bear Fire", action.Name);
        Assert.Equal("1200 acres, 35% contained", action.Description);
        // Never expires: retired when the fire goes, not on a clock.
        Assert.Equal(2147483647u, action.ExpireEpoch);

        // Same feed again: nothing to say.
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(15)));
    }

    [Fact]
    public void A_Watched_Field_Changing_Resends_The_Same_Marker()
    {
        var engine = Armed();
        var placed = engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire", acreage: 1200)), Home, Noon)[0];

        var updated = Assert.Single(
            engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire", acreage: 3000)), Home, Noon.AddMinutes(15)));

        Assert.Equal(FeedSyncActionKind.Update, updated.Kind);
        Assert.Equal("3000 acres, 35% contained", updated.Description);
        // The same marker, so it replaces rather than accumulating.
        Assert.Equal(placed.WaypointId, updated.WaypointId);
    }

    [Fact]
    public void An_Unwatched_Field_Changing_Says_Nothing()
    {
        var engine = Armed();
        engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon);

        // date_modified is not in watch:, which is the whole point — a feed
        // that restamps every record must not rebroadcast the lot.
        var changed = Fire(1, "Bear Fire").Replace("\"whenever\"", "\"later\"");
        Assert.Empty(engine.Reconcile(FileName, Feed(changed), Home, Noon.AddMinutes(15)));
    }

    [Fact]
    public void A_Record_Going_Inactive_Retires_Its_Marker()
    {
        var engine = Armed();
        var placed = engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon)[0];

        var removed = Assert.Single(
            engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire", active: false)), Home, Noon.AddMinutes(15)));

        Assert.Equal(FeedSyncActionKind.Remove, removed.Kind);
        Assert.Equal(placed.WaypointId, removed.WaypointId);
        // Retired by an expiry in the past: there is no delete on the wire.
        Assert.True(removed.ExpireEpoch < Noon.AddMinutes(15).ToUnixTimeSeconds());
    }

    [Fact]
    public void A_Record_Vanishing_Entirely_Also_Retires_Its_Marker()
    {
        // The case no trigger could ever represent.
        var engine = Armed();
        engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon);

        var removed = Assert.Single(engine.Reconcile(FileName, "[]", Home, Noon.AddMinutes(15)));
        Assert.Equal(FeedSyncActionKind.Remove, removed.Kind);

        // And only once — it is forgotten after retirement.
        Assert.Empty(engine.Reconcile(FileName, "[]", Home, Noon.AddMinutes(30)));
    }

    [Fact]
    public void Records_Outside_The_Range_Are_Ignored()
    {
        var engine = Armed();

        // Los Angeles, some 600 km away.
        var actions = engine.Reconcile(
            FileName, Feed(Fire(1, "Bear Fire"), Fire(2, "Palisades", lat: 34.05, lng: -118.24)), Home, Noon);

        var placed = Assert.Single(actions);
        Assert.Equal("Fire: Bear Fire", placed.Name);
    }

    [Fact]
    public void Several_Records_Are_Each_Given_Their_Own_Marker()
    {
        // The thing a script could not do: one poll, many markers.
        var engine = Armed();

        var actions = engine.Reconcile(
            FileName,
            Feed(Fire(1, "Bear Fire"), Fire(2, "Cedar Fire", lat: 39.25), Fire(3, "Pine Fire", lat: 39.15)),
            Home, Noon);

        Assert.Equal(3, actions.Count);
        Assert.All(actions, a => Assert.Equal(FeedSyncActionKind.Place, a.Kind));
        Assert.Equal(3, actions.Select(a => a.WaypointId).Distinct().Count());
    }

    [Fact]
    public void A_Waypoint_Id_Is_Stable_For_The_Same_Record()
    {
        // What lets in-memory state be safe across a restart: the same record
        // always maps to the same marker, so a re-send replaces it.
        Assert.Equal(FeedSyncEngine.WaypointIdFor("1234"), FeedSyncEngine.WaypointIdFor("1234"));
        Assert.NotEqual(FeedSyncEngine.WaypointIdFor("1234"), FeedSyncEngine.WaypointIdFor("1235"));
        Assert.NotEqual(0u, FeedSyncEngine.WaypointIdFor(""));
    }

    [Fact]
    public void Reloading_Keeps_What_Is_Already_On_The_Map()
    {
        var engine = Armed();
        engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon);

        var parse = ScriptParser.Parse(Yaml);
        engine.Load([new ScriptFile(FileName, FileName, Yaml, Enabled: true, parse)], Noon.AddMinutes(1));

        // Editing a feed must not re-place every marker it had already sent.
        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(2)));
    }

    [Fact]
    public void Reloading_Does_Not_Restart_A_Running_Feed_Clock()
    {
        var engine = Armed();
        Assert.Single(engine.Due(Noon));                    // the first read
        Assert.Empty(engine.Due(Noon.AddMinutes(5)));       // next due at +15

        // A reload — someone enabled an unrelated script, or the editor saved.
        var parse = ScriptParser.Parse(Yaml);
        engine.Load([new ScriptFile(FileName, FileName, Yaml, Enabled: true, parse)], Noon.AddMinutes(5));

        // Still on its own schedule rather than reading again on the spot.
        Assert.Empty(engine.Due(Noon.AddMinutes(6)));
        Assert.Single(engine.Due(Noon.AddMinutes(16)));
    }

    [Fact]
    public void Changing_The_Interval_Does_Restart_The_Clock()
    {
        var engine = Armed();
        Assert.Single(engine.Due(Noon));

        // every: 15m -> 1m. The old schedule is now the wrong one, so the feed
        // takes the new interval from the reload rather than an hour later.
        var faster = Yaml.Replace("every: 15m", "every: 1m");
        var parse = ScriptParser.Parse(faster);
        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        engine.Load([new ScriptFile(FileName, FileName, faster, Enabled: true, parse)], Noon.AddMinutes(5));

        Assert.Single(engine.Due(Noon.AddMinutes(5)));
    }

    [Fact]
    public void Due_Respects_The_Interval_But_Fires_Immediately_On_Load()
    {
        var engine = Armed();

        // A mirror has nothing to say until it has read the feed once.
        Assert.Single(engine.Due(Noon));
        Assert.Empty(engine.Due(Noon.AddMinutes(5)));
        Assert.Single(engine.Due(Noon.AddMinutes(16)));
    }

    [Fact]
    public void A_Response_That_Is_Not_A_List_Is_Reported_Rather_Than_Throwing()
    {
        var engine = Armed();
        string? diagnostic = null;
        engine.Diagnostic += d => diagnostic = d;

        Assert.Empty(engine.Reconcile(FileName, """{"error":"nope"}""", Home, Noon));
        Assert.Contains("is not a list", diagnostic);

        Assert.Empty(engine.Reconcile(FileName, "<html>", Home, Noon));
        Assert.Contains("not valid JSON", diagnostic);
    }

    [Fact]
    public void Without_A_Home_Location_A_Ranged_Sync_Places_Nothing()
    {
        // Fails closed rather than mirroring the whole state.
        var engine = Armed();
        var nowhere = new ScriptSelf(1, "ME", "My Node", 101);

        Assert.Empty(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), nowhere, Noon));
    }

    [Fact]
    public void A_Restart_Does_Not_Re_Place_Markers_That_Are_Already_Out_There()
    {
        // The whole point of persisting: a fire season is dozens of markers,
        // and without this every start re-broadcasts the lot.
        using var temp = new TempFile();
        var feed = Feed(Fire(1, "Bear Fire"), Fire(2, "Ridge Fire"));

        var first = Armed(store: new FeedSyncStore(temp.Path));
        Assert.Equal(2, first.Reconcile(FileName, feed, Home, Noon).Count);

        // A new engine, as a new process would build it, reading the file the
        // first one wrote.
        var second = Armed(store: new FeedSyncStore(temp.Path), at: Noon.AddMinutes(20));
        Assert.Empty(second.Reconcile(FileName, feed, Home, Noon.AddMinutes(20)));
    }

    [Fact]
    public void A_Restart_Still_Notices_What_Changed_While_It_Was_Closed()
    {
        // Remembering must not mean going deaf: the saved fingerprint is what
        // an update is measured against.
        using var temp = new TempFile();

        var first = Armed(store: new FeedSyncStore(temp.Path));
        first.Reconcile(FileName, Feed(Fire(1, "Bear Fire", acreage: 1200)), Home, Noon);

        var second = Armed(store: new FeedSyncStore(temp.Path), at: Noon.AddMinutes(20));
        var action = Assert.Single(
            second.Reconcile(FileName, Feed(Fire(1, "Bear Fire", acreage: 5000)), Home, Noon.AddMinutes(20)));

        Assert.Equal(FeedSyncActionKind.Update, action.Kind);
        Assert.Contains("5000 acres", action.Description);
    }

    [Fact]
    public void A_Feed_Turned_Off_And_On_Again_Keeps_Its_Markers()
    {
        // Its section is left alone while it is not loaded, rather than being
        // pruned as unknown — otherwise disabling a sync for a minute costs a
        // full re-place.
        using var temp = new TempFile();

        var first = Armed(store: new FeedSyncStore(temp.Path));
        Assert.Single(first.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));

        // A run in which this feed is not loaded at all, and another feed is.
        var other = new FeedSyncEngine(new FeedSyncStore(temp.Path));
        other.Load([], Noon.AddMinutes(10));

        var back = Armed(store: new FeedSyncStore(temp.Path), at: Noon.AddMinutes(20));
        Assert.Empty(back.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon.AddMinutes(20)));
    }

    // ----- require -------------------------------------------------------------

    /// <summary>The sample with a test that keeps prescribed burns off the
    /// map, which is the case it was added for.</summary>
    private static string Filtered => Yaml.Replace(
        "  watch:\n",
        "  require:\n"
        + "    - value: \"{item.data.is_prescribed}\"\n"
        + "      not_equals: true\n"
        + "  watch:\n");

    [Fact]
    public void A_Lone_Require_Needs_No_Dash()
    {
        var loose = Yaml.Replace(
            "  watch:\n",
            "  require:\n"
            + "      value: \"{item.data.is_prescribed}\"\n"
            + "      not_equals: true\n"
            + "  watch:\n");

        var parse = ScriptParser.Parse(loose);
        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        var test = Assert.Single(parse.Sync!.Require);
        Assert.Equal("{item.data.is_prescribed}", test.Value);
    }

    [Fact]
    public void Every_Require_Has_To_Hold()
    {
        var both = Yaml.Replace(
            "  watch:\n",
            "  require:\n"
            + "    - value: \"{item.data.is_prescribed}\"\n"
            + "      not_equals: true\n"
            + "    - value: \"{item.data.acreage}\"\n"
            + "      at_least: 100\n"
            + "  watch:\n");

        var engine = Armed(both);
        Assert.Equal(2, ScriptParser.Parse(both).Sync!.Require.Count);

        // Small and real, big and prescribed: each fails one test.
        Assert.Empty(engine.Reconcile(FileName, Feed(
            Fire(1, "Spot Fire", acreage: 4),
            Fire(2, "Unit 7 Burn", acreage: 900, prescribed: true)), Home, Noon));

        var placed = Assert.Single(
            engine.Reconcile(FileName, Feed(Fire(3, "Bear Fire", acreage: 900)), Home, Noon));
        Assert.Equal(FeedSyncActionKind.Place, placed.Kind);
    }

    [Fact]
    public void A_Record_That_Fails_Require_Is_Never_Placed()
    {
        var engine = Armed(Filtered);

        var action = Assert.Single(engine.Reconcile(FileName, Feed(
            Fire(1, "Bear Fire"),
            Fire(2, "Unit 7 Burn", prescribed: true)), Home, Noon));

        Assert.Equal(FeedSyncActionKind.Place, action.Kind);
        Assert.Contains("Bear Fire", action.Name);
    }

    [Fact]
    public void A_Record_That_Stops_Qualifying_Is_Retired()
    {
        // Reclassification is the reason failing counts as gone rather than as
        // unseen: the marker is already out there and has to be taken back.
        var engine = Armed(Filtered);
        var placed = Assert.Single(engine.Reconcile(FileName, Feed(Fire(1, "Bear Fire")), Home, Noon));

        var removed = Assert.Single(engine.Reconcile(
            FileName, Feed(Fire(1, "Bear Fire", prescribed: true)), Home, Noon.AddMinutes(15)));

        Assert.Equal(FeedSyncActionKind.Remove, removed.Kind);
        Assert.Equal(placed.WaypointId, removed.WaypointId);
    }

    [Fact]
    public void A_Record_That_Starts_Qualifying_Is_Placed()
    {
        var engine = Armed(Filtered);
        Assert.Empty(engine.Reconcile(
            FileName, Feed(Fire(1, "Unit 7 Burn", prescribed: true)), Home, Noon));

        var placed = Assert.Single(engine.Reconcile(
            FileName, Feed(Fire(1, "Unit 7 Burn")), Home, Noon.AddMinutes(15)));
        Assert.Equal(FeedSyncActionKind.Place, placed.Kind);
    }

    [Fact]
    public void A_Missing_Field_Does_Not_Exclude_A_Record()
    {
        // A feed that has not filled the field in yet reads as empty, which is
        // not "true" — the record stays. Silence is not a reason to drop a fire.
        var engine = Armed(Filtered);
        var bare = "{\"id\":9,\"is_active\":true,\"name\":\"Bare Fire\""
                 + ",\"lat\":39.31,\"lng\":-120.84,\"data\":{}}";

        Assert.Single(engine.Reconcile(FileName, Feed(bare), Home, Noon));
    }

    [Fact]
    public void A_Require_That_Tests_Something_Moving_Is_Warned_About()
    {
        var moving = Yaml.Replace(
            "  watch:\n",
            "  require:\n"
            + "    - value: \"{time}\"\n"
            + "      not_equals: \"never\"\n"
            + "  watch:\n");

        var parse = ScriptParser.Parse(moving);
        Assert.True(parse.IsValid);
        Assert.Contains(parse.Problems, p => p.Message.Contains("{time} changes on its own"));
    }

    [Fact]
    public void A_Require_With_No_Comparison_Is_An_Error()
    {
        var empty = Yaml.Replace("  watch:\n", "  require: []\n  watch:\n");

        var parse = ScriptParser.Parse(empty);
        Assert.False(parse.IsValid);
        Assert.Contains("require", parse.FirstError!.Value.Message);
    }

    /// <summary>A scratch path that cleans itself up.</summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"MeshRF.FeedSync.{Guid.NewGuid():n}.json");

        public void Dispose()
        {
            try { File.Delete(Path); } catch (IOException) { }
        }
    }
}
