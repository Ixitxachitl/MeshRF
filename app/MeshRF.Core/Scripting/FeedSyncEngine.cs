// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MeshRF.Scripting;

/// <summary>One loaded feed, and when it is next due.</summary>
/// <param name="FileName">The file it came from, which is its identity.</param>
/// <param name="Sync">What to fetch and what to place.</param>
public sealed record FeedSyncDue(string FileName, MeshFeedSync Sync);

/// <summary>
/// Keeps a set of waypoints in step with a list of records from a feed.
/// </summary>
/// <remarks>
/// <para>Holds what it last saw so it can tell a new record from a changed one
/// from one that has gone. That memory is the whole reason this exists rather
/// than being a script: a record leaving a feed is not an event anything can
/// trigger on.</para>
/// <para>The memory is deliberately in-process and not persisted, which is safe
/// because of two other choices. A marker's waypoint id is derived from the
/// record's own id, so re-sending after a restart replaces rather than
/// duplicates. And a marker this node forgot about — because it was closed when
/// the record went away — can be cleared by whoever holds it, since these are
/// sent unlocked.</para>
/// </remarks>
public sealed class FeedSyncEngine
{
    /// <summary>What was last sent for one record.</summary>
    private sealed class Tracked
    {
        public required uint WaypointId { get; init; }
        public required string Fingerprint { get; set; }
        public required DateTimeOffset LastSent { get; set; }
        public required double Latitude { get; set; }
        public required double Longitude { get; set; }
        public required string Name { get; set; }
        public required string Description { get; set; }
    }

    private sealed class Loaded
    {
        public required string FileName { get; init; }
        public required MeshFeedSync Sync { get; init; }
        public DateTimeOffset NextDue { get; set; }
        public Dictionary<string, Tracked> Seen { get; } = new(StringComparer.Ordinal);
    }

    private readonly List<Loaded> _feeds = [];

    public int ArmedCount => _feeds.Count;

    public IReadOnlyList<string> ArmedNames => _feeds.Select(f => f.FileName).ToList();

    public event Action<string>? Diagnostic;

    /// <summary>
    /// Replaces the loaded set, keeping what is already on the map for any feed
    /// still present so a reload does not re-place every marker.
    /// </summary>
    public void Load(IEnumerable<ScriptFile> files, DateTimeOffset now)
    {
        var previous = _feeds.ToDictionary(f => f.FileName, f => f, StringComparer.Ordinal);
        _feeds.Clear();

        foreach (var file in files)
        {
            if (!file.Enabled || file.Parse.Sync is not { } sync) continue;

            var loaded = new Loaded
            {
                FileName = file.FileName,
                Sync = sync,
                // Due immediately, unlike a script's every: trigger. A feed
                // mirror has nothing to say until it has read the feed once,
                // and waiting an interval to find that out just delays the
                // first markers.
                NextDue = now,
            };

            if (previous.TryGetValue(file.FileName, out var was))
            {
                foreach (var kv in was.Seen) loaded.Seen[kv.Key] = kv.Value;
            }
            _feeds.Add(loaded);
        }
    }

    /// <summary>Feeds whose interval has elapsed. The caller fetches each and
    /// hands the body back to <see cref="Reconcile"/>.</summary>
    public IReadOnlyList<FeedSyncDue> Due(DateTimeOffset now)
    {
        List<FeedSyncDue>? due = null;
        foreach (var feed in _feeds)
        {
            if (feed.NextDue > now) continue;
            feed.NextDue = now + feed.Sync.Every;
            (due ??= []).Add(new FeedSyncDue(feed.FileName, feed.Sync));
        }
        return (IReadOnlyList<FeedSyncDue>?)due ?? [];
    }

    /// <summary>
    /// Works out what to send after reading a feed: what is new, what changed,
    /// what has gone.
    /// </summary>
    /// <param name="fileName">Which feed the body came from.</param>
    /// <param name="responseJson">The raw response.</param>
    /// <param name="self">This node, for the distance filter and for expanding
    /// {my.*} in a name or description.</param>
    public IReadOnlyList<FeedSyncAction> Reconcile(
        string fileName, string responseJson, ScriptSelf self, DateTimeOffset now)
    {
        var feed = _feeds.FirstOrDefault(f => f.FileName == fileName);
        if (feed is null) return [];
        var sync = feed.Sync;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(responseJson);
        }
        catch (JsonException ex)
        {
            Diagnostic?.Invoke($"{fileName}: the response is not valid JSON — {ex.Message}");
            return [];
        }

        var actions = new List<FeedSyncAction>();
        var present = new HashSet<string>(StringComparer.Ordinal);

