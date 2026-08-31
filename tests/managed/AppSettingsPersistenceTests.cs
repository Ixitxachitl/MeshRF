// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using MeshRF;
using Xunit;

namespace MeshRF.Tests;

/// <summary>
/// settings.json holds the window layout, the radio setup and every stored
/// secret, and every failure inside <see cref="AppSettings.Save"/> is
/// swallowed — so the only thing that proves a save lands at all, or that a
/// file left half-written by a crash is recoverable, is reading the file back.
/// </summary>
public sealed class AppSettingsPersistenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AppSettingsPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "MeshRF.Tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
        AppSettings.PathOverride = _path;
    }

    public void Dispose()
    {
        // Before clearing the override, or a still-queued write would land on
        // the real settings file.
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));
        AppSettings.PathOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    private void SaveAndFlush(AppSettings settings)
    {
        settings.Save();
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Each_Alert_Tone_Keeps_Its_Own_Duration()
    {
        // The three durations used to be one setting. Silencing crossings has
        // to leave messages audible, which only holds if all three round-trip
        // independently.
        var settings = new AppSettings
        {
            RingtoneMode = "30 seconds",
            GeofenceRingtoneMode = "Off",
            AlertBellRingtoneMode = "10 seconds",
        };

        SaveAndFlush(settings);

        var loaded = AppSettings.Load();
        Assert.Equal("30 seconds", loaded.RingtoneMode);
        Assert.Equal("Off", loaded.GeofenceRingtoneMode);
        Assert.Equal("10 seconds", loaded.AlertBellRingtoneMode);
    }

    [Fact]
    public void The_Alert_Bell_Tune_Round_Trips()
    {
        SaveAndFlush(new AppSettings { AlertBellRtttl = "x:d=8,o=5,b=120:c" });

        Assert.Equal("x:d=8,o=5,b=120:c", AppSettings.Load().AlertBellRtttl);
    }

    [Fact]
    public void A_Settings_File_Predating_The_Split_Keeps_Its_Message_Tone()
    {
        // Upgrading must not silently reset what the operator had chosen: the
        // message tone kept its key, so an old file still supplies it, and the
        // two new durations come up at their defaults rather than Off.
        File.WriteAllText(_path, "{\"RingtoneMode\":\"30 seconds\"}");

        var loaded = AppSettings.Load();

        Assert.Equal("30 seconds", loaded.RingtoneMode);
        Assert.Equal("Play once", loaded.GeofenceRingtoneMode);
        Assert.Equal("Play once", loaded.AlertBellRingtoneMode);
    }

    [Fact]
    public void SaveWritesAFileThatLoadsBack()
    {
        SaveAndFlush(new AppSettings { CenterFreqMHz = 913.125, MainLeftPaneStar = 2.5 });

        Assert.True(File.Exists(_path));
        var loaded = AppSettings.Load();
        Assert.Equal(913.125, loaded.CenterFreqMHz);
        Assert.Equal(2.5, loaded.MainLeftPaneStar);
    }

    [Fact]
    public void SaveLeavesNoTemporaryFileBehind()
    {
        SaveAndFlush(new AppSettings { CenterFreqMHz = 906.875 });

        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public void SecondSaveKeepsThePreviousFileAsTheBackup()
    {
        SaveAndFlush(new AppSettings { CenterFreqMHz = 906.875 });
        SaveAndFlush(new AppSettings { CenterFreqMHz = 913.125 });

        var backup = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path + ".bak"));
        Assert.NotNull(backup);
        Assert.Equal(906.875, backup!.CenterFreqMHz);
    }

    [Fact]
    public void ATruncatedSettingsFileIsRecoveredFromTheBackup()
    {
        SaveAndFlush(new AppSettings { CenterFreqMHz = 906.875, MainLeftPaneStar = 3 });
        SaveAndFlush(new AppSettings { CenterFreqMHz = 913.125, MainLeftPaneStar = 4 });

        // What a machine dying mid-write leaves behind.
        File.WriteAllText(_path, File.ReadAllText(_path)[..40]);

        var loaded = AppSettings.Load();
        Assert.Equal(906.875, loaded.CenterFreqMHz);
        Assert.Equal(3, loaded.MainLeftPaneStar);
        Assert.NotNull(AppSettings.LastLoadWarning);

        // And the recovered copy is put back, so the next save is not written
        // on top of a file that will not parse.
        Assert.Equal(906.875, AppSettings.Load().CenterFreqMHz);
    }

    [Fact]
    public void AnEmptySettingsFileWithNoBackupFallsBackToDefaults()
    {
        File.WriteAllText(_path, string.Empty);

        var loaded = AppSettings.Load();
        Assert.Equal(new AppSettings().CenterFreqMHz, loaded.CenterFreqMHz);
        Assert.NotNull(AppSettings.LastLoadWarning);
    }

    [Fact]
    public void OverlappingSavesEndWithTheNewestOneOnDisk()
    {
        for (int i = 0; i < 50; i++)
            new AppSettings { CenterFreqMHz = 900 + i, HopLimit = (byte)(i % 8) }.Save();

        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(10));

        // Saves collapse onto the newest, so the file holds the last one asked
        // for — never a mix of two, and never nothing at all.
        var loaded = AppSettings.Load();
        Assert.Equal(949, loaded.CenterFreqMHz);
        Assert.Equal(49 % 8, loaded.HopLimit);
    }

    [Fact]
    public void MissingSettingsFileIsNotReportedAsAFault()
    {
        var loaded = AppSettings.Load();

        Assert.Equal(new AppSettings().CenterFreqMHz, loaded.CenterFreqMHz);
        Assert.Null(AppSettings.LastLoadWarning);
    }

    [Fact]
    public void AGoodLoadClearsAnEarlierWarning()
    {
        File.WriteAllText(_path, "{ not json");
        AppSettings.Load();
        Assert.NotNull(AppSettings.LastLoadWarning);

        SaveAndFlush(new AppSettings { CenterFreqMHz = 913.125 });

        Assert.Equal(913.125, AppSettings.Load().CenterFreqMHz);
        Assert.Null(AppSettings.LastLoadWarning);
    }
}
