// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Emoji picker, ported from MeshRF.App's EmojiPickerWindow. Same category
/// ranges, so both apps offer the same glyphs. Used for message reactions and
/// for inserting into the compose box.
/// </summary>
public partial class EmojiPickerWindow : Window
{
    private readonly record struct Range(int Start, int End);
    private sealed record CategorySpec(string Name, Range[] Ranges);

    // Mirrors MeshRF.App's s_categorySpecs.
    private static readonly CategorySpec[] CategorySpecs =
    [
        new("Smileys", [new(0x1F600, 0x1F64F), new(0x1F910, 0x1F92F), new(0x1FAE0, 0x1FAE8)]),
        new("Hands", [new(0x1F440, 0x1F450), new(0x1F90C, 0x1F93A), new(0x1FAF0, 0x1FAF8), new(0x270A, 0x270D)]),
        new("People", [new(0x1F460, 0x1F487), new(0x1F574, 0x1F57A), new(0x1F645, 0x1F64F)]),
        new("Animals", [new(0x1F400, 0x1F43E), new(0x1F980, 0x1F9AE), new(0x1F330, 0x1F33F)]),
        new("Food", [new(0x1F32D, 0x1F37F), new(0x1F950, 0x1F96F)]),
        new("Travel", [new(0x1F680, 0x1F6FF), new(0x1F300, 0x1F32C), new(0x1F5FA, 0x1F5FF), new(0x26F0, 0x26FF)]),
        new("Activities", [new(0x1F380, 0x1F3C8), new(0x1F3D0, 0x1F3FA), new(0x1F93F, 0x1F94F)]),
        new("Objects", [new(0x1F4A1, 0x1F4FF), new(0x1F6E0, 0x1F6EC), new(0x1F9F0, 0x1F9FF), new(0x1FA70, 0x1FAFF)]),
        new("Symbols", [new(0x2600, 0x26FF), new(0x2700, 0x27BF), new(0x2B00, 0x2BFF), new(0x2194, 0x21AA), new(0x2300, 0x23FF)]),
    ];

    private static readonly Lazy<List<(string Category, string Glyph)>> Catalog = new(BuildCatalog);

    private string? _picked;

    public EmojiPickerWindow()
    {
        InitializeComponent();
        BuildTabs(null);
        SearchBox.TextChanged += (_, _) => BuildTabs(SearchBox.Text);
    }

    /// <summary>Show the picker and return the chosen glyph, or null if cancelled.</summary>
    public static async Task<string?> PickAsync(Window owner)
    {
        var w = new EmojiPickerWindow();
        await w.ShowDialog(owner);
        return w._picked;
    }

    private static List<(string Category, string Glyph)> BuildCatalog()
    {
        var list = new List<(string, string)>();
        foreach (var spec in CategorySpecs)
        {
            foreach (var range in spec.Ranges)
            {
                for (int cp = range.Start; cp <= range.End; cp++)
                {
                    // Skip unassigned/non-printable code points so the grid
                    // doesn't fill with tofu boxes.
                    var cat = CharUnicodeInfo.GetUnicodeCategory(cp switch
                    {
                        <= 0xFFFF => (char)cp,
                        _ => '�',
                    });
                    if (cp <= 0xFFFF && cat is System.Globalization.UnicodeCategory.OtherNotAssigned
                                            or System.Globalization.UnicodeCategory.Control)
                        continue;
                    string glyph;
                    try { glyph = char.ConvertFromUtf32(cp); }
                    catch { continue; }
                    list.Add((spec.Name, glyph));
                }
            }
        }
        return list;
    }

    private void BuildTabs(string? filter)
    {
        CategoryTabs.Items.Clear();
        var f = (filter ?? string.Empty).Trim();

        foreach (var spec in CategorySpecs)
        {
            var glyphs = Catalog.Value
                .Where(e => e.Category == spec.Name)
                .Select(e => e.Glyph)
                .Where(g => f.Length == 0 || g.Contains(f, StringComparison.Ordinal))
                .ToList();
            if (glyphs.Count == 0) continue;

            var panel = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var glyph in glyphs)
            {
                var b = new Button
                {
                    Content = new TextBlock { Text = glyph, FontSize = 18 },
                    Padding = new Thickness(4, 2),
                    Margin = new Thickness(1),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Tag = glyph,
                };
                b.Click += OnGlyphClick;
                panel.Children.Add(b);
            }

            CategoryTabs.Items.Add(new TabItem
            {
                Header = spec.Name,
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            });
        }
        if (CategoryTabs.ItemCount > 0) CategoryTabs.SelectedIndex = 0;
    }

    private void OnGlyphClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string glyph }) _picked = glyph;
        Close();
    }
}
