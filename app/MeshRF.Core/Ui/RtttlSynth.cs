// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// Portable RTTTL → 16-bit mono PCM/WAV synthesis. Only playback is
/// platform-specific; everything here is pure computation.
/// </summary>
public static class RtttlSynth
{
    public const int SampleRate = 44100;
    private const int LoopGapMs = 400;

    private static readonly double[] Octave4 =
    {
        261.63, 277.18, 293.66, 311.13, 329.63, 349.23,
        369.99, 392.00, 415.30, 440.00, 466.16, 493.88,
    };

    /// <summary>Render <paramref name="rtttl"/> to a complete WAV file, honouring
    /// <paramref name="mode"/>'s looping. Returns null for Off/blank/invalid
    /// input or zero volume, so callers can treat it as "nothing to play".</summary>
    public static byte[]? BuildWav(string? rtttl, RingtoneMode mode, double volume)
    {
        if (mode == RingtoneMode.Off || string.IsNullOrWhiteSpace(rtttl)) return null;
        volume = Math.Clamp(volume, 0.0, 1.0);
        if (volume <= 0.0) return null;

        short[] tune;
        try { tune = Render(rtttl!, volume); }
        catch { return null; }
        if (tune.Length == 0) return null;

        short[] samples = mode switch
        {
            RingtoneMode.Seconds5 => LoopTo(tune, 5),
            RingtoneMode.Seconds10 => LoopTo(tune, 10),
            RingtoneMode.Seconds30 => LoopTo(tune, 30),
            _ => tune,
        };
        return BuildWavBytes(samples);
    }

    private static short[] Render(string rtttl, double volume)
    {
        var sections = rtttl.Split(':');
        if (sections.Length != 3) return Array.Empty<short>();

        int defaultDuration = 4, defaultOctave = 6, bpm = 63;
        foreach (var kv in sections[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = kv.Split('=');
            if (pair.Length != 2) continue;
            var key = pair[0].Trim().ToLowerInvariant();
            if (!int.TryParse(pair[1].Trim(), out int val)) continue;
            switch (key)
            {
                case "d": if (val > 0) defaultDuration = val; break;
                case "o": defaultOctave = val; break;
                case "b": if (val > 0) bpm = val; break;
            }
        }

        double wholeNoteMs = 60_000.0 / bpm * 4.0;
        var buffer = new List<short>(SampleRate);

        foreach (var raw in sections[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0) continue;

            int i = 0;
            int duration = 0;
            while (i < token.Length && char.IsDigit(token[i]))
            {
                duration = duration * 10 + (token[i] - '0');
                i++;
            }
            if (duration == 0) duration = defaultDuration;

            char note = i < token.Length ? token[i] : 'p';
            i++;

            bool sharp = i < token.Length && token[i] == '#';
            if (sharp) i++;

            bool dotted = i < token.Length && token[i] == '.';
            if (dotted) i++;

            int octave = defaultOctave;
            if (i < token.Length && char.IsDigit(token[i]))
            {
                octave = token[i] - '0';
                i++;
            }
            if (!dotted && i < token.Length && token[i] == '.') dotted = true;

            double noteMs = wholeNoteMs / duration;
            if (dotted) noteMs *= 1.5;

            Synthesize(buffer, NoteFrequency(note, sharp, octave), noteMs, volume);
        }

        return buffer.ToArray();
    }

    private static double NoteFrequency(char note, bool sharp, int octave)
    {
        int semitone = note switch
        {
            'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5,
            'g' => 7, 'a' => 9, 'b' => 11,
            _ => -1,
        };
        if (semitone < 0) return 0.0;
        if (sharp) semitone++;
        return Octave4[semitone % 12] * Math.Pow(2.0, octave - 4);
    }

    private static void Synthesize(List<short> buffer, double freq, double ms, double volume)
    {
        int count = (int)(SampleRate * ms / 1000.0);
        if (count <= 0) return;

        if (freq <= 0.0)
        {
            for (int n = 0; n < count; n++) buffer.Add(0);
            return;
        }

        int gap = Math.Min(count / 5, SampleRate / 125);
        int audible = Math.Max(1, count - gap);
        double amp = volume * 0.55 * short.MaxValue;
        int ramp = Math.Min(audible / 8, SampleRate / 500);
        double step = 2.0 * Math.PI * freq / SampleRate;

        for (int n = 0; n < audible; n++)
        {
            double env = 1.0;
            if (ramp > 0 && n < ramp) env = (double)n / ramp;
            else if (ramp > 0 && n > audible - ramp) env = (double)(audible - n) / ramp;
            buffer.Add((short)(Math.Sin(step * n) * amp * env));
        }
        for (int n = audible; n < count; n++) buffer.Add(0);
    }

    private static short[] LoopTo(short[] tune, int targetSeconds)
    {
        int target = SampleRate * targetSeconds;
        int gap = SampleRate * LoopGapMs / 1000;
        var outBuf = new short[target];

        int pos = 0;
        while (pos < target)
        {
            int copy = Math.Min(tune.Length, target - pos);
            Array.Copy(tune, 0, outBuf, pos, copy);
            pos += copy;
            if (pos >= target) break;
            pos += Math.Min(gap, target - pos);
        }
        return outBuf;
    }

    private static byte[] BuildWavBytes(short[] samples)
    {
        const int channels = 1, bits = 16;
        int dataBytes = samples.Length * 2;
        int byteRate = SampleRate * channels * bits / 8;

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bits / 8));
        w.Write((short)bits);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
