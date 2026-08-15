// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF.Mesh;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// Recognising our own rebroadcast when we hear it back. A relayed frame keeps
/// the original sender in its header, so the isFromUs check that catches packets
/// we originated never fires for one we merely carried — and a receive SDR
/// running alongside the transmitter hears every relay we make.
/// </summary>
public class RelaySelfEchoTests
{
    private const uint Me = 0xAABBCCDDu;
    private const uint Peer = 0x11223344u;

    private static MeshHeader Header(uint from = Peer, uint packetId = 0x1234u) =>
        new()
        {
            From = from,
            To = 0xFFFFFFFFu,
            PacketId = packetId,
            Flags = (byte)(3 | (3 << 5)),   // hop_limit 3, hop_start 3
        };

    private static RelayContext Context() =>
        new("Client", "All", Me, LoraPreset.LongFast,
            _ => null, Array.Empty<NodeRecord>, false);

    private static byte[] Frame() => new byte[MeshHeader.Size];

    [Fact]
    public async Task APacketWeRelayedIsRecognisedAsOurOwnEcho()
    {
        var sent = new TaskCompletionSource();
        using var scheduler = new RelayScheduler
        {
            Transmit = _ => { sent.TrySetResult(); return Task.CompletedTask; },
        };

        var header = Header();
        Assert.False(scheduler.WasRelayedByUs(header.From, header.PacketId));

        scheduler.Schedule(header, Frame(), nextHopLimit: 2, delayMs: 0);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.WasRelayedByUs(header.From, header.PacketId));
    }

    /// <summary>
    /// The echo arrives while the send is still in flight, so the record has to
    /// be in place before Transmit is awaited, not after it returns.
    /// </summary>
    [Fact]
    public async Task EchoIsRecognisedWhileTheFrameIsStillGoingOut()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var scheduler = new RelayScheduler
        {
            Transmit = async _ => { started.TrySetResult(); await release.Task; },
        };

        var header = Header();
        scheduler.Schedule(header, Frame(), nextHopLimit: 2, delayMs: 0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduler.WasRelayedByUs(header.From, header.PacketId));
        release.TrySetResult();
    }

    /// <summary>A relay another station beat us to never went out, so a later
    /// copy of that packet is someone else's transmission, not our echo.</summary>
    [Fact]
    public void ACanceledRelayIsNotRememberedAsOurs()
    {
        using var scheduler = new RelayScheduler { Transmit = _ => Task.CompletedTask };

        var header = Header();
        scheduler.Schedule(header, Frame(), nextHopLimit: 2, delayMs: 30_000);
        scheduler.HandleDuplicate(Context(), header, snrDb: 0f);

        Assert.False(scheduler.WasRelayedByUs(header.From, header.PacketId));
    }

    [Fact]
    public async Task OnlyTheExactPacketWeSentCounts()
    {
        var sent = new TaskCompletionSource();
        using var scheduler = new RelayScheduler
        {
            Transmit = _ => { sent.TrySetResult(); return Task.CompletedTask; },
        };

        var header = Header();
        scheduler.Schedule(header, Frame(), nextHopLimit: 2, delayMs: 0);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(scheduler.WasRelayedByUs(header.From, header.PacketId + 1));
        Assert.False(scheduler.WasRelayedByUs(header.From + 1, header.PacketId));
        // Packet id 0 is the "no id" sentinel and must never match.
        Assert.False(scheduler.WasRelayedByUs(header.From, 0));
    }
}
