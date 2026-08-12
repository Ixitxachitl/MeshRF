// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Emoji picker for message reactions, waypoint icons and the compose box.
/// The glyphs come from <see cref="EmojiCatalog"/>, so the grid holds every
/// emoji the colour emoji font on this machine can draw and nothing it can't;
/// the tabs are Unicode's own emoji groups and the search box matches Unicode
/// names.
/// </summary>
public partial class EmojiPickerWindow : Window
{
    private readonly bool _singleCodePointOnly;
    private readonly List<(string Group, List<EmojiCatalog.Entry> Entries)> _groups = [];

    private EmojiCatalog.Snapshot? _catalog;
    private string? _picked;

    public EmojiPickerWindow() : this(singleCodePointOnly: false) { }

    public EmojiPickerWindow(bool singleCodePointOnly)
    {
        _singleCodePointOnly = singleCodePointOnly;
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Rebuild();
        CategoryTabs.SelectionChanged += (_, _) => FillSelectedTab();
    }

    /// <summary>Show the picker and return the chosen glyph, or null if cancelled.</summary>
    /// <param name="singleCodePointOnly">
    /// Restrict the grid to emoji that are a single code point. Meshtastic
    /// waypoint icons are one uint32, so flags, keycaps and ZWJ sequences would
    /// be silently truncated to their first scalar there.
    /// </param>
    public static async Task<string?> PickAsync(Window owner, bool singleCodePointOnly = false)
    {
        var w = new EmojiPickerWindow(singleCodePointOnly);
        await w.ShowDialog(owner);
        return w._picked;
    }

    /// <summary>
    /// Built here rather than in the constructor: the font chain to filter
    /// against comes from the Window style in App.axaml, which is only applied
    /// once the window is attached.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _catalog ??= EmojiCatalog.For(FontFamily);
        Rebuild();
    }

    /// <summary>Regroup for the current search text and reset the tab strip.</summary>
    private void Rebuild()
    {
        if (_catalog is null) return;
        string filter = (SearchBox.Text ?? string.Empty).Trim();

        string? wasSelected = (CategoryTabs.SelectedItem as TabItem)?.Tag as string;
        _groups.Clear();
        int shown = 0;
        foreach (var entry in _catalog.Entries)
        {
            if (_singleCodePointOnly && !entry.IsSingleCodePoint) continue;
            if (!Matches(entry, filter)) continue;

            // The catalog is in Unicode's display order and groups its entries,
            // so starting a bucket whenever the group changes keeps both the
            // tabs and the glyphs within them in that order.
            if (_groups.Count == 0 || _groups[^1].Group != entry.Group)
                _groups.Add((entry.Group, []));
            _groups[^1].Entries.Add(entry);
            shown++;
        }

        CategoryTabs.Items.Clear();
        foreach (var (group, _) in _groups)
            CategoryTabs.Items.Add(new TabItem { Header = group, Tag = group });

        if (CategoryTabs.ItemCount > 0)
        {
            int index = wasSelected is null ? 0 : _groups.FindIndex(g => g.Group == wasSelected);
            CategoryTabs.SelectedIndex = index < 0 ? 0 : index;
            FillSelectedTab();
        }

        StatusText.Text = filter.Length > 0
            ? $"{shown} match{(shown == 1 ? "" : "es")}"
            : $"{shown} emoji available in {_catalog.FontFamily}";
    }

    /// <summary>
    /// Populate the visible tab only. The full set is ~1900 glyphs; building
    /// every tab up front — and again on each keystroke — is the difference
    /// between the picker opening instantly and visibly stalling.
    /// </summary>
    private void FillSelectedTab()
    {
        if (CategoryTabs.SelectedItem is not TabItem { Tag: string group } tab) return;
        if (tab.Content is not null) return;

        int index = _groups.FindIndex(g => g.Group == group);
        if (index < 0) return;

        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        foreach (var entry in _groups[index].Entries)
        {
            var b = new Button
            {
                // Pin the emoji font so the grid draws with the same font the
                // catalog was filtered against.
                Content = new TextBlock
                {
                    Text = entry.Glyph,
                    FontSize = 18,
                    FontFamily = new FontFamily(_catalog!.FontFamily),
                },
                // Roomy enough that the glyphs read as a grid rather than a
                // wall, and that each one is a comfortable click target.
                Padding = new Thickness(7, 5),
                Margin = new Thickness(3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = entry.Glyph,
            };
            ToolTip.SetTip(b, entry.Name);
            b.Click += OnGlyphClick;
            panel.Children.Add(b);
        }

        tab.Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static bool Matches(EmojiCatalog.Entry entry, string filter) =>
        filter.Length == 0
        || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        // Lets a pasted glyph find itself, which is how the old picker searched.
        || entry.Glyph.Contains(filter, StringComparison.Ordinal);

    private void OnGlyphClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string glyph }) _picked = glyph;
        Close();
    }
}
