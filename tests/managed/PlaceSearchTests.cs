// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Reading Nominatim's answers. The request itself needs the network, so what
/// is tested here is the parsing, which is where a locale or a missing field
/// would quietly put the map in the wrong hemisphere.
/// </summary>
public class PlaceSearchTests
{
    private const string TwoResults = """
        [
          {"place_id":1,"lat":"39.2361","lon":"-120.8330","display_name":"Alta, Placer County, California"},
          {"place_id":2,"lat":"-33.8688","lon":"151.2093","display_name":"Sydney, New South Wales, Australia"}
        ]
        """;

    [Fact]
    public void ResultsComeBackNamedAndPlaced()
    {
        var places = PlaceSearch.Parse(TwoResults);

        Assert.Equal(2, places.Count);
        Assert.StartsWith("Alta", places[0].Name);
        Assert.Equal(39.2361, places[0].At.Lat, 4);
        Assert.Equal(-120.8330, places[0].At.Lon, 4);
    }

    [Fact]
    public void TheSouthernHemisphereSurvivesTheTrip()
    {
        var places = PlaceSearch.Parse(TwoResults);

        Assert.Equal(-33.8688, places[1].At.Lat, 4);
        Assert.Equal(151.2093, places[1].At.Lon, 4);
    }

    [Fact]
    public void CoordinatesAreReadInvariantlyWhateverTheMachineIsSetTo()
    {
        // Nominatim sends "39.2361" as a string. Parsing that under a locale
        // where the comma is the decimal point reads it as 392361, which puts
        // the map somewhere off the planet.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(39.2361, PlaceSearch.Parse(TwoResults)[0].At.Lat, 4);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void NothingFoundIsAnEmptyListRatherThanAnError()
    {
        Assert.Empty(PlaceSearch.Parse("[]"));
    }

    [Fact]
    public void AResultMissingWhatItNeedsIsSkippedRatherThanFailingTheBatch()
    {
        const string mixed = """
            [
              {"place_id":1,"lat":"39.2361","display_name":"No longitude"},
              {"place_id":2,"lon":"-120.8","display_name":"No latitude"},
              {"place_id":3,"lat":"x","lon":"y","display_name":"Unparseable"},
              {"place_id":4,"lat":"1.5","lon":"2.5"},
              {"place_id":5,"lat":"51.5","lon":"-0.12","display_name":"London"}
            ]
            """;

        var places = PlaceSearch.Parse(mixed);

        Assert.Single(places);
        Assert.Equal("London", places[0].Name);
    }

    [Fact]
    public void AnAnswerThatIsNotAListIsNotAResult()
    {
        // Nominatim answers an error as an object, not an array.
        Assert.Empty(PlaceSearch.Parse("""{"error":"Unable to geocode"}"""));
    }

    [Fact]
    public async Task AnEmptyQueryIsNotWorthAskingAbout()
    {
        using var search = new PlaceSearch();

        Assert.Empty(await search.FindAsync(""));
        Assert.Empty(await search.FindAsync("   "));
    }
}
