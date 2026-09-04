// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Reading OpenStreetMap's building extracts. The fetch needs the network, so
/// what is pinned here is the parsing — where a dropped ring or a swallowed
/// courtyard would quietly change every prediction that crosses it.
/// </summary>
public class OverpassBuildingsTests
{
    private const string OneWay = """
        {"elements":[
          {"type":"way","id":1,"geometry":[
            {"lat":44.9770,"lon":-93.2660},
            {"lat":44.9770,"lon":-93.2650},
            {"lat":44.9780,"lon":-93.2650},
            {"lat":44.9780,"lon":-93.2660},
            {"lat":44.9770,"lon":-93.2660}]}
        ]}
        """;

    [Fact]
    public void AWayBecomesAFootprint()
    {
        var footprints = OverpassBuildings.Parse(OneWay);

        Assert.Single(footprints);
        Assert.Equal(4, footprints[0].Outline.Count);
    }

    [Fact]
    public void TheRepeatedClosingNodeIsDropped()
    {
        // Overpass closes a way by repeating its first node. Left in, the
        // crossing test counts one edge twice, which flips the answer for
        // points near it.
        var outline = OverpassBuildings.Parse(OneWay)[0].Outline;

        Assert.NotEqual(outline[0], outline[^1]);
    }

    [Fact]
    public void TheFootprintKnowsItsOwnBounds()
    {
        var footprint = OverpassBuildings.Parse(OneWay)[0];

        Assert.Equal(44.9770, footprint.MinLat, 4);
        Assert.Equal(44.9780, footprint.MaxLat, 4);
        Assert.Equal(-93.2660, footprint.MinLon, 4);
        Assert.Equal(-93.2650, footprint.MaxLon, 4);
    }

    [Fact]
    public void ARelationContributesItsOuterRingsOnly()
    {
        // The inner rings are courtyards. Treated as buildings they would
        // charge a signal for crossing a hole in one.
        const string relation = """
            {"elements":[
              {"type":"relation","id":9,"members":[
                {"type":"way","role":"outer","geometry":[
                  {"lat":1.0,"lon":1.0},{"lat":1.0,"lon":1.1},
                  {"lat":1.1,"lon":1.1},{"lat":1.1,"lon":1.0}]},
                {"type":"way","role":"inner","geometry":[
                  {"lat":1.04,"lon":1.04},{"lat":1.04,"lon":1.06},
                  {"lat":1.06,"lon":1.06},{"lat":1.06,"lon":1.04}]}
              ]}
            ]}
            """;

        var footprints = OverpassBuildings.Parse(relation);

        Assert.Single(footprints);
        Assert.Equal(1.0, footprints[0].MinLat, 4);
    }

    [Fact]
    public void SomethingWithoutEnoughPointsToBeARingIsSkipped()
    {
        const string degenerate = """
            {"elements":[
              {"type":"way","id":1,"geometry":[{"lat":1.0,"lon":1.0},{"lat":1.0,"lon":1.1}]},
              {"type":"way","id":2,"geometry":[]},
              {"type":"way","id":3},
              {"type":"way","id":4,"geometry":[
                {"lat":2.0,"lon":2.0},{"lat":2.0,"lon":2.1},{"lat":2.1,"lon":2.1}]}
            ]}
            """;

        var footprints = OverpassBuildings.Parse(degenerate);

        Assert.Single(footprints);
        Assert.Equal(2.0, footprints[0].MinLat, 4);
    }

    [Fact]
    public void AnEmptyOrUnexpectedAnswerYieldsNothingRatherThanThrowing()
    {
        Assert.Empty(OverpassBuildings.Parse("""{"elements":[]}"""));
        Assert.Empty(OverpassBuildings.Parse("""{"remark":"timed out"}"""));
        Assert.Empty(OverpassBuildings.Parse("""{"elements":{}}"""));
    }

    [Fact]
    public void ManyBuildingsIndexTogether()
    {
        var many = string.Join(',', Enumerable.Range(0, 50).Select(i =>
            $$"""
            {"type":"way","id":{{i}},"geometry":[
              {"lat":{{44.97 + i * 0.001}},"lon":-93.26},
              {"lat":{{44.97 + i * 0.001}},"lon":-93.259},
              {"lat":{{44.9705 + i * 0.001}},"lon":-93.259},
              {"lat":{{44.9705 + i * 0.001}},"lon":-93.26}]}
            """));

        var index = new BuildingIndex(OverpassBuildings.Parse($$"""{"elements":[{{many}}]}"""));

        Assert.Equal(50, index.Count);
    }

    [Fact]
    public async Task AskingAboutNoAreaFetchesNothing()
    {
        using var buildings = new OverpassBuildings();

        var extract = await buildings.AroundAsync(new GeoPoint(0, 0), 0);

        Assert.Equal(0, extract.Count);
        Assert.False(extract.LookupFailed);
    }

    [Fact]
    public void NoneMappedAndCouldNotAskAreDifferentAnswers()
    {
        // They look identical on a map — no buildings drawn, nothing charged
        // for — and identical to the toggle having done nothing at all. The
        // caller can only say which if the lookup says which.
        Assert.Equal(0, BuildingExtract.None.Count);
        Assert.False(BuildingExtract.None.LookupFailed);

        Assert.Equal(0, BuildingExtract.Unavailable.Count);
        Assert.True(BuildingExtract.Unavailable.LookupFailed);
    }
}
