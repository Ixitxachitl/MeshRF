// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using Xunit;

namespace MeshRF.Tests;

public class NodeInfoThrottleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const int Window = NodeInfoThrottle.SendWindowSeconds;
    private const string Mesh = "MediumFast 913.125";
    private const string OtherMesh = "LongFast 906.875";

    [Fact]
    public void FirstSendIsAllowed()
    {
        var throttle = new NodeInfoThrottle();
        Assert.True(throttle.AllowsSend(Mesh, Window, out _, T0));
    }

    // Firmware's allocReply refuses inside the window and allows once it passes.
    [Fact]
    public void WindowHoldsThenReleases()
    {
        var throttle = new NodeInfoThrottle();
        throttle.MarkSent(Mesh, T0);

        Assert.False(throttle.AllowsSend(Mesh, Window, out var sinceLast, T0.AddMinutes(9)));
        Assert.Equal(TimeSpan.FromMinutes(9), sinceLast);
        Assert.True(throttle.AllowsSend(Mesh, Window, out _, T0.AddMinutes(10)));
    }

    // One budget for every path: an introduction leaves the periodic beacon and
    // the reply to a request both holding, as firmware's transmit history does.
    [Fact]
    public void EveryPathSpendsTheSameBudget()
    {
        var throttle = new NodeInfoThrottle();
        throttle.MarkSent(Mesh, T0);
        Assert.False(throttle.AllowsSend(Mesh, Window, out _, T0.AddMinutes(1)));

        throttle.MarkSent(Mesh, T0.AddMinutes(9));
        Assert.False(throttle.AllowsSend(Mesh, Window, out _, T0.AddMinutes(18)));
        Assert.True(throttle.AllowsSend(Mesh, Window, out _, T0.AddMinutes(19)));
    }

    // A node listening on two presets is two nodes as far as the air is
    // concerned: a NodeInfo on one mesh is not one the other heard.
    [Fact]
    public void MeshesHoldSeparateWindows()
    {
        var throttle = new NodeInfoThrottle();
        throttle.MarkSent(Mesh, T0);

        Assert.False(throttle.AllowsSend(Mesh, Window, out _, T0.AddMinutes(1)));
        Assert.True(throttle.AllowsSend(OtherMesh, Window, out _, T0.AddMinutes(1)));

        throttle.MarkSent(OtherMesh, T0.AddMinutes(1));
        Assert.False(throttle.AllowsSend(OtherMesh, Window, out _, T0.AddMinutes(2)));
        Assert.True(throttle.AllowsSend(Mesh, Window, out _, T0.AddMinutes(11)));
    }

    // The congestion coefficient stretches this window as it stretches the
    // periodic intervals, so a busy mesh introduces itself less often.
    [Fact]
    public void ScaledWindowHoldsLongerOnABusyMesh()
    {
        int scaled = BroadcastIntervals.ScaledSeconds(Window, "Client", 200, LoraPreset.LongFast);
        Assert.True(scaled > Window);

        var throttle = new NodeInfoThrottle();
        throttle.MarkSent(Mesh, T0);
        Assert.False(throttle.AllowsSend(Mesh, scaled, out _, T0.AddSeconds(Window + 1)));
    }

    [Fact]
    public void ClockStepBackwardsDoesNotHoldTheWindowOpen()
    {
        var throttle = new NodeInfoThrottle();
        throttle.MarkSent(Mesh, T0);
        Assert.True(throttle.AllowsSend(Mesh, Window, out _, T0.AddHours(-2)));
    }

    // Firmware's 12 h reply memory: the first ask is answered, a second one
    // inside the window is not.
    [Fact]
    public void SecondRequestInsideTwelveHoursIsSuppressed()
    {
        var throttle = new NodeInfoThrottle();
        Assert.False(throttle.SuppressReplyTo(0x1234, 64, T0));
        Assert.True(throttle.SuppressReplyTo(0x1234, 64, T0.AddHours(11)));
    }

    [Fact]
    public void RequestAfterTwelveHoursIsAnswered()
    {
        var throttle = new NodeInfoThrottle();
        Assert.False(throttle.SuppressReplyTo(0x1234, 64, T0));
        Assert.False(throttle.SuppressReplyTo(0x1234, 64, T0.AddHours(12)));
    }

    // The stamp follows the request, not the reply, so a peer that keeps asking
    // keeps its own window open — 23 h after the first ask it is still quiet.
    [Fact]
    public void AskingAgainRefreshesTheWindow()
    {
        var throttle = new NodeInfoThrottle();
        Assert.False(throttle.SuppressReplyTo(0x1234, 64, T0));
        Assert.True(throttle.SuppressReplyTo(0x1234, 64, T0.AddHours(11)));
        Assert.True(throttle.SuppressReplyTo(0x1234, 64, T0.AddHours(22)));
    }

    [Fact]
    public void SuppressionIsPerRequester()
    {
        var throttle = new NodeInfoThrottle();
        Assert.False(throttle.SuppressReplyTo(0x1111, 64, T0));
        Assert.False(throttle.SuppressReplyTo(0x2222, 64, T0.AddMinutes(1)));
        Assert.True(throttle.SuppressReplyTo(0x1111, 64, T0.AddMinutes(2)));
    }

    // Bounded like firmware's cache: past the ceiling the oldest ask is dropped,
    // and the node it belonged to is answered again.
    [Fact]
    public void TableIsBoundedByTheNodeCount()
    {
        var throttle = new NodeInfoThrottle();
        Assert.False(throttle.SuppressReplyTo(0x1111, 2, T0));
        Assert.False(throttle.SuppressReplyTo(0x2222, 2, T0.AddMinutes(1)));
        Assert.False(throttle.SuppressReplyTo(0x3333, 2, T0.AddMinutes(2)));

        Assert.False(throttle.SuppressReplyTo(0x1111, 2, T0.AddMinutes(3)));
        Assert.True(throttle.SuppressReplyTo(0x3333, 2, T0.AddMinutes(4)));
    }

    // An expired stamp only ever answers "don't suppress", so it is dropped
    // rather than left to fill the table.
    [Fact]
    public void ExpiredStampsAreDropped()
    {
        var throttle = new NodeInfoThrottle();
        Assert.False(throttle.SuppressReplyTo(0x1111, 64, T0));
        Assert.False(throttle.SuppressReplyTo(0x2222, 64, T0.AddHours(13)));
        // 0x1111's stamp is gone with the sweep, so it is treated as a stranger.
        Assert.False(throttle.SuppressReplyTo(0x1111, 64, T0.AddHours(13)));
    }
}
