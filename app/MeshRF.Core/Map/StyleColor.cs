// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace MeshRF.Map;

/// <summary>A style colour, held as straight (non-premultiplied) components in
/// 0..1 so interpolation between two stops stays linear in each channel.</summary>
public readonly record struct StyleColor(double R, double G, double B, double A)
{
    public static readonly StyleColor Transparent = new(0, 0, 0, 0);
    public static readonly StyleColor Black = new(0, 0, 0, 1);

    public byte R8 => Clamp8(R);
    public byte G8 => Clamp8(G);
    public byte B8 => Clamp8(B);
    public byte A8 => Clamp8(A);

    private static byte Clamp8(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);

    public static StyleColor Lerp(StyleColor a, StyleColor b, double t) => new(
        a.R + (b.R - a.R) * t,
        a.G + (b.G - a.G) * t,
        a.B + (b.B - a.B) * t,
        a.A + (b.A - a.A) * t);

    /// <summary>Parses the CSS colour forms a MapLibre style may use: #rgb,
    /// #rgba, #rrggbb, #rrggbbaa, rgb()/rgba() and hsl()/hsla().</summary>
    public static bool TryParse(string? text, out StyleColor color)
    {
        color = Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        if (s[0] == '#') return TryParseHex(s.AsSpan(1), out color);

        int open = s.IndexOf('(');
        if (open < 0 || !s.EndsWith(')')) return TryParseNamed(s, out color);

        var fn = s[..open].Trim().ToLowerInvariant();
        var args = s[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        return fn switch
        {
            "rgb" or "rgba" => TryParseRgb(args, out color),
            "hsl" or "hsla" => TryParseHsl(args, out color),
            _ => false,
        };
    }

    public static StyleColor Parse(string? text, StyleColor fallback) =>
        TryParse(text, out var c) ? c : fallback;

    private static bool TryParseHex(ReadOnlySpan<char> hex, out StyleColor color)
    {
        color = Transparent;
        Span<int> v = stackalloc int[8];
        if (hex.Length is not (3 or 4 or 6 or 8)) return false;
        for (int i = 0; i < hex.Length; i++)
        {
            int d = HexDigit(hex[i]);
            if (d < 0) return false;
            v[i] = d;
        }

        // The short forms repeat each digit: #abc is #aabbcc.
        if (hex.Length is 3 or 4)
        {
            double a3 = hex.Length == 4 ? (v[3] * 16 + v[3]) / 255.0 : 1.0;
            color = new StyleColor((v[0] * 16 + v[0]) / 255.0, (v[1] * 16 + v[1]) / 255.0,
                                   (v[2] * 16 + v[2]) / 255.0, a3);
            return true;
        }

        double a = hex.Length == 8 ? (v[6] * 16 + v[7]) / 255.0 : 1.0;
        color = new StyleColor((v[0] * 16 + v[1]) / 255.0, (v[2] * 16 + v[3]) / 255.0,
                               (v[4] * 16 + v[5]) / 255.0, a);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool TryParseRgb(string[] args, out StyleColor color)
    {
        color = Transparent;
        if (args.Length is not (3 or 4)) return false;
        if (!TryChannel(args[0], out var r) || !TryChannel(args[1], out var g) ||
            !TryChannel(args[2], out var b)) return false;
        double a = 1.0;
        if (args.Length == 4 && !TryNumber(args[3], out a)) return false;
        color = new StyleColor(r, g, b, Math.Clamp(a, 0, 1));
        return true;

        // A channel is 0..255, or a percentage.
        static bool TryChannel(string s, out double value)
        {
            if (s.EndsWith('%'))
            {
                if (!TryNumber(s[..^1], out value)) return false;
                value = Math.Clamp(value / 100.0, 0, 1);
                return true;
            }
            if (!TryNumber(s, out value)) return false;
            value = Math.Clamp(value / 255.0, 0, 1);
            return true;
        }
    }

    private static bool TryParseHsl(string[] args, out StyleColor color)
    {
        color = Transparent;
        if (args.Length is not (3 or 4)) return false;
        if (!TryNumber(args[0], out var h)) return false;
        if (!TryPercent(args[1], out var s) || !TryPercent(args[2], out var l)) return false;
        double a = 1.0;
        if (args.Length == 4 && !TryNumber(args[3], out a)) return false;

        h = ((h % 360.0) + 360.0) % 360.0 / 360.0;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        color = new StyleColor(HueToRgb(p, q, h + 1.0 / 3.0), HueToRgb(p, q, h),
                               HueToRgb(p, q, h - 1.0 / 3.0), Math.Clamp(a, 0, 1));
        return true;

        static bool TryPercent(string s, out double value)
        {
            var t = s.EndsWith('%') ? s[..^1] : s;
            if (!TryNumber(t, out value)) return false;
            value = Math.Clamp(s.EndsWith('%') ? value / 100.0 : value, 0, 1);
            return true;
        }
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    private static bool TryNumber(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>The handful of CSS keywords a hand-edited style is likely to
    /// carry. Anything else fails rather than guessing.</summary>
    private static bool TryParseNamed(string s, out StyleColor color)
    {
        color = s.ToLowerInvariant() switch
        {
            "transparent" => Transparent,
            "black" => new StyleColor(0, 0, 0, 1),
            "white" => new StyleColor(1, 1, 1, 1),
            "red" => new StyleColor(1, 0, 0, 1),
            "green" => new StyleColor(0, 128 / 255.0, 0, 1),
            "blue" => new StyleColor(0, 0, 1, 1),
            "gray" or "grey" => new StyleColor(128 / 255.0, 128 / 255.0, 128 / 255.0, 1),
            _ => default,
        };
        return color != default || string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase);
    }
}
