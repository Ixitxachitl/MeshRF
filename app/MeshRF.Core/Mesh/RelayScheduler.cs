// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Holds rebroadcasts that are waiting out their contention delay, and resolves
/// what happens when another copy of the same packet arrives first.
///
/// The delay is the whole point of flooding politeness: every node that heard a
/// packet wants to relay it, so each waits a weighted random interval and drops
/// its own copy if someone else transmits first. Without that, one packet
/// becomes as many rebroadcasts as there were listeners.
/// </summary>
public sealed class RelayScheduler : IDisposable
{
    private sealed class Pending
    {
        public required CancellationTokenSource Cts { get; init; }

        /// <summary>The frame as it will go out, kept so a clamp can re-arm the
        /// same rebroadcast at a later time.</summary>
        public required byte[] RelayFrame { get; init; }

        /// <summary>hop_limit we will send at, i.e. already decremented.</summary>
        public required byte NextHopLimit { get; init; }

        /// <summary>hop_limit of the copy we heard, before any decrement. This —
        /// not <see cref="NextHopLimit"/> — is what a later duplicate has to beat
        /// to count as having taken a shorter path, since that is the like-for-like
        /// comparison (firmware keeps the highest received hop limit per packet,
        /// PacketHistory.cpp).</summary>
        public required byte ReceivedHopLimit { get; init; }

        /// <summary>What the rebroadcast goes out on: the listener the packet
        /// arrived on, since a relay stays on the mesh it came from.</summary>
        public required TxTarget Target { get; init; }

        /// <summary>Set once the delay has elapsed and we have committed to
        /// sending. From then on the relay can no longer be called off.</summary>
        public bool Transmitting;
    }

    /// <summary>How many of our own rebroadcasts to remember. Only has to cover
    /// the flight time of a frame we are transmitting right now, so this is
    /// generous.</summary>
    private const int RecentRelayLimit = 128;

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Pending> _pending = new();
    private readonly HashSet<ulong> _recentRelayKeys = new();
    private readonly Queue<ulong> _recentRelayOrder = new();
    private bool _disposed;

    /// <summary>Transmits the prepared frame on the given target. Runs off the
    /// caller's thread after the delay elapses.</summary>
    public required Func<byte[], TxTarget, Task> Transmit { get; init; }

    public Action<string>? Log { get; init; }

    private static ulong Key(uint from, uint packetId) => ((ulong)from << 32) | packetId;

    /// <summary>Queues a rebroadcast. A packet already scheduled is left alone —
    /// re-arming on every heard copy would push the transmission later each
    /// time and never fire.</summary>
    public void Schedule(MeshHeader header, byte[] relayFrame, byte nextHopLimit, int delayMs, TxTarget target)
    {
        var key = Key(header.From, header.PacketId);
        lock (_lock)
        {
            if (_disposed || _pending.ContainsKey(key)) return;
        }
        Arm(key, header, relayFrame, nextHopLimit, header.HopLimit, delayMs, target);
    }

    /// <summary>
    /// Slides an already-scheduled rebroadcast to the end of the contention
    /// window, mirroring firmware's clampToLateRebroadcastWindow. Used by roles
    /// that must relay but shouldn't transmit over the station we just heard.
    /// No-ops once the send is committed, or if there is nothing pending.
    /// </summary>
    public void ClampToLateWindow(MeshHeader header, int delayMs)
    {
        var key = Key(header.From, header.PacketId);
        Pending? pending;
        lock (_lock)
        {
            if (_disposed) return;
            if (!_pending.TryGetValue(key, out pending) || pending.Transmitting) return;
            _pending.Remove(key);
        }

        try { pending.Cts.Cancel(); } catch { /* already completed */ }
        Arm(key, header, pending.RelayFrame, pending.NextHopLimit, pending.ReceivedHopLimit, delayMs, pending.Target);
        Log?.Invoke($"  relay for packet {header.PacketId:x8} clamped to late window ({delayMs} ms)");
    }

