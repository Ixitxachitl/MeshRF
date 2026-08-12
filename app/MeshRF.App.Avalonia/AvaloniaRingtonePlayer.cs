// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Cross-platform ringtone player. The RTTTL → WAV synthesis is shared
/// (<see cref="RtttlSynth"/>); only playback differs per OS, and .NET has no
/// built-in cross-platform audio output.
///
/// Every platform goes through the same path — write the WAV to a temp file and
/// hand it to a system player — rather than using System.Media.SoundPlayer on
/// Windows, which lives in a Windows-only package and would need conditional
/// compilation to keep the Linux build clean.
/// </summary>
public sealed class AvaloniaRingtonePlayer : IRingtonePlayer
{
    private readonly object _gate = new();
    private Process? _process;
    private string? _tempFile;
    private bool _disposed;

    public void Play(string? rtttl, MeshRF.RingtoneMode mode, double volume)
    {
        if (_disposed) return;
        var wav = RtttlSynth.BuildWav(rtttl, mode, volume);
        if (wav is null) return;

        Stop();
        try { PlayFile(wav); }
        catch
        {
            // No audio device / no player installed — a missing notification
            // sound must never take the app down.
        }
    }

    private void PlayFile(byte[] wav)
    {
        var path = Path.Combine(Path.GetTempPath(), $"meshrf-ringtone-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, wav);

        foreach (var (exe, args) in PlayerCandidates(path))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                });
                if (proc is null) continue;
                lock (_gate) { _process = proc; _tempFile = path; }
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => TryDelete(path);
                return;
            }
            catch
            {
                // Not installed — try the next candidate.
            }
        }
        TryDelete(path); // nothing on this box could play it
    }

    /// <summary>Player commands to try, in order, for the current OS.</summary>
    private static IEnumerable<(string Exe, string Args)> PlayerCandidates(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("powershell",
                $"-NoProfile -WindowStyle Hidden -Command \"(New-Object Media.SoundPlayer '{path}').PlaySync()\"");
            yield break;
        }
        if (OperatingSystem.IsMacOS())
        {
            yield return ("afplay", $"\"{path}\"");
            yield break;
        }
        // Linux/BSD: PulseAudio, then ALSA, then SoX.
        yield return ("paplay", $"\"{path}\"");
        yield return ("aplay", $"-q \"{path}\"");
        yield return ("play", $"-q \"{path}\"");
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
            _process?.Dispose();
            _process = null;
            if (_tempFile is not null) { TryDelete(_tempFile); _tempFile = null; }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
