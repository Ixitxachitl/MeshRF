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
    private readonly record struct Pending(CancellationTokenSource Cts, byte NextHopLimit);

    private readonly object _lock = new();
    private readonly Dictionary<ulong, Pending> _pending = new();
    private bool _disposed;

    /// <summary>Transmits the prepared frame. Runs off the caller's thread after
    /// the delay elapses.</summary>
    public required Func<byte[], Task> Transmit { get; init; }

    public Action<string>? Log { get; init; }

    private static ulong Key(uint from, uint packetId) => ((ulong)from << 32) | packetId;

    /// <summary>Queues a rebroadcast. A packet already scheduled is left alone —
    /// re-arming on every heard copy would push the transmission later each
    /// time and never fire.</summary>
    public void Schedule(MeshHeader header, byte[] relayFrame, byte nextHopLimit, int delayMs)
    {
        var key = Key(header.From, header.PacketId);
        CancellationTokenSource cts;

        lock (_lock)
        {
            if (_disposed || _pending.ContainsKey(key)) return;
            cts = new CancellationTokenSource();
            _pending[key] = new Pending(cts, nextHopLimit);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested) return;
                await Transmit(relayFrame).ConfigureAwait(false);
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
                    if (_pending.TryGetValue(key, out var current) && current.Cts == cts)
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
    public bool HandleDuplicate(RelayContext ctx, MeshHeader header)
    {
        if (header.PacketId == 0) return false;

        var key = Key(header.From, header.PacketId);
        Pending pending;
        bool hasPending;
        lock (_lock) hasPending = _pending.TryGetValue(key, out pending);

        if (hasPending && header.HopLimit > pending.NextHopLimit && header.HopLimit > 0 &&
            RelayPolicy.IsRoutingRoleEnabled(ctx.Role))
        {
            Cancel(key, pending, log: false);
            Log?.Invoke($"  relay upgraded for packet {header.PacketId:x8} (hop_limit {pending.NextHopLimit} -> {header.HopLimit})");
            return true;
        }

        if (!RelayPolicy.RoleAllowsCancelingScheduledRelay(ctx, header)) return false;
        if (hasPending) Cancel(key, pending, log: true);
        return false;
    }

    private void Cancel(ulong key, Pending pending, bool log)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(key, out var current) && current.Cts == pending.Cts)
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
