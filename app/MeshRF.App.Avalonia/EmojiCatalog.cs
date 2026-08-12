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
    // font, so it tells us which link of the FontFamily fallback chain is the
    // emoji font without hardcoding platform font names a second time.
    private const int EmojiProbeCodePoint = 0x1F600;

    private static readonly Lazy<IReadOnlyList<(int[] CodePoints, string Name, string Group)>> Unfiltered =
        new(LoadResource);

    private static readonly Dictionary<string, Snapshot> Cache = [];

    /// <summary>
    /// Emoji drawable in <paramref name="fontFamily"/>, which should be the
    /// font chain the picker will render with (App.axaml puts the platform
    /// colour emoji fonts after Inter). Results are cached per chain.
    /// </summary>
    internal static Snapshot For(FontFamily fontFamily)
    {
        // ToString keeps the whole fallback chain; Name is only its first link.
        string key = fontFamily.ToString();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var built = Build(fontFamily);
            Cache[key] = built;
            return built;
        }
    }

    private static Snapshot Build(FontFamily fontFamily)
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
    /// First family in the chain that carries colour emoji, plus its typeface.
    /// Falls back to whatever font the text would actually land in, so an
    /// emoji-font-less machine gets a short honest list rather than tofu.
    /// </summary>
    private static (string Family, GlyphTypeface? Font) ResolveEmojiFont(FontFamily chain)
    {
        foreach (var name in chain.FamilyNames)
        {
            // A family that isn't installed resolves to the default typeface,
            // which the probe then rejects.
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(name), out var candidate)) continue;
            if (!candidate.CharacterToGlyphMap.ContainsGlyph(EmojiProbeCodePoint)) continue;
            return (name, candidate);
        }

        if (FontManager.Current.TryGetGlyphTypeface(new Typeface(chain), out var fallback))
            return (chain.FamilyNames.PrimaryFamilyName, fallback);
        return (chain.FamilyNames.PrimaryFamilyName, null);
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
