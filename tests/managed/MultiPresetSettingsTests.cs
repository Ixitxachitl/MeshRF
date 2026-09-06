// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

/// <summary>The multi-preset settings survive a round trip through the
/// file, and an older file without them loads with the feature off.</summary>
[Collection("settings-file")]
public sealed class MultiPresetSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public MultiPresetSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MeshRF.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
        AppSettings.PathOverride = _path;
    }

    public void Dispose()
    {
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));
        AppSettings.PathOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void TheFieldsRoundTrip()
    {
        var s = new AppSettings
        {
            MultiPresetEnabled = true,
            MonitorExcludedPresets = { "ShortTurbo", "TinySlow" },
            MonitorCenterOffsetKHz = -3125,
            NodeFilterHeardOn = "LongFast",
        };
        s.Save();
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));

        var back = AppSettings.Load();
        Assert.True(back.MultiPresetEnabled);
        Assert.Equal(new[] { "ShortTurbo", "TinySlow" }, back.MonitorExcludedPresets);
        Assert.Equal(-3125, back.MonitorCenterOffsetKHz);
        Assert.Equal("LongFast", back.NodeFilterHeardOn);
    }

    [Fact]
    public void AnOlderFileLoadsWithTheFeatureOff()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(new { Region = "US", Preset = "MediumFast" }));
        var s = AppSettings.Load();
        Assert.False(s.MultiPresetEnabled);
        Assert.Empty(s.MonitorExcludedPresets);
        Assert.Null(s.MonitorCenterOffsetKHz);
        Assert.Equal("Any", s.NodeFilterHeardOn);
    }
}
