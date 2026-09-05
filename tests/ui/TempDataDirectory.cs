// SPDX-License-Identifier: GPL-3.0-or-later
using MeshRF;

namespace MeshRF.UiTests;

/// <summary>
/// Runs a test body against a data directory of its own.
///
/// Anything that builds a view model opens the node, message and channel
/// databases and writes the window layout back to settings.json — all of it in
/// the developer's own profile. Without this, a headless run rewrites the
/// layout of the app they are running and leaves a freshly minted identity in
/// their node database.
/// </summary>
public static class TempDataDirectory
{
    public static void With(Action body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MeshRF-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string? previous = AppData.DirectoryOverride;
        AppData.DirectoryOverride = dir;
        try
        {
            body();
        }
        finally
        {
            AppData.DirectoryOverride = previous;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
