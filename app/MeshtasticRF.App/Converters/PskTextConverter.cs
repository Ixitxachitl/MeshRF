// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows.Data;

namespace MeshtasticRF.App.Converters;

/// <summary>
/// Two-way converter between a byte[] PSK and a Meshtastic-style key string:
/// either "default", "none", or a base64 (or hex) representation, matching
/// what the firmware accepts in `meshtastic --set channel.psk`.
/// </summary>
public sealed class PskTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] b) return string.Empty;
        if (b.Length == 0) return "none";
        if (b.Length == 1 && b[0] == 0x00) return "none";
        if (b.Length == 1 && b[0] == 0x01) return "default";
        // The expanded default key persists from older versions — normalize
        // it back to "default" ("AQ==") for display so the user isn't shown
        // the well-known bytes.
        if (b.Length == MeshtasticRF.Channels.ChannelConfig.DefaultPsk.Length &&
            b.AsSpan().SequenceEqual(MeshtasticRF.Channels.ChannelConfig.DefaultPsk))
            return "default";
        return "base64:" + System.Convert.ToBase64String(b);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        s = s.Trim();
        if (string.IsNullOrEmpty(s) || s.Equals("default", StringComparison.OrdinalIgnoreCase))
            return new byte[] { 0x01 };
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<byte>();

        // Strip optional prefixes used by the firmware CLI / mesh URLs.
        if (s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("base64:".Length);
        if (s.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            s = s.Substring("hex:".Length);

        // Try base64 first, then hex. If both fail, leave the existing value
        // unchanged (return Binding.DoNothing).
        try { return System.Convert.FromBase64String(s); } catch { }
        try { return System.Convert.FromHexString(s); } catch { }
        return Binding.DoNothing;
    }
}
