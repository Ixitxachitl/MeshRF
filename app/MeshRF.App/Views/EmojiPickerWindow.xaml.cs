// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MeshRF.App.Views;

/// <summary>Emoji picker used for reactions and waypoint icons.</summary>
public partial class EmojiPickerWindow : Window
{
    public enum EmojiPickerMode
    {
        WaypointIcon,
        Reaction,
    }

    public sealed record EmojiEntry(string Glyph, string Category, string? Description = null, int? PrimaryCodePoint = null)
    {
        public string ToolTip => string.IsNullOrWhiteSpace(Description)
            ? BuildCodePointLabel(Glyph)
            : $"{Glyph}  {Description}";

        private static string BuildCodePointLabel(string glyph)
        {
            var parts = new List<string>();
            foreach (var rune in glyph.EnumerateRunes())
                parts.Add($"U+{rune.Value:X}");
            return $"{glyph}  {string.Join(" ", parts)}";
        }
    }

    public sealed record EmojiCategory(string Name, IReadOnlyList<EmojiEntry> Emojis);

    private readonly record struct Range(int Start, int End);
    private sealed record CategorySpec(string Name, IReadOnlyList<Range> Ranges);

    private static readonly CategorySpec[] s_categorySpecs =
    [
        new("Smileys", [new(0x1F600, 0x1F64F), new(0x1F910, 0x1F92F), new(0x1FAE0, 0x1FAE8)]),
        new("Hands", [new(0x1F440, 0x1F450), new(0x1F90C, 0x1F93A), new(0x1FAF0, 0x1FAF8), new(0x270A, 0x270D)]),
        new("People", [new(0x1F460, 0x1F487), new(0x1F574, 0x1F57A), new(0x1F645, 0x1F64F)]),
        new("Animals", [new(0x1F400, 0x1F43E), new(0x1F980, 0x1F9AE), new(0x1F330, 0x1F33F)]),
        new("Food", [new(0x1F32D, 0x1F37F), new(0x1F950, 0x1F96F)]),
        new("Travel", [new(0x1F680, 0x1F6FF), new(0x1F300, 0x1F32C), new(0x1F5FA, 0x1F5FF), new(0x26F0, 0x26FF)]),
        new("Activities", [new(0x1F380, 0x1F3C8), new(0x1F3D0, 0x1F3FA), new(0x1F93F, 0x1F94F)]),
        new("Objects", [new(0x1F4A1, 0x1F4FF), new(0x1F6E0, 0x1F6EC), new(0x1F9F0, 0x1F9FF), new(0x1FA70, 0x1FAFF)]),
        new("Symbols", [new(0x2600, 0x26FF), new(0x2700, 0x27BF), new(0x2B00, 0x2BFF), new(0x2194, 0x21AA), new(0x2300, 0x23FF), new(0x2934, 0x2935), new(0x3030, 0x3030), new(0x303D, 0x303D), new(0x3297, 0x3299)])
    ];

    private static readonly Lazy<IReadOnlyList<EmojiEntry>> s_catalog = new(BuildCatalog);
    private static readonly Lazy<IReadOnlyList<EmojiCategory>> s_waypointCategories =
        new(() => BuildCategories(includeReactionSequences: false));
    private static readonly Lazy<IReadOnlyList<EmojiCategory>> s_reactionCategories =
        new(() => BuildCategories(includeReactionSequences: true));

    public IReadOnlyList<EmojiCategory> Categories { get; }

    public string? SelectedEmoji { get; private set; }

    public EmojiPickerWindow(EmojiPickerMode mode)
    {
        InitializeComponent();
        Categories = mode == EmojiPickerMode.Reaction
            ? s_reactionCategories.Value
            : s_waypointCategories.Value;
        Title = mode == EmojiPickerMode.Reaction ? "Pick Reaction" : "Pick Waypoint Icon";
        DataContext = this;
    }

    public static string? PickEmoji(Window? owner, EmojiPickerMode mode = EmojiPickerMode.Reaction)
    {
        var dlg = new EmojiPickerWindow(mode)
        {
            Owner = owner,
        };

        return dlg.ShowDialog() == true ? dlg.SelectedEmoji : null;
    }

