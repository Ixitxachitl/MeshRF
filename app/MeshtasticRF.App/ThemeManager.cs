// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Microsoft.Win32;

namespace MeshtasticRF.App;

/// <summary>
/// Swaps the merged theme ResourceDictionary at runtime. Three modes:
/// "Light", "Dark", "System" (follows Windows apps theme).
/// </summary>
public static class ThemeManager
{
    private const string LightUri = "Themes/Light.xaml";
    private const string DarkUri = "Themes/Dark.xaml";

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

        // Replace the FIRST entry of MergedDictionaries (where App.xaml puts
        // the active theme). Use a fresh ResourceDictionary so DynamicResource
        // bindings re-resolve.
        var dicts = app.Resources.MergedDictionaries;
        var newDict = new ResourceDictionary { Source = new Uri(resolved, UriKind.Relative) };
        if (dicts.Count == 0) dicts.Add(newDict);
        else dicts[0] = newDict;
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
