// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>How long an incoming-message ringtone plays.</summary>
public enum RingtoneMode
{
    /// <summary>Ringtone disabled; nothing plays.</summary>
    Off,
    /// <summary>Play the tune through exactly once.</summary>
    PlayOnce,
    /// <summary>Loop the tune for up to 5 seconds.</summary>
    Seconds5,
    /// <summary>Loop the tune for up to 10 seconds.</summary>
    Seconds10,
    /// <summary>Loop the tune for up to 30 seconds.</summary>
    Seconds30,
}

/// <summary>
/// Plays an RTTTL (Ring Tone Text Transfer Language) tune as an incoming-message
/// notification. RTTTL is the same ringtone format Meshtastic uses for its
/// external-notification buzzer, so a tune copied from a Meshtastic device
/// plays here too.
/// </summary>
public interface IRingtonePlayer : IDisposable
{
    /// <summary>The stock Meshtastic external-notification ringtone.</summary>
    const string MeshtasticDefault =
        "24:d=32,o=5,b=565:f6,p,f6,4p,p,f6,p,f6,2p,p,b6,p,b6,p,b6,p,b6,p,b,p,b,p,b,p,b,p,b,p,b,p,b,p,b,1p.,2p.,p";

    /// <summary>
    /// Render and play <paramref name="rtttl"/> according to <paramref name="mode"/>
    /// and <paramref name="volume"/> (0..1). A previous play is stopped first.
    /// Invalid RTTTL or <see cref="RingtoneMode.Off"/> is a silent no-op.
    /// </summary>
    void Play(string? rtttl, RingtoneMode mode, double volume);

    /// <summary>Stop any in-progress playback.</summary>
    void Stop();
}

/// <summary>
/// Silent stand-in for platforms without a ringtone backend wired up yet.
/// TODO: cross-platform apps (Avalonia) currently get this instead of real
/// audio output — porting <c>RtttlPlayer</c>'s WAV synthesis off
/// System.Media.SoundPlayer (Windows-only) to a cross-platform audio API is
/// tracked separately from the RX/message pipeline work.
/// </summary>
public sealed class NullRingtonePlayer : IRingtonePlayer
{
    public static readonly NullRingtonePlayer Instance = new();
    private NullRingtonePlayer() { }
    public void Play(string? rtttl, RingtoneMode mode, double volume) { }
    public void Stop() { }
    public void Dispose() { }
}
