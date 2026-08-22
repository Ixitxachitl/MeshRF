// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// Remembers which fetches are failing, and how long to leave them alone.
/// </summary>
/// <remarks>
/// <para>For anything fetched on demand and cached on success: a map tile, an
/// icon, a thumbnail. A failed fetch lands in no cache, so whatever wanted it
/// asks again the moment it next looks — and a caller driven by redraws looks
/// several times a second. That turns a provider's rate limit into a hot loop
/// against it at the exact moment it has asked to be left alone.</para>
/// <para>The wait doubles with each consecutive failure and stops at a ceiling,
/// so a transient blip costs one short pause while a sustained refusal settles
/// into occasional retries. A success forgets the key outright: the thing is
/// reachable again, and its next failure should start from the short wait
/// rather than from wherever the last run of failures had climbed to.</para>
/// </remarks>
public sealed class FetchBackoff
{
    private readonly record struct Entry(DateTimeOffset Until, int Failures);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeSpan _first;
    private readonly TimeSpan _max;
    private readonly int _capacity;

    /// <param name="first">How long a key waits after its first failure.</param>
    /// <param name="max">Ceiling the doubling stops at.</param>
    /// <param name="capacity">How many keys are remembered before the table is
    /// pruned. Reached only by a caller ranging over a large key space, such as
    /// a map panned across the world.</param>
    public FetchBackoff(TimeSpan first, TimeSpan max, int capacity = 2000)
    {
        if (first <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(first), "the first wait has to be longer than zero");
        if (max < first)
            throw new ArgumentOutOfRangeException(nameof(max), "the ceiling cannot be shorter than the first wait");
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "at least one key has to be rememberable");

        _first = first;
        _max = max;
        _capacity = capacity;
    }

    /// <summary>How many keys are currently remembered, whether still waiting
    /// or merely not yet pruned.</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Whether this key may be fetched now. True for anything never
    /// tried, and for anything whose wait has elapsed.</summary>
    public bool ShouldTry(string key, DateTimeOffset now)
    {
        lock (_gate) return !_entries.TryGetValue(key, out var entry) || now >= entry.Until;
    }

    /// <summary>What is left of this key's wait, or zero if it may be tried.
    /// </summary>
    public TimeSpan RetryIn(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || now >= entry.Until) return TimeSpan.Zero;
            return entry.Until - now;
        }
    }

    /// <summary>Records a failure, doubling the wait this key serves.</summary>
    public void Failed(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            int failures = _entries.TryGetValue(key, out var was) ? was.Failures + 1 : 1;

            // The ceiling is tested by shifting it down rather than by shifting
            // the wait up and comparing: a key that has failed enough times
            // would overflow a long on the way up and wrap to a negative wait,
            // which is to say no wait at all — the exact hot loop this exists
            // to prevent, arriving only after a provider had been down a while.
            int doublings = failures - 1;
            long ticks = doublings >= 62 || _first.Ticks > _max.Ticks >> doublings
                ? _max.Ticks
                : _first.Ticks << doublings;

            _entries[key] = new Entry(now + TimeSpan.FromTicks(ticks), failures);
            Prune(now);
        }
    }

    /// <summary>Forgets a key, so a later failure starts from the first wait.
    /// </summary>
    public void Succeeded(string key)
    {
        lock (_gate) _entries.Remove(key);
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>
    /// Keeps the table bounded as the caller ranges over new keys.
    /// </summary>
    /// <remarks>
    /// Keys whose wait has elapsed go first, since they no longer hold anything
    /// back and would be refetched on the next ask regardless. Only if that
    /// leaves the table still over its capacity does the rest go with them,
    /// which costs a round of retries against something already failing but is
    /// preferable to remembering the whole world.
    /// </remarks>
    private void Prune(DateTimeOffset now)
    {
        if (_entries.Count <= _capacity) return;

        foreach (var key in _entries.Where(e => now >= e.Value.Until).Select(e => e.Key).ToList())
            _entries.Remove(key);

        if (_entries.Count > _capacity) _entries.Clear();
    }
}
