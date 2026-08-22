// SPDX-License-Identifier: GPL-3.0-or-later
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// The rule that stops a failing fetch being retried on every redraw.
/// </summary>
public class FetchBackoffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private static FetchBackoff Backoff(int capacity = 2000) =>
        new(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), capacity);

    [Fact]
    public void SomethingNeverTriedMayBeFetched()
    {
        Assert.True(Backoff().ShouldTry("tile", T0));
    }

    [Fact]
    public void AFailureHoldsTheKeyBackForTheFirstWait()
    {
        var backoff = Backoff();
        backoff.Failed("tile", T0);

        // The case that mattered: a render a quarter of a second later must not
        // ask again.
        Assert.False(backoff.ShouldTry("tile", T0 + TimeSpan.FromMilliseconds(250)));
        Assert.False(backoff.ShouldTry("tile", T0 + TimeSpan.FromSeconds(4.9)));
        Assert.True(backoff.ShouldTry("tile", T0 + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AQuarterSecondRenderLoopGetsOneRequestPerWaitRatherThanOnePerFrame()
    {
        var backoff = Backoff();
        int attempts = 0;

        // Ten minutes of rendering four times a second against a tile that
        // never comes back. Unbounded this is 2,400 requests.
        for (int tick = 0; tick < 10 * 60 * 4; tick++)
        {
            var now = T0 + TimeSpan.FromMilliseconds(250 * tick);
            if (!backoff.ShouldTry("tile", now)) continue;
            attempts++;
            backoff.Failed("tile", now);
        }

        // Attempts land at 0s, 5s, 15s, 35s, 75s, 155s and 315s; the next is due
        // at 635s, past the end. Seven requests where there would have been
        // 2,400.
        Assert.Equal(7, attempts);
    }

    [Fact]
    public void TheWaitDoublesWithEachConsecutiveFailure()
    {
        var backoff = Backoff();
        var now = T0;

        foreach (var expected in new[] { 5, 10, 20, 40, 80 })
        {
            backoff.Failed("tile", now);
            Assert.Equal(TimeSpan.FromSeconds(expected), backoff.RetryIn("tile", now));
            now += TimeSpan.FromSeconds(expected);
        }
    }

    [Fact]
    public void TheWaitStopsAtTheCeiling()
    {
        var backoff = Backoff();
        var now = T0;
        for (int i = 0; i < 40; i++)
        {
            backoff.Failed("tile", now);
            now += backoff.RetryIn("tile", now);
        }

        backoff.Failed("tile", now);
        Assert.Equal(TimeSpan.FromMinutes(10), backoff.RetryIn("tile", now));
    }

    [Fact]
    public void ASuccessForgetsTheKeySoTheNextFailureStartsShort()
    {
        var backoff = Backoff();
        var now = T0;
        for (int i = 0; i < 5; i++) { backoff.Failed("tile", now); now += TimeSpan.FromMinutes(20); }

        backoff.Succeeded("tile");
        Assert.True(backoff.ShouldTry("tile", now));

        backoff.Failed("tile", now);
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.RetryIn("tile", now));
    }

    [Fact]
    public void KeysAreHeldBackIndependently()
    {
        var backoff = Backoff();
        backoff.Failed("a", T0);

        Assert.False(backoff.ShouldTry("a", T0));
        Assert.True(backoff.ShouldTry("b", T0));
    }

    [Fact]
    public void PanningAcrossTheWorldDoesNotGrowTheTableWithoutBound()
    {
        var backoff = Backoff(capacity: 100);
        var now = T0;

        // Every key fails once, all of them still inside their wait, so nothing
        // can be pruned for being elapsed.
        for (int i = 0; i < 1000; i++) backoff.Failed($"tile{i}", now);

        Assert.True(backoff.Count <= 100, $"held {backoff.Count} keys");
    }

    [Fact]
    public void ElapsedKeysArePrunedBeforeLiveOnes()
    {
        var backoff = Backoff(capacity: 100);

        for (int i = 0; i < 100; i++) backoff.Failed($"old{i}", T0);

        // Long enough that every key above is free again, then one fresh
        // failure to push the table over its capacity.
        var later = T0 + TimeSpan.FromMinutes(1);
        backoff.Failed("fresh", later);

        Assert.False(backoff.ShouldTry("fresh", later));
        Assert.True(backoff.ShouldTry("old0", later));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AFirstWaitOfZeroOrLessIsRefused(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FetchBackoff(TimeSpan.FromSeconds(seconds), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ACeilingShorterThanTheFirstWaitIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FetchBackoff(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)));
    }
}
