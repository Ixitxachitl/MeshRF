// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The emoji the picker offers: the Unicode emoji set (Assets/emoji-catalog.txt,
/// generated from UTS #51 emoji-test.txt) narrowed to the glyphs the colour
/// emoji font on this machine can actually draw.
///
/// The old picker walked hand-picked code point ranges, which both over- and
/// under-shot: the ranges are not solidly emoji, so unassigned code points and
/// plain symbols rendered as tofu, and every emoji outside those ranges — flags,
/// most of the Unicode 12+ additions — was missing entirely. Asking the font
/// itself is exact and stays correct as the platform font is updated.
/// </summary>
internal static class EmojiCatalog
{
    /// <summary>One offered emoji.</summary>
    /// <param name="Glyph">The string to insert or send.</param>
    /// <param name="Name">Unicode's name, e.g. "grinning face"; used by search.</param>
    /// <param name="Group">Unicode's emoji group, which becomes the tab header.</param>
    /// <param name="IsSingleCodePoint">
    /// True when the glyph is one scalar (optionally plus U+FE0F). Meshtastic
    /// waypoint icons are a single uint32 code point, so that picker can only
    /// offer these.
    /// </param>
    internal sealed record Entry(string Glyph, string Name, string Group, bool IsSingleCodePoint);

    /// <summary>What one font resolved to: the family actually used, and its emoji.</summary>
    internal sealed record Snapshot(string FontFamily, IReadOnlyList<Entry> Entries);

    private const string ResourceName = "emoji-catalog.txt";

    // U+1F600 GRINNING FACE. Present in every colour emoji font and in no text
    // font, so it tells us whether the platform emoji font is actually installed.
    private const int EmojiProbeCodePoint = 0x1F600;

    /// <summary>
    /// This platform's colour emoji font. Named per-OS rather than offered as a
    /// cross-platform fallback chain because fontconfig never fails a lookup: on
    /// Linux, asking for "Segoe UI Emoji" returns whatever it considers the
    /// closest match (Noto Color Emoji once that is installed, DejaVu Sans
    /// before). A chain listing all three therefore resolves every link to a
    /// real font on Linux, and text lands in an emoji font that has no Latin
    /// glyphs. Program.cs registers this as the font-manager fallback and the
    /// picker probes it; nothing else should name an emoji font.
    /// </summary>
    internal static string PlatformFamily =>
        OperatingSystem.IsWindows() ? "Segoe UI Emoji"
        : OperatingSystem.IsMacOS() ? "Apple Color Emoji"
        : "Noto Color Emoji";

    private static readonly Lazy<IReadOnlyList<(int[] CodePoints, string Name, string Group)>> Unfiltered =
        new(LoadResource);

    private static readonly Dictionary<string, Snapshot> Cache = [];

