// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// The one directory the app's persistent state lives in:
/// <c>%APPDATA%\MeshRF</c>. Every store resolves its file through
/// <see cref="Root"/>, so the whole set moves together.
/// </summary>
/// <remarks>
/// That is what <see cref="DirectoryOverride"/> is for: a test that builds a
/// view model gets the real stores with it, and those stores open their
/// database on construction — so without a way to move the directory, a
/// headless run reads and writes the databases of the app the developer is
/// running. Setting it before the first store is constructed puts settings,
/// nodes, channels, messages, scripts and the survey log in a directory of the
/// test's own. The app never sets it; left null, everything lands where it
/// always did.
///
/// Download caches (map tiles, terrain, buildings) deliberately do not go
/// through here. A tile fetched under a test is a valid tile, so there is
/// nothing to isolate, and pointing them at an empty directory would make a
/// headless run re-fetch what the profile already holds.
/// </remarks>
public static class AppData
{
    /// <summary>Redirects <see cref="Root"/>. Set it before constructing
    /// anything that persists; a store already open keeps the file it
    /// opened.</summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>The data directory, created if it is not there yet.</summary>
    public static string Root
    {
        get
        {
            var dir = DirectoryOverride is { Length: > 0 } custom
                ? custom
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MeshRF");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>A file in the data directory.</summary>
    public static string PathFor(string fileName) => Path.Combine(Root, fileName);

    /// <summary>A subdirectory of the data directory, created if needed.</summary>
    public static string SubdirectoryFor(string name)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
