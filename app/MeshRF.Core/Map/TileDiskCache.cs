// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Map;

/// <summary>Keeps a directory of cached tiles under a size ceiling.
///
/// Tiles are cheap to refetch and worthless once the map has moved on, so the
/// cache is bounded by bytes rather than age or count: a rasterised tile is a
/// few kilobytes while an encoded vector source tile is closer to half a
/// megabyte, and a count would treat those as equal.</summary>
public static class TileDiskCache
{
    /// <summary>Deletes least recently used files until the directory fits in
    /// <paramref name="targetBytes"/>, but only once it exceeds
    /// <paramref name="maxBytes"/>. The gap between the two stops a cache
    /// sitting at its ceiling from re-trimming on every write.
    ///
    /// Best-effort: a file that cannot be deleted is counted as kept and the
    /// trim carries on, so one locked tile never stalls the whole sweep.</summary>
    /// <returns>Bytes remaining in the directory.</returns>
    public static long Trim(string directory, long maxBytes, long targetBytes)
    {
        if (maxBytes < 0 || targetBytes < 0 || targetBytes > maxBytes)
            throw new ArgumentOutOfRangeException(nameof(targetBytes),
                "target must be between zero and the ceiling");

        FileInfo[] files;
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return 0;
            files = dir.GetFiles();
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        long total = 0;
        foreach (var f in files) total += f.Length;
        if (total <= maxBytes) return total;

        // Oldest first. Write time doubles as the access time because a tile
        // served from the cache has its timestamp refreshed by MarkUsed, so
        // the areas actually being looked at are the last to be dropped.
        Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

        foreach (var f in files)
        {
            if (total <= targetBytes) break;
            long size = f.Length;
            try
            {
                f.Delete();
                total -= size;
            }
            catch (IOException) { /* in use, or gone already */ }
            catch (UnauthorizedAccessException) { /* not ours to delete */ }
        }

        return total;
    }

    /// <summary>Marks a cached file as used, so <see cref="Trim"/> sees it as
    /// recent. Only a timestamp older than <paramref name="staleAfter"/> is
    /// rewritten, so serving a tile costs no extra write in the common case
    /// while a long-lived area still outlives one visited once.</summary>
    public static void MarkUsed(string file, TimeSpan staleAfter)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (File.GetLastWriteTimeUtc(file) > now - staleAfter) return;
            File.SetLastWriteTimeUtc(file, now);
        }
        catch (IOException) { /* cache is best-effort */ }
        catch (UnauthorizedAccessException) { /* cache is best-effort */ }
    }
}
