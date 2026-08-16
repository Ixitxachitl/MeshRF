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

    private const string Yaml =
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

    private static FeedSyncEngine Armed(string yaml = Yaml)
    {
        var parse = ScriptParser.Parse(yaml);
        Assert.True(parse.IsValid, parse.FirstError?.ToString());
        Assert.True(parse.IsSync);

        var engine = new FeedSyncEngine();
        engine.Load([new ScriptFile(FileName, FileName, yaml, Enabled: true, parse)], Noon);
        return engine;
    }

    private static string Feed(params string[] fires) => "[" + string.Join(",", fires) + "]";

    /// <summary>A record shaped like Watch Duty's: lat/lng top level, acreage
    /// and containment nested, plus a date_modified nothing watches.</summary>
    private static string Fire(int id, string name, bool active = true,
                               double lat = 39.31, double lng = -120.84,
                               int acreage = 1200, int containment = 35)
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
             + ",\"data\":{\"acreage\":" + acreage + ",\"containment\":" + containment + "}}";
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
}
