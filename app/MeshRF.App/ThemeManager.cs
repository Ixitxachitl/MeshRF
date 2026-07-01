// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Microsoft.Win32;

namespace MeshRF.App;

/// <summary>
/// Swaps the merged theme ResourceDictionary at runtime. Three modes:
/// "Light", "Dark", "System" (follows Windows apps theme).
/// </summary>
public static class ThemeManager
{
    private const string LightUri = "Themes/Light.xaml";
    private const string DarkUri = "Themes/Dark.xaml";

    /// <summary>True when the currently-applied theme resolves to Dark. Lets
    /// non-resource views (e.g. the map) pick theme-appropriate assets.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>The currently active map tile provider theme ("Auto", "Light",
    /// "Light (CARTO)", "Voyager", or "Dark"). Updated by the on-map selector;
    /// read by any open map-containing window to stay in sync.</summary>
    public static string MapTileTheme { get; private set; } = "Auto";

    /// <summary>Raised after a theme is applied, so views that aren't driven by
    /// DynamicResource (e.g. the map's tile source) can refresh.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Raised when the map tile provider changes so that any open
    /// secondary map windows (e.g. location-history popups) can redraw.</summary>
    public static event Action? MapTileThemeChanged;

    /// <summary>Updates the active map tile theme and notifies all subscribers.
    /// Called by <see cref="MapView"/> when the tile-provider selector changes
    /// and on settings restore.</summary>
    public static void SetMapTileTheme(string theme)
    {
        MapTileTheme = theme ?? "Auto";
        MapTileThemeChanged?.Invoke();
    }

    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var resolved = (theme ?? "System").ToLowerInvariant() switch
        {
            "light" => LightUri,
            "dark" => DarkUri,
            _ => SystemPrefersDark() ? DarkUri : LightUri,
        };
        IsDark = resolved == DarkUri;

        // Replace the FIRST entry of MergedDictionaries (where App.xaml puts
        // the active theme). Use a fresh ResourceDictionary so DynamicResource
        // bindings re-resolve.
        var dicts = app.Resources.MergedDictionaries;
        var newDict = new ResourceDictionary { Source = new Uri(resolved, UriKind.Relative) };
        if (dicts.Count == 0) dicts.Add(newDict);
        else dicts[0] = newDict;

        ThemeChanged?.Invoke();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 1 == light, 0 == dark.
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i == 0;
        }
        catch
        {
        }
        return false;
    }
}