    /// <summary>
    /// Emoji drawable in <see cref="PlatformFamily"/>. Cached; the first call
    /// shapes the whole catalogue.
    /// </summary>
    internal static Snapshot For()
    {
        string key = PlatformFamily;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var built = Build(key);
            Cache[key] = built;
            return built;
        }
    }

    private static Snapshot Build(string fontFamily)
    {
        var (family, font) = ResolveEmojiFont(fontFamily);
        if (font is null) return new Snapshot(family, []);
        double cell = MeasureCell(font);

        var entries = new List<Entry>();
        foreach (var (codePoints, name, group) in Unfiltered.Value)
        {
            string glyph = ToGlyph(codePoints);
            bool single = codePoints.Length == 1
                          || (codePoints.Length == 2 && codePoints[1] == 0xFE0F);
            if (!CanRender(font, codePoints, glyph, single, cell)) continue;
            entries.Add(new Entry(glyph, name, group, single));
        }
        return new Snapshot(family, entries);
    }

    /// <summary>
    /// <paramref name="family"/>'s typeface, or null when this machine has no
    /// colour emoji font — a Linux box without fonts-noto-color-emoji gets a
    /// short honest list rather than a grid of tofu. The U+1F600 probe is what
    /// distinguishes "installed" from "substituted": every platform resolves an
    /// absent family to *something*, so a successful lookup proves nothing on
    /// its own.
    /// </summary>
    private static (string Family, GlyphTypeface? Font) ResolveEmojiFont(string family)
    {
        if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var candidate)
            && candidate.CharacterToGlyphMap.ContainsGlyph(EmojiProbeCodePoint))
            return (family, candidate);
        return (family, null);
    }

    /// <summary>
    /// Whether <paramref name="font"/> draws this emoji. A lone scalar is a
    /// straight cmap lookup.
    ///
    /// Sequences (flags, keycaps, ZWJ) have to be shaped, and "one glyph out"
    /// is the wrong test: Segoe UI Emoji composes 🇺🇸 from two half-width
    /// letter tiles and 👨‍👩‍👧 from three overlapping part-glyphs, all of
    /// which are correct renderings. What separates those from an unsupported
    /// sequence is width — when the font has no substitution for the run it
    /// leaves the joiner in place and the parts render side by side, taking two
    /// or more cells. So the test is that the whole run still occupies a single
    /// emoji cell.
    /// </summary>
    private static bool CanRender(GlyphTypeface font, int[] codePoints, string glyph, bool single, double cell)
    {
        if (single) return font.CharacterToGlyphMap.ContainsGlyph(codePoints[0]);
        if (cell <= 0) return false;

        double width = ShapedWidth(font, glyph);
        // Tolerance only absorbs rounding; an unsupported sequence comes back
        // at a multiple of the cell, not a few hundredths over.
        return width > 0 && width <= cell * 1.05;
    }

    /// <summary>
    /// Total advance of <paramref name="glyph"/>, or 0 when the font can't draw
    /// some part of it (a .notdef anywhere) or there is no shaper at all.
    /// </summary>
    private static double ShapedWidth(GlyphTypeface font, string glyph)
    {
        try
        {
            using var shaped = TextShaper.Current.ShapeText(
                glyph, new TextShaperOptions(font, culture: CultureInfo.InvariantCulture));
            if (shaped.Length == 0) return 0;

            double width = 0;
            for (int i = 0; i < shaped.Length; i++)
            {
                if (shaped[i].GlyphIndex == 0) return 0;
                width += shaped[i].GlyphAdvance;
            }
            return width;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Width of one emoji cell in this font, measured from U+1F600 rather than
    /// assumed: colour emoji glyphs are wider than the em square, and by how
    /// much varies by font.
    /// </summary>
    private static double MeasureCell(GlyphTypeface font) =>
        font.CharacterToGlyphMap.ContainsGlyph(EmojiProbeCodePoint)
            ? ShapedWidth(font, char.ConvertFromUtf32(EmojiProbeCodePoint))
            : 0;

    private static string ToGlyph(int[] codePoints)
    {
        var sb = new System.Text.StringBuilder(codePoints.Length * 2);
        foreach (int cp in codePoints) sb.Append(char.ConvertFromUtf32(cp));
        return sb.ToString();
    }

    /// <summary>
    /// Reads the generated catalog: "#Group" header lines, then one
    /// "HEX HEX...\tname" line per emoji, in Unicode's CLDR display order.
    /// </summary>
    private static IReadOnlyList<(int[], string, string)> LoadResource()
    {
        var list = new List<(int[], string, string)>();
        using var stream = typeof(EmojiCatalog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return list;

        using var reader = new StreamReader(stream);
        string group = string.Empty;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            if (line[0] == '#') { group = line[1..]; continue; }

            int tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var fields = line[..tab].Split(' ');
            var codePoints = new int[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                codePoints[i] = int.Parse(fields[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            list.Add((codePoints, line[(tab + 1)..], group));
        }
        return list;
    }
}
