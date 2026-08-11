// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Persisted settings for the Avalonia app, as JSON under
/// %APPDATA%/MeshRF/avalonia-settings.json (Linux: $HOME/.config/MeshRF/...).
/// Deliberately a separate file from MeshRF.App's settings.json: that file's
/// schema carries many WPF-only fields (theme, waterfall, MQTT, gains) this
/// app doesn't understand yet, and a naive save here would silently drop
/// them if the two apps shared one file. Revisit once the Avalonia app's
/// settings surface actually overlaps with the WPF one.
/// </summary>
public sealed class AvaloniaAppSettings
{
    public string RxDeviceKind { get; set; } = "Auto";
    public string Preset { get; set; } = "LongFast";
    public double CenterFreqMHz { get; set; } = 906.875;

    public static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MeshRF");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "avalonia-settings.json");
        }
    }

    public static AvaloniaAppSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return new AvaloniaAppSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AvaloniaAppSettings>(json) ?? new AvaloniaAppSettings();
        }
        catch
        {
            return new AvaloniaAppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort: a failed save shouldn't crash the app.
        }
    }
}
