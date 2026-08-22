// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Scripting;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// How many times an hour a 5m mirror actually leaves for the network, driven
/// at the app's real 250 ms poll cadence.
/// </summary>
public class FeedSyncPollRateTests
{
    private static ScriptFile Lightning(string every) => new(
        FileName: "lightning-sync.yaml",
        FullPath: "lightning-sync.yaml",
        Text: $"""
        enabled: true
        alias: Lightning strikes nearby
        sync:
          every: {every}
          url: "https://data.api.xweather.com/lightning/closest?p=39.1,-121.0&limit=10"
          items: response
          id: id
          lat: loc.lat
          lon: loc.long
          watch: []
          waypoint:
            name: "Lightning"
        """,
        Enabled: true,
        Parse: ScriptParser.Parse($"""
        enabled: true
        alias: Lightning strikes nearby
        sync:
          every: {every}
          url: "https://data.api.xweather.com/lightning/closest?p=39.1,-121.0&limit=10"
          items: response
          id: id
          lat: loc.lat
          lon: loc.long
          watch: []
          waypoint:
            name: "Lightning"
        """));

    [Fact]
    public void FiresTwelveTimesAnHourAtTheAppsPollCadence()
    {
        var engine = new FeedSyncEngine();
        var start = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        engine.Load([Lightning("5m")], start);

        int fired = 0;
        // One hour of Poll(), which runs every 250 ms.
        for (int tick = 0; tick < 4 * 60 * 60; tick++)
            fired += engine.Due(start + TimeSpan.FromMilliseconds(250 * tick)).Count;

        Assert.Equal(12, fired);
    }

    [Fact]
    public void ReloadingEveryTickDoesNotBringThePollForward()
    {
        var engine = new FeedSyncEngine();
        var start = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);
        var file = Lightning("5m");
        engine.Load([file], start);

        int fired = 0;
        for (int tick = 0; tick < 4 * 60 * 60; tick++)
        {
            var now = start + TimeSpan.FromMilliseconds(250 * tick);
            // Worst case: something re-reads the folder on every single poll.
            engine.Load([file], now);
            fired += engine.Due(now).Count;
        }

        Assert.Equal(12, fired);
    }
}
