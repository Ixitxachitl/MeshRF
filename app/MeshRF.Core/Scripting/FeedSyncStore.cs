// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace MeshRF.Scripting;

/// <summary>What a feed sync last sent for one record, in a form that survives
/// a restart. Short names because there is one of these per marker on the
/// map.</summary>
/// <param name="W">Waypoint id the marker was sent under.</param>
/// <param name="F">Fingerprint of the fields being watched.</param>
/// <param name="T">When it was last sent, unix seconds.</param>
/// <param name="Lat">Where it was placed.</param>
/// <param name="Lon">Where it was placed.</param>
/// <param name="N">Name as sent.</param>
/// <param name="D">Description as sent.</param>
public sealed record FeedSyncMemory(
    uint W, string F, long T, double Lat, double Lon, string N, string D);

/// <summary>
/// Remembers what each feed sync has already put on the map, across restarts.
/// </summary>
/// <remarks>
/// <para>Without this the memory is in-process only, so every start re-sends
/// every marker a feed is mirroring. That is harmless on the receiving end —
/// the waypoint id is derived from the record id, so a resend replaces rather
/// than duplicates — but it is not harmless on the air, which is the resource
/// the whole scripting layer is careful with. A busy fire season is dozens of
/// markers, re-broadcast every time the app opens.</para>
/// <para>A separate file rather than a corner of settings.json: it is written
/// on a poll timer rather than when the user changes something, and losing it
/// costs one round of re-sends rather than a configuration.</para>
/// </remarks>
public sealed class FeedSyncStore
{
    private readonly string _path;

    /// <summary>Feed file name to record id to what was sent. Sections for
    /// feeds that are not currently loaded are kept as they were read, so
    /// turning a sync off and on again does not re-place its markers.</summary>
    private Dictionary<string, Dictionary<string, FeedSyncMemory>> _state = new(StringComparer.Ordinal);

    public FeedSyncStore(string? path = null) => _path = path ?? DefaultPath;

    public static string DefaultPath => AppData.PathFor("feed-sync.json");

    /// <summary>Reads the file. A missing or unreadable one leaves the memory
    /// empty, which costs one round of re-sends rather than failing to start.</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _state = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, FeedSyncMemory>>>(
                         File.ReadAllText(_path)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _state = new(StringComparer.Ordinal);
        }
    }

    /// <summary>What one feed had placed when it was last saved.</summary>
    public IReadOnlyDictionary<string, FeedSyncMemory> For(string fileName) =>
        _state.TryGetValue(fileName, out var section) ? section : new Dictionary<string, FeedSyncMemory>();

    /// <summary>Records what a feed has on the map now, replacing that feed's
    /// section and leaving every other one alone.</summary>
    public void Save(string fileName, IReadOnlyDictionary<string, FeedSyncMemory> records)
    {
        _state[fileName] = new Dictionary<string, FeedSyncMemory>(records, StringComparer.Ordinal);
        Write();
    }

    private void Write()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_state, s_options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing a write costs one round of re-sends next start, which is
            // not worth interrupting a poll for.
        }
    }

    private static readonly JsonSerializerOptions s_options = new() { WriteIndented = true };
}
