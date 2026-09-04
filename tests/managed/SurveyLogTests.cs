// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Map;
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The record of what this station heard and where it was standing, which is
/// what turns a client with a GPS into a survey instrument.
/// </summary>
public sealed class SurveyLogTests : IDisposable
{
    private readonly string _dir;
    private readonly SurveyLog _log;

    public SurveyLogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MeshRF.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _log = new SurveyLog(Path.Combine(_dir, "survey.csv"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private static readonly GeoPoint Home = new(44.9778, -93.2650);

    private bool Record(
        uint node = 1, byte? hops = 0, bool viaMqtt = false, float? snr = -8,
        GeoPoint? mine = null, GeoPoint? peer = null) =>
        _log.Record(node, hops, viaMqtt, snr,
                    mine ?? Home,
                    peer ?? CoverageMap.Along(Home, 90, 2000),
                    DateTime.UtcNow);

    [Fact]
    public void AReadingThisStationTookIsKept()
    {
        Assert.True(Record());

        var samples = _log.Read();
        Assert.Single(samples);
        Assert.Equal(1u, samples[0].NodeNum);
        Assert.Equal(-8, samples[0].SnrDb, 2);
        Assert.Equal(2000, samples[0].DistanceM, 0);
    }

    [Fact]
    public void ARelayedOrMqttReadingIsNotASurveyPoint()
    {
        // Its SNR was measured somewhere else, and writing it down under this
        // station's position would be recording a measurement that never
        // happened here.
        Assert.False(Record(hops: 2));
        Assert.False(Record(hops: null));
        Assert.False(Record(viaMqtt: true));
        Assert.Empty(_log.Read());
    }

    [Fact]
    public void AReadingMissingAPositionOrASignalIsNotUsable()
    {
        Assert.False(Record(snr: null));
        Assert.False(_log.Record(1, 0, false, -8, null, Home, DateTime.UtcNow));
        Assert.False(_log.Record(1, 0, false, -8, Home, null, DateTime.UtcNow));
    }

    [Fact]
    public void APeerAtTheSameSpotHasNoPathToRecord()
    {
        Assert.False(Record(peer: Home));
    }

    [Fact]
    public void ReadingsAccumulateAcrossWrites()
    {
        for (int i = 0; i < 5; i++) Record(node: (uint)i);
        Assert.Equal(5, _log.Read().Count);
    }

    [Fact]
    public void TheFileSurvivesBeingReadWhileItIsWritten()
    {
        Record();
        var first = _log.Read();
        Record();

        Assert.Single(first);
        Assert.Equal(2, _log.Read().Count);
    }

    [Fact]
    public void AHalfWrittenLineIsSkippedRatherThanFailingTheWholeRead()
    {
        Record();
        File.AppendAllText(_log.FilePath, "2026-09-04T00:00:00.000");

        Assert.Single(_log.Read());
    }

    [Fact]
    public void ClearingLeavesNothingBehind()
    {
        Record();
        _log.Clear();

        Assert.False(_log.Exists);
        Assert.Empty(_log.Read());
    }

    [Fact]
    public void ReadingALogThatWasNeverWrittenIsEmptyRatherThanAnError()
    {
        Assert.Empty(new SurveyLog(Path.Combine(_dir, "never.csv")).Read());
    }

    // -- Carrying a survey between machines ----------------------------------

    private SurveyLog Elsewhere(string name) => new(Path.Combine(_dir, name));

    [Fact]
    public void AnExportedLogReadsBackTheSame()
    {
        for (int i = 0; i < 3; i++) Record(node: (uint)i);

        var carried = Path.Combine(_dir, "carried.csv");
        Assert.True(_log.Export(carried));
        Assert.Equal(3, new SurveyLog(carried).Read().Count);
    }

    [Fact]
    public void ExportingALogThatWasNeverWrittenSaysSo()
    {
        Assert.False(_log.Export(Path.Combine(_dir, "nothing.csv")));
    }

    [Fact]
    public void ImportingMergesRatherThanReplaces()
    {
        // The case this exists for: a survey driven on a laptop coming back to
        // the station, which has readings of its own already.
        var laptop = Elsewhere("laptop.csv");
        for (int i = 0; i < 4; i++)
            laptop.Record(100u + (uint)i, 0, false, -7, Home, CoverageMap.Along(Home, 45, 3000), DateTime.UtcNow.AddMinutes(i));

        Record(node: 1);
        var result = _log.Import(laptop.FilePath);

        Assert.Equal(4, result.Added);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(5, _log.Read().Count);
    }

    [Fact]
    public void ImportingTheSameDriveTwiceAddsNothingTheSecondTime()
    {
        // Otherwise every reading in it counts double, and a bin's average is
        // quietly weighted toward whichever file was imported most often.
        var laptop = Elsewhere("laptop.csv");
        for (int i = 0; i < 3; i++)
            laptop.Record((uint)i, 0, false, -7, Home, CoverageMap.Along(Home, 45, 3000), DateTime.UtcNow.AddMinutes(i));

        Assert.Equal(3, _log.Import(laptop.FilePath).Added);

        var again = _log.Import(laptop.FilePath);
        Assert.Equal(0, again.Added);
        Assert.Equal(3, again.Duplicates);
        Assert.Equal(3, _log.Read().Count);
    }

    [Fact]
    public void ImportingIntoAnEmptyLogJustWorks()
    {
        var laptop = Elsewhere("laptop.csv");
        laptop.Record(1, 0, false, -7, Home, CoverageMap.Along(Home, 45, 3000), DateTime.UtcNow);

        Assert.Equal(1, _log.Import(laptop.FilePath).Added);
        Assert.Single(_log.Read());
    }

    [Fact]
    public void ImportingSomethingThatIsNotASurveyAddsNothing()
    {
        var junk = Path.Combine(_dir, "junk.csv");
        File.WriteAllLines(junk, ["name,value", "alpha,1", "beta,2"]);

        var result = _log.Import(junk);

        Assert.Equal(0, result.Added);
        Assert.True(result.Unreadable > 0, "unreadable lines should be counted, not silently dropped");
        Assert.Empty(_log.Read());
    }

    [Fact]
    public void ImportingAFileThatIsNotThereIsHarmless()
    {
        var result = _log.Import(Path.Combine(_dir, "missing.csv"));
        Assert.Equal(0, result.Added);
    }

    [Fact]
    public void ImportedReadingsFitAlongsideTheirOwn()
    {
        // What the whole exercise is for: readings from a drive joining the
        // station's own so one peer spans several ranges instead of one.
        var laptop = Elsewhere("laptop.csv");
        foreach (double range in new[] { 4000.0, 9000, 15000 })
            for (int i = 0; i < 4; i++)
                laptop.Record(7, 0, false, -9, Home, CoverageMap.Along(Home, 90, range),
                              DateTime.UtcNow.AddSeconds(range + i));

        for (int i = 0; i < 4; i++) Record(node: 7);
        _log.Import(laptop.FilePath);

        var bins = SurveyLog.Bin(_log.Read());
        Assert.True(bins.Count >= 4, $"expected several ranges for one peer, got {bins.Count}");
    }

    // -- Binning ------------------------------------------------------------

    private static SurveySample At(uint node, double distanceM, double snr) =>
        new(DateTime.UtcNow, node, Home.Lat, Home.Lon, 0, 0, snr, distanceM);

    [Fact]
    public void ReadingsFromOnePeerAtOneRangeAverageTogether()
    {
        // The whole reason for binning: one packet's SNR carries several
        // decibels of fading, and twenty average most of it away.
        // Kept inside one bin on purpose. A decade split ten ways makes each
        // about 1.26 times as wide as the last, so readings a few metres apart
        // can still land either side of a boundary.
        var samples = new[] { At(1, 1000, -6), At(1, 1020, -10), At(1, 1050, -8) };

        var bins = SurveyLog.Bin(samples);

        Assert.Single(bins);
        Assert.Equal(-8, bins[0].MeanSnrDb, 2);
        Assert.Equal(3, bins[0].Count);
        Assert.InRange(bins[0].DistanceM, 1000, 1050);
    }

    [Fact]
    public void ReadingsAtDifferentRangesStaySeparate()
    {
        // And this is what a fit needs: the same peer measured at a spread of
        // ranges rather than at one.
        var samples = new List<SurveySample>();
        foreach (double d in new[] { 300.0, 1200, 5000, 18000 })
            for (int i = 0; i < 4; i++) samples.Add(At(1, d, -5));

        var bins = SurveyLog.Bin(samples);

        Assert.Equal(4, bins.Count);
        Assert.Equal(bins.OrderBy(b => b.DistanceM).Select(b => b.DistanceM), bins.Select(b => b.DistanceM));
    }

    [Fact]
    public void DifferentPeersAtTheSameRangeStaySeparate()
    {
        var samples = new List<SurveySample>();
        for (int i = 0; i < 4; i++) { samples.Add(At(1, 1000, -5)); samples.Add(At(2, 1000, -12)); }

        var bins = SurveyLog.Bin(samples);

        Assert.Equal(2, bins.Count);
        Assert.Equal([-12, -5], bins.OrderBy(b => b.MeanSnrDb).Select(b => Math.Round(b.MeanSnrDb)));
    }

    [Fact]
    public void ABinWithTooFewReadingsIsNotWorthBelieving()
    {
        var samples = new[] { At(1, 1000, -5), At(1, 1000, -6) };

        Assert.Empty(SurveyLog.Bin(samples));
        Assert.Single(SurveyLog.Bin(samples, minCount: 2));
    }

    [Fact]
    public void BinsAreLogarithmicBecauseTheFitIs()
    {
        // A hundred metres matters near the station and is nothing at ten
        // kilometres, so equal-width bins would waste resolution where it
        // counts and invent it where it does not.
        var near = new[] { At(1, 300, -5), At(1, 380, -5), At(1, 480, -5) };
        var far = new[] { At(1, 10_000, -5), At(1, 10_080, -5), At(1, 10_160, -5) };

        // One reading per bin here: this is about where the boundaries fall,
        // not about how many readings a bin deserves.
        Assert.True(SurveyLog.Bin(near, minCount: 1).Count > 1,
            "steps of eighty metres near the station should separate");
        Assert.Single(SurveyLog.Bin(far, minCount: 1));
    }

    [Fact]
    public void AZeroWidthBinIsRefusedRatherThanDividedBy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SurveyLog.Bin([], binsPerDecade: 0));
    }
}
