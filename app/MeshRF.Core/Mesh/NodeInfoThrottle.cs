// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Mesh;

/// <summary>
/// Firmware's two brakes on sending our NodeInfo, both of which sit in
/// <c>NodeInfoModule</c> behind every path that would put one on the air: the
/// window since the last one went out, and the twelve-hour memory of who we
/// have already answered.
///
/// The trigger they hold back is <c>MeshService::handleFromRadio</c> — hearing a
/// node we have no name for, introduce ourselves. That fires on every packet
/// from such a node, and a node whose reply never reaches us (out of range for
/// the unicast, quiet under its own suppression, heard only through a repeater)
/// never stops being nameless. Without a window, the introduction is every
/// packet, forever.
///
/// The send window is kept per mesh, which firmware has no need for: a node
/// listening on several presets at once is several nodes as far as the air is
/// concerned, and a NodeInfo on one of them is not one the others heard. The
/// reply memory is not — a peer that asked over either mesh is one device with
/// one node database, and it has our name.
/// </summary>
public sealed class NodeInfoThrottle
{
    /// <summary>
    /// Firmware's base window in <c>allocReply</c>, before congestion scaling
    /// stretches it: ten minutes between NodeInfo transmits.
    /// </summary>
    public const int SendWindowSeconds = 10 * 60;

    /// <summary>Firmware's <c>USERPREFS_NODEINFO_REPLY_SUPPRESS_SECS</c>.</summary>
    public static readonly TimeSpan ReplySuppressWindow = TimeSpan.FromHours(12);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastSentUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, DateTime> _lastRequestUtc = new();

    /// <summary>
    /// Notes a NodeInfo going out on a mesh, whatever sent it. Firmware keeps
    /// one per-port transmit history, so an introduction, a reply to a request
    /// and the periodic beacon all spend the same budget — and it stamps when
    /// it decides to send rather than when the transmit completes.
    /// </summary>
    /// <param name="mesh">The settings and frequency it goes out on — see
    /// <see cref="TxTarget.MeshTag"/>.</param>
    public void MarkSent(string mesh, DateTime? nowUtc = null)
    {
        lock (_gate) _lastSentUtc[mesh] = nowUtc ?? DateTime.UtcNow;
    }

    /// <summary>Whether the window since our last NodeInfo on this mesh has run
    /// out.</summary>
    /// <param name="mesh">The mesh the send would go out on — see
    /// <see cref="TxTarget.MeshTag"/>.</param>
    /// <param name="windowSeconds">The window after congestion scaling — see
    /// <see cref="BroadcastIntervals.ScaledSeconds"/>.</param>
    /// <param name="sinceLast">How long ago the last one went out, for the line
    /// that explains a refusal. <see cref="TimeSpan.MaxValue"/> when none has.</param>
    public bool AllowsSend(string mesh, int windowSeconds, out TimeSpan sinceLast, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            if (!_lastSentUtc.TryGetValue(mesh, out var last))
            {
                sinceLast = TimeSpan.MaxValue;
                return true;
            }
            sinceLast = now - last;
            // A clock that stepped backwards leaves a negative gap, which would
            // otherwise hold every NodeInfo until wall time caught up again.
            if (sinceLast < TimeSpan.Zero) return true;
            return sinceLast >= TimeSpan.FromSeconds(Math.Max(0, windowSeconds));
        }
    }

    /// <summary>
    /// Firmware's reply memory: a peer that asked us inside the last twelve
    /// hours gets no second answer.
    /// </summary>
    /// <remarks>
    /// The stamp is refreshed by the request rather than by the reply, so a peer
    /// that keeps asking keeps its own window open. That is the intent — it is
    /// asking for something it was already told, and a node that genuinely lost
    /// our name has our periodic NodeInfo coming anyway.
    /// </remarks>
    /// <param name="maxEntries">Ceiling on the table, firmware's node-database
    /// size: a node we no longer hold cannot be one we owe an answer to.</param>
    public bool SuppressReplyTo(uint requester, int maxEntries, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            bool suppress = _lastRequestUtc.TryGetValue(requester, out var lastAsked)
                            && now - lastAsked >= TimeSpan.Zero
                            && now - lastAsked < ReplySuppressWindow;
            _lastRequestUtc[requester] = now;
            PruneLocked(now, maxEntries);
            return suppress;
        }
    }

    private void PruneLocked(DateTime now, int maxEntries)
    {
        // A stamp past the window can only ever answer "don't suppress", which
        // is what a missing one answers, so keeping it buys nothing.
        foreach (var stale in _lastRequestUtc
                     .Where(kv => now - kv.Value >= ReplySuppressWindow)
                     .Select(kv => kv.Key)
                     .ToList())
            _lastRequestUtc.Remove(stale);

        int cap = Math.Max(1, maxEntries);
        while (_lastRequestUtc.Count > cap)
        {
            uint oldest = _lastRequestUtc.OrderBy(kv => kv.Value).First().Key;
            _lastRequestUtc.Remove(oldest);
        }
    }
}
