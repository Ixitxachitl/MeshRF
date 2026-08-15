// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// Decides whether a script is allowed to fire right now.
/// </summary>
/// <remarks>
/// <para>Three independent ceilings, all of which have to pass: the script's
/// own cooldown, the script's hourly cap, and a global hourly budget shared by
/// every script.</para>
/// <para>The global budget is the one that matters most. Per-script limits are
/// written by the same person who wrote the mistake, so a runaway regex with
/// <c>max_per_hour: 1000</c> would sail straight past them; the global budget
/// is the ceiling they cannot raise from inside a script file, and it is what
/// stands between a bad afternoon of editing and a channel nobody else can
/// use.</para>
/// </remarks>
public sealed class ScriptRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Cooldown clocks, keyed by script and (when per_node is on) by
    /// the node that set it off. Node 0 is the shared, not-per-node slot.</summary>
    private readonly Dictionary<(string Script, uint Node), DateTimeOffset> _lastFired = new();

    private readonly Dictionary<string, Queue<DateTimeOffset>> _perScriptHour = new(StringComparer.Ordinal);

    private readonly Queue<DateTimeOffset> _globalHour = new();

    /// <summary>Firings per hour allowed across all scripts together.</summary>
    public int GlobalMaxPerHour { get; set; } = 30;

    /// <summary>
    /// Checks every ceiling and, if all pass, records the firing.
    /// </summary>
    /// <param name="scriptName">The script's file name.</param>
    /// <param name="limits">That script's declared limits.</param>
    /// <param name="triggerNode">Node that set it off, 0 for a timer.</param>
    /// <param name="now">Current time.</param>
    /// <param name="consumesAirtime">False for a run that only logs and waits.
    /// Such a run still observes its own cooldown, but is not charged against
    /// the global budget, which exists to ration the channel rather than to
    /// ration script activity as such.</param>
    /// <param name="reason">Why it was refused, for the log.</param>
    public bool TryFire(
        string scriptName, ScriptLimits limits, uint triggerNode, DateTimeOffset now,
        bool consumesAirtime, out string reason)
    {
        var cooldownKey = (scriptName, limits.PerNode ? triggerNode : 0u);
        if (_lastFired.TryGetValue(cooldownKey, out var last) && limits.Cooldown > TimeSpan.Zero)
        {
            var elapsed = now - last;
            if (elapsed < limits.Cooldown)
            {
                var left = limits.Cooldown - elapsed;
                reason = $"cooling down, {left.TotalSeconds:0}s left" +
                         (limits.PerNode && triggerNode != 0 ? " for this node" : string.Empty);
                return false;
            }
        }

        var scriptHour = Prune(GetQueue(scriptName), now);
        if (limits.MaxPerHour > 0 && scriptHour.Count >= limits.MaxPerHour)
        {
            reason = $"hit its own limit of {limits.MaxPerHour}/hour";
            return false;
        }

        Prune(_globalHour, now);
        if (consumesAirtime && GlobalMaxPerHour > 0 && _globalHour.Count >= GlobalMaxPerHour)
        {
            reason = $"hit the global budget of {GlobalMaxPerHour}/hour across all scripts";
            return false;
        }

        _lastFired[cooldownKey] = now;
        scriptHour.Enqueue(now);
        if (consumesAirtime) _globalHour.Enqueue(now);
        reason = string.Empty;
        return true;
    }

    /// <summary>Firings in the last hour across every script, for the status
    /// line in the Scripts window.</summary>
    public int FiredInLastHour(DateTimeOffset now) => Prune(_globalHour, now).Count;

    /// <summary>Drops everything remembered. Called when scripts are reloaded:
    /// the cooldowns belong to a set of scripts that no longer exists, and a
    /// user who just edited a script expects to be able to test it.</summary>
    public void Reset()
    {
        _lastFired.Clear();
        _perScriptHour.Clear();
        _globalHour.Clear();
    }

    private Queue<DateTimeOffset> GetQueue(string scriptName)
    {
        if (_perScriptHour.TryGetValue(scriptName, out var queue)) return queue;
        return _perScriptHour[scriptName] = new Queue<DateTimeOffset>();
    }

    private static Queue<DateTimeOffset> Prune(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && now - queue.Peek() >= Window) queue.Dequeue();
        return queue;
    }
}