    private static IReadOnlyList<EmojiCategory> BuildCategories(bool includeReactionSequences)
    {
        var catalog = s_catalog.Value;
        var categories = new List<EmojiCategory>(s_categorySpecs.Length + 3);
        var all = new List<EmojiEntry>(catalog);

        foreach (var spec in s_categorySpecs)
        {
            var list = catalog.Where(e => string.Equals(e.Category, spec.Name, StringComparison.Ordinal)).ToList();
            if (list.Count > 0)
                categories.Add(new EmojiCategory($"{spec.Name} ({list.Count.ToString(CultureInfo.InvariantCulture)})", list));
        }

        if (includeReactionSequences)
        {
            var keycaps = BuildKeycapEntries();
            var flags = BuildFlagEntries();
            categories.Add(new EmojiCategory($"Keycaps ({keycaps.Count.ToString(CultureInfo.InvariantCulture)})", keycaps));
            categories.Add(new EmojiCategory($"Flags ({flags.Count.ToString(CultureInfo.InvariantCulture)})", flags));
            all.AddRange(keycaps);
            all.AddRange(flags);
        }

        categories.Add(new EmojiCategory($"All ({all.Count.ToString(CultureInfo.InvariantCulture)})", all));
        return categories;
    }

    private static IReadOnlyList<EmojiEntry> BuildCatalog()
    {
        var list = new List<EmojiEntry>();
        var seen = new HashSet<int>();

        foreach (var spec in s_categorySpecs)
        {
            foreach (var range in spec.Ranges)
            {
                for (int codePoint = range.Start; codePoint <= range.End; codePoint++)
                {
                    if (!IsSupportedSingleCodePointEmoji(codePoint) || !seen.Add(codePoint))
                        continue;

                    list.Add(new EmojiEntry(char.ConvertFromUtf32(codePoint), spec.Name, PrimaryCodePoint: codePoint));
                }
            }
        }

        return list
            .OrderBy(e => CategoryOrder(e.Category))
            .ThenBy(e => e.PrimaryCodePoint ?? int.MaxValue)
            .ThenBy(e => e.Glyph, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSupportedSingleCodePointEmoji(int codePoint)
    {
        if (!Rune.IsValid(codePoint)) return false;
        if (char.IsSurrogate((char)Math.Min(codePoint, char.MaxValue))) return false;

        return codePoint switch
        {
            >= 0x1F300 and <= 0x1F5FF => true,
            >= 0x1F600 and <= 0x1F64F => true,
            >= 0x1F680 and <= 0x1F6FF => true,
            >= 0x1F900 and <= 0x1F9FF => true,
            >= 0x1FA70 and <= 0x1FAFF => true,
            >= 0x2600 and <= 0x26FF => true,
            >= 0x2700 and <= 0x27BF => true,
            >= 0x2B00 and <= 0x2BFF => true,
            >= 0x2300 and <= 0x23FF => true,
            >= 0x2194 and <= 0x21AA => true,
            0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299 => true,
            _ => false,
        };
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Smileys" => 0,
        "Hands" => 1,
        "People" => 2,
        "Animals" => 3,
        "Food" => 4,
        "Travel" => 5,
        "Activities" => 6,
        "Objects" => 7,
        "Symbols" => 8,
        _ => 99,
    };

    private static IReadOnlyList<EmojiEntry> BuildKeycapEntries()
    {
        return new List<EmojiEntry>
        {
            new("0️⃣", "Keycaps", "keycap 0"),
            new("1️⃣", "Keycaps", "keycap 1"),
            new("2️⃣", "Keycaps", "keycap 2"),
            new("3️⃣", "Keycaps", "keycap 3"),
            new("4️⃣", "Keycaps", "keycap 4"),
            new("5️⃣", "Keycaps", "keycap 5"),
            new("6️⃣", "Keycaps", "keycap 6"),
            new("7️⃣", "Keycaps", "keycap 7"),
            new("8️⃣", "Keycaps", "keycap 8"),
            new("9️⃣", "Keycaps", "keycap 9"),
            new("#️⃣", "Keycaps", "keycap #"),
            new("*️⃣", "Keycaps", "keycap *"),
        };
    }

    private static IReadOnlyList<EmojiEntry> BuildFlagEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flags = new List<EmojiEntry>();

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                var iso = region.TwoLetterISORegionName;
                if (iso.Length != 2 || !iso.All(ch => ch is >= 'A' and <= 'Z') || !seen.Add(iso))
                    continue;

                flags.Add(new EmojiEntry(BuildFlagGlyph(iso), "Flags", region.EnglishName));
            }
            catch
            {
            }
        }

        return flags.OrderBy(f => f.Description, StringComparer.CurrentCulture).ToList();
    }

    private static string BuildFlagGlyph(string isoCountryCode)
    {
        const int regionalIndicatorBase = 0x1F1E6;
        return string.Concat(char.ConvertFromUtf32(regionalIndicatorBase + (isoCountryCode[0] - 'A')),
                             char.ConvertFromUtf32(regionalIndicatorBase + (isoCountryCode[1] - 'A')));
    }

    private void OnEmojiClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string glyph || string.IsNullOrWhiteSpace(glyph))
            return;

        SelectedEmoji = glyph.Trim();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