    private void Arm(ulong key, MeshHeader header, byte[] relayFrame, byte nextHopLimit,
                     byte receivedHopLimit, int delayMs, TxTarget target)
    {
        Pending entry;
        lock (_lock)
        {
            if (_disposed) return;
            entry = new Pending
            {
                Cts = new CancellationTokenSource(),
                RelayFrame = relayFrame,
                NextHopLimit = nextHopLimit,
                ReceivedHopLimit = receivedHopLimit,
                Target = target,
            };
            _pending[key] = entry;
        }
        var cts = entry.Cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                // Latch before awaiting the send: Transmit waits on the TX
                // semaphore and for a clear channel, which can take seconds, and
                // a duplicate arriving in that window must not be able to cancel
                // (pointless — the frame is already committed) or, worse, drop
                // the entry and let a second relay of the same packet be queued.
                lock (_lock) entry.Transmitting = true;
                // Remember it before the send, not after: a receive SDR running
                // alongside the transmitter hears the frame while Transmit is
                // still awaiting, so the record has to already be in place.
                RememberRelayed(key);
                await Transmit(relayFrame, target).ConfigureAwait(false);
                Log?.Invoke($"  relayed packet {header.PacketId:x8} ({header.HopLimit}->{nextHopLimit}) after {delayMs} ms");
            }
            catch (OperationCanceledException)
            {
                // Someone else relayed it first; that's the design working.
            }
            catch (Exception ex)
            {
                Log?.Invoke($"  relay failed for packet {header.PacketId:x8}: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                        _pending.Remove(key);
                }
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// A duplicate arrived. Mirrors firmware's two-step handling in
    /// FloodingRouter::shouldFilterReceived:
    ///
    /// If this copy has more hops left than the one queued, it took a shorter
    /// path — drop ours and relay the better one instead, so the packet keeps
    /// as much reach as possible. Otherwise cancel ours, since the mesh has
    /// already heard it; roles that must always relay are exempt.
    /// </summary>
    /// <returns>True when the caller should re-run its relay decision with this
    /// copy (the upgrade case).</returns>
    public bool HandleDuplicate(RelayContext ctx, MeshHeader header, float snrDb)
    {
        if (header.PacketId == 0) return false;

        var key = Key(header.From, header.PacketId);
        Pending? pending;
        lock (_lock)
        {
            _pending.TryGetValue(key, out pending);
            // Read under the lock: past this point the send is committed, so
            // there is nothing left to cancel or upgrade.
            if (pending is { Transmitting: true }) return false;
        }
        bool hasPending = pending is not null;

        // Compare received-to-received. Against NextHopLimit (already
        // decremented) an equal-distance duplicate looks like a shorter path,
        // so two neighbours the same number of hops away would each re-arm us
        // instead of cancelling — exactly the duplicate suppression this class
        // exists to provide.
        if (hasPending && header.HopLimit > pending!.ReceivedHopLimit && header.HopLimit > 0 &&
            RelayPolicy.IsRoutingRoleEnabled(ctx.Role))
        {
            Cancel(key, pending, log: false);
            Log?.Invoke($"  relay upgraded for packet {header.PacketId:x8} (hop_limit {pending.ReceivedHopLimit} -> {header.HopLimit})");
            return true;
        }

        if (!RelayPolicy.RoleAllowsCancelingScheduledRelay(ctx, header))
        {
            // We still have to relay, but not on top of whoever just did: slide
            // our copy to the back of the window instead of firing on schedule.
            if (RelayPolicy.ShouldClampToLateWindow(ctx, header))
                ClampToLateWindow(header, RelayPolicy.GetTxDelayMsecWeightedWorst(ctx.Preset, snrDb));
            return false;
        }

        if (hasPending) Cancel(key, pending!, log: true);
        return false;
    }

    /// <summary>
    /// Whether we put this exact packet on the air ourselves, recently enough
    /// that hearing it now is our own echo rather than someone else's copy.
    ///
    /// A rebroadcast keeps the original sender in its header, so the isFromUs
    /// test that catches packets we *originated* never fires for one we merely
    /// relayed. With a separate receive SDR we hear every relay we transmit, and
    /// without this the echo counts as a fresh sighting of the original sender —
    /// at our own transmitter's signal strength and one hop too far away.
    /// </summary>
    public bool WasRelayedByUs(uint from, uint packetId)
    {
        if (packetId == 0) return false;
        lock (_lock) return _recentRelayKeys.Contains(Key(from, packetId));
    }

    private void RememberRelayed(ulong key)
    {
        lock (_lock)
        {
            if (!_recentRelayKeys.Add(key)) return;
            _recentRelayOrder.Enqueue(key);
            while (_recentRelayOrder.Count > RecentRelayLimit)
                _recentRelayKeys.Remove(_recentRelayOrder.Dequeue());
        }
    }

    private void Cancel(ulong key, Pending pending, bool log)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                _pending.Remove(key);
        }

        try { pending.Cts.Cancel(); } catch { /* already completed */ }
        if (log) Log?.Invoke($"  relay canceled for duplicate packet {(uint)key:x8}");
    }

    public void Dispose()
    {
        List<Pending> outstanding;
        lock (_lock)
        {
            _disposed = true;
            outstanding = _pending.Values.ToList();
            _pending.Clear();
        }
        foreach (var p in outstanding)
        {
            try { p.Cts.Cancel(); } catch { }
            p.Cts.Dispose();
        }
    }
}