        using (document)
        {
            if (!TryItems(document.RootElement, sync.ItemsPath, out var items))
            {
                // With an excerpt: a sync has no equivalent of dropping the
                // json: block from a script to see what actually came back, so
                // the one message about a wrong items: has to carry enough of
                // the response to find the right path from.
                var excerpt = responseJson.Length > 200 ? responseJson[..200] + "…" : responseJson;
                Diagnostic?.Invoke(
                    $"{fileName}: {(sync.ItemsPath.Length == 0 ? "the response" : sync.ItemsPath)} is not a list. " +
                    $"The response starts: {excerpt}");
                return [];
            }

            foreach (var item in items)
            {
                var raw = item.GetRawText();

                var id = JsonValuePath.Read(raw, sync.IdPath, out _);
                if (string.IsNullOrWhiteSpace(id)) continue;

                // A record that is no longer live counts as gone, so it falls
                // out of "present" and is retired below alongside anything that
                // simply stopped being returned.
                if (sync.ActivePath.Length > 0)
                {
                    var active = JsonValuePath.Read(raw, sync.ActivePath, out _);
                    if (!string.Equals(active, "true", StringComparison.OrdinalIgnoreCase)) continue;
                }

                if (!TryCoordinate(raw, sync.LatitudePath, 90, out var lat) ||
                    !TryCoordinate(raw, sync.LongitudePath, 180, out var lon))
                    continue;

                if (sync.WithinMetres is { } limit)
                {
                    if (!self.HasLocation) continue;
                    var metres = ScriptRequirement.HaversineMetres(
                        self.Latitude!.Value, self.Longitude!.Value, lat, lon);
                    if (metres > limit) continue;
                }

                present.Add(id);

                var expansion = new ScriptExpansion(new ScriptEvent { Self = self, At = now }) { Item = raw };
                var name = ScriptTemplate.ClampToPayload(expansion.Expand(sync.Waypoint.Name));
                var description = ScriptTemplate.ClampToPayload(expansion.Expand(sync.Waypoint.Description));
                var fingerprint = Fingerprint(raw, sync.WatchPaths, name, description);

                if (!feed.Seen.TryGetValue(id, out var tracked))
                {
                    tracked = new Tracked
                    {
                        WaypointId = WaypointIdFor(id),
                        Fingerprint = fingerprint,
                        LastSent = now,
                        Latitude = lat,
                        Longitude = lon,
                        Name = name,
                        Description = description,
                    };
                    feed.Seen[id] = tracked;
                    actions.Add(Build(FeedSyncActionKind.Place, id, tracked, sync, now));
                    continue;
                }

                bool changed = !string.Equals(tracked.Fingerprint, fingerprint, StringComparison.Ordinal);
                bool stale = sync.Expires > TimeSpan.Zero && now - tracked.LastSent >= sync.Expires / 2;
                if (!changed && !stale) continue;

                tracked.Fingerprint = fingerprint;
                tracked.LastSent = now;
                tracked.Latitude = lat;
                tracked.Longitude = lon;
                tracked.Name = name;
                tracked.Description = description;
                actions.Add(Build(
                    changed ? FeedSyncActionKind.Update : FeedSyncActionKind.Refresh, id, tracked, sync, now));
            }
        }

        foreach (var id in feed.Seen.Keys.Where(k => !present.Contains(k)).ToList())
        {
            var tracked = feed.Seen[id];
            feed.Seen.Remove(id);
            actions.Add(new FeedSyncAction(
                FeedSyncActionKind.Remove, id, tracked.WaypointId,
                tracked.Latitude, tracked.Longitude, tracked.Name, tracked.Description,
                // An expiry a minute in the past. Firmware only shows a
                // waypoint while expire > now, so this is how one is retired —
                // there is no delete on the wire.
                ExpireEpoch: (uint)now.AddMinutes(-1).ToUnixTimeSeconds()));
        }

        return actions;
    }

    private static FeedSyncAction Build(
        FeedSyncActionKind kind, string id, Tracked tracked, MeshFeedSync sync, DateTimeOffset now) =>
        new(kind, id, tracked.WaypointId, tracked.Latitude, tracked.Longitude,
            tracked.Name, tracked.Description,
            sync.Expires > TimeSpan.Zero
                ? (uint)now.Add(sync.Expires).ToUnixTimeSeconds()
                // Not 0: firmware treats that as already expired and never
                // draws it. Waypoints.WaypointRecord.NeverExpiresEpoch, which
                // the phone clients use too.
                : 2147483647u);

    private static bool TryItems(JsonElement root, string path, out IEnumerable<JsonElement> items)
    {
        items = Array.Empty<JsonElement>();

        var element = root;
        if (path.Length > 0)
        {
            // Reuses the same path reader the rest of the language uses, by
            // walking to the value and requiring it be an array.
            var raw = JsonValuePath.Read(root.GetRawText(), path, out _);
            if (raw is null) return false;
            try { element = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { return false; }
        }

        if (element.ValueKind != JsonValueKind.Array) return false;
        items = element.EnumerateArray().Select(e => e.Clone()).ToList();
        return true;
    }

    private static bool TryCoordinate(string raw, string path, double limit, out double value)
    {
        value = 0;
        var text = JsonValuePath.Read(raw, path, out _);
        return text is not null
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && Math.Abs(value) <= limit;
    }

    /// <summary>
    /// What "changed" means for one record: the watched fields, plus the text
    /// actually being sent.
    /// </summary>
    /// <remarks>
    /// Including the rendered name and description covers the case watch: was
    /// meant for and misses — a template reading a field nobody thought to
    /// list. Excluding everything else is what stops a feed that restamps every
    /// record on every poll from rebroadcasting the lot.
    /// </remarks>
    private static string Fingerprint(string raw, IReadOnlyList<string> watchPaths, string name, string description)
    {
        // Unit separator: a control character no API value will contain, so
        // two different field splits cannot fingerprint the same.
        const char separator = '';

        var sb = new StringBuilder(name).Append(separator).Append(description);
        foreach (var path in watchPaths)
            sb.Append(separator).Append(JsonValuePath.Read(raw, path, out _) ?? string.Empty);
        return sb.ToString();
    }

    /// <summary>
    /// A waypoint id derived from the record's own id, so the same record maps
    /// to the same marker on every run and across restarts — which is what
    /// makes an update replace rather than accumulate.
    /// </summary>
    public static uint WaypointIdFor(string itemId)
    {
        // FNV-1a. Not for security — only for a stable spread across the id
        // space that the same input always lands on.
        const uint offset = 2166136261, prime = 16777619;
        uint hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(itemId))
        {
            hash ^= b;
            hash *= prime;
        }
        // 0 is "unset" to firmware, which would make the marker unaddressable.
        return hash == 0 ? 1u : hash;
    }
}
