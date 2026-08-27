// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using MeshRF.Map;
using Xunit;

namespace MeshRF.Tests;

public sealed class TileDiskCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "meshrf-tilecache-" + Guid.NewGuid().ToString("N"));

    public TileDiskCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Writes a file of a given size, aged by the given span so the
    /// eviction order is deterministic rather than dependent on write speed.</summary>
    private string Write(string name, int bytes, TimeSpan age)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private long TotalBytes() =>
        new DirectoryInfo(_dir).GetFiles().Sum(f => f.Length);

    private string[] Remaining() =>
        new DirectoryInfo(_dir).GetFiles().Select(f => f.Name).OrderBy(n => n).ToArray();

    // -- Not over the ceiling -----------------------------------------------

    [Fact]
    public void ACacheUnderItsCeilingIsLeftAlone()
    {
        Write("a.png", 1000, TimeSpan.FromDays(9));
        Write("b.png", 1000, TimeSpan.FromDays(1));

        Assert.Equal(2000, TileDiskCache.Trim(_dir, maxBytes: 5000, targetBytes: 4000));
        Assert.Equal(["a.png", "b.png"], Remaining());
    }

    [Fact]
    public void ExactlyAtTheCeilingIsNotOverIt()
    {
        Write("a.png", 2000, TimeSpan.FromDays(1));
        TileDiskCache.Trim(_dir, maxBytes: 2000, targetBytes: 1000);
        Assert.Single(Remaining());
    }

    // -- Over the ceiling ---------------------------------------------------

    [Fact]
    public void TrimmingDropsOldestFirstUntilUnderTheTarget()
    {
        Write("oldest.png", 1000, TimeSpan.FromDays(10));
        Write("middle.png", 1000, TimeSpan.FromDays(5));
        Write("newest.png", 1000, TimeSpan.FromDays(1));

        // Over 2500, trim to 2000: dropping the oldest alone is enough.
        long left = TileDiskCache.Trim(_dir, maxBytes: 2500, targetBytes: 2000);

        Assert.Equal(2000, left);
        Assert.Equal(["middle.png", "newest.png"], Remaining());
    }

    [Fact]
    public void TrimmingGoesToTheTargetNotJustUnderTheCeiling()
    {
        for (int i = 0; i < 10; i++)
            Write($"t{i:D2}.png", 1000, TimeSpan.FromDays(20 - i));

        long left = TileDiskCache.Trim(_dir, maxBytes: 9000, targetBytes: 5000);

        // The gap is the point: it must not stop the moment it is under 9000.
        Assert.True(left <= 5000, $"expected <= 5000, got {left}");
        Assert.Equal(left, TotalBytes());
        // The five newest survive.
        Assert.Equal(["t05.png", "t06.png", "t07.png", "t08.png", "t09.png"], Remaining());
    }

    [Fact]
    public void ABigFileCountsForMoreThanManySmallOnes()
    {
        // One vector source tile against a pile of rasterised tiles: the cache
        // is bounded by bytes, so the big one is worth many of the small.
        Write("source.pbf", 500_000, TimeSpan.FromDays(10));
        for (int i = 0; i < 20; i++)
            Write($"tile{i:D2}.png", 10_000, TimeSpan.FromDays(1));

        long left = TileDiskCache.Trim(_dir, maxBytes: 300_000, targetBytes: 200_000);

        Assert.True(left <= 200_000);
        Assert.DoesNotContain("source.pbf", Remaining());
        Assert.Equal(20, Remaining().Length);
    }

    // -- Recency ------------------------------------------------------------

    [Fact]
    public void MarkUsedRefreshesAStaleFileSoItSurvivesATrim()
    {
        var kept = Write("kept.png", 1000, TimeSpan.FromDays(10));
        Write("other.png", 1000, TimeSpan.FromDays(5));

        TileDiskCache.MarkUsed(kept, staleAfter: TimeSpan.FromDays(1));
        TileDiskCache.Trim(_dir, maxBytes: 1500, targetBytes: 1000);

        // Without the refresh, kept.png would have been the one to go.
        Assert.Equal(["kept.png"], Remaining());
    }

    [Fact]
    public void MarkUsedLeavesAFreshFileAloneSoServingCostsNoWrite()
    {
        var path = Write("fresh.png", 1000, TimeSpan.FromHours(2));
        var before = File.GetLastWriteTimeUtc(path);

        TileDiskCache.MarkUsed(path, staleAfter: TimeSpan.FromDays(1));

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void MarkUsedOnAMissingFileIsHarmless()
    {
        TileDiskCache.MarkUsed(Path.Combine(_dir, "gone.png"), TimeSpan.FromDays(1));
    }

    // -- Edges --------------------------------------------------------------

    [Fact]
    public void AMissingDirectoryIsEmptyRatherThanAnError() =>
        Assert.Equal(0, TileDiskCache.Trim(
            Path.Combine(_dir, "no-such-dir"), maxBytes: 100, targetBytes: 50));

    [Fact]
    public void AZeroTargetEmptiesTheCache()
    {
        Write("a.png", 1000, TimeSpan.FromDays(2));
        Write("b.png", 1000, TimeSpan.FromDays(1));

        Assert.Equal(0, TileDiskCache.Trim(_dir, maxBytes: 500, targetBytes: 0));
        Assert.Empty(Remaining());
    }

    [Fact]
    public void ATargetAboveTheCeilingIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TileDiskCache.Trim(_dir, maxBytes: 100, targetBytes: 200));
}
