// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

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
    /// <summary>Side of one glyph cell. Square so the grid reads as a grid
    /// whatever the glyph's advance width is: flags are two half-width tiles,
    /// keycaps are narrow, and letting each button size to its content left the
    /// rows visibly ragged.</summary>
    private const double CellSize = 34;

    private const double GlyphFontSize = 18;

    private readonly bool _singleCodePointOnly;
    private readonly List<(string Group, List<EmojiCatalog.Entry> Entries)> _groups = [];

    private EmojiCatalog.Snapshot? _catalog;
    private string? _picked;

    /// <summary>Breathing room inside each glyph's text box and the vertical
    /// nudge that centres it, both measured once per open by
    /// <see cref="MeasureGlyphFit"/>.</summary>
    private double _glyphInset;
    private double _inkOffset;

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
    /// Built on first open rather than in the constructor: filtering the
    /// catalogue shapes every emoji in it, which is far too slow to do on the
    /// UI thread for a window that may never be opened.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _catalog ??= EmojiCatalog.For();
        (_glyphInset, _inkOffset) = MeasureGlyphFit(_catalog.FontFamily);
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
                    FontSize = GlyphFontSize,
                    FontFamily = new FontFamily(_catalog!.FontFamily),
                    TextAlignment = TextAlignment.Center,
                    // Text is clipped to its own box, and emoji paint outside
                    // theirs — the tops came off without this.
                    Padding = new Thickness(_glyphInset),
                    // A render transform, so the correction moves the paint
                    // without resizing the cell that centres it.
                    RenderTransform = new TranslateTransform(0, _inkOffset),
                },
                // The cell is the click target, and the glyph is centred in it
                // both ways. Fluent's own MinWidth/MinHeight would otherwise
                // stretch the button past the square.
                Width = CellSize,
                Height = CellSize,
                MinWidth = 0,
                MinHeight = 0,
                Padding = default,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
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

    /// <summary>
    /// How much room a glyph needs beyond its text box, and how far to push it
    /// down so its paint — rather than its line box — sits in the middle of a
    /// cell.
    ///
    /// Both are needed because emoji do not fit the box the text layout gives
    /// them: the paint runs above the line box (a glyph is clipped to that box,
    /// which is what sliced the tops off) while the line gap adds slack below
    /// it, leaving a box-centred glyph sitting high. Padding the box by the
    /// overhang fixes the first, and the offset fixes the second.
    ///
    /// Measured by drawing one and looking rather than computed from the font,
    /// because a colour emoji is a bitmap or a stack of layers, not an outline:
    /// the font reports a zero-height ink box for every one of them. One probe
    /// serves the whole grid — the glyphs all come from the single pinned emoji
    /// font, so they share its metrics.
    /// </summary>
    private static (double Inset, double Offset) MeasureGlyphFit(string fontFamily)
    {
        // Padding for the probe alone, far beyond any plausible overhang, so
        // the measurement itself cannot be the thing that crops the glyph.
        const double probePad = 12;

        // U+1F600 GRINNING FACE, the same glyph EmojiCatalog probes the font
        // with, so a font that got this far is known to draw it.
        var text = new TextBlock
        {
            Text = "\U0001F600",
            FontSize = GlyphFontSize,
            FontFamily = new FontFamily(fontFamily),
            Foreground = Brushes.White,
            Padding = new Thickness(probePad),
        };

        try
        {
            text.Measure(Size.Infinity);
            var box = text.DesiredSize;
            text.Arrange(new Rect(box));

            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(box.Width)),
                                       Math.Max(1, (int)Math.Ceiling(box.Height)));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96, 96));
            bitmap.Render(text);

            int stride = pixels.Width * 4;
            var buffer = new byte[stride * pixels.Height];
            unsafe
            {
                fixed (byte* p = buffer)
                    bitmap.CopyPixels(new PixelRect(pixels), (IntPtr)p, buffer.Length, stride);
            }

            int top = -1, bottom = -1, left = int.MaxValue, right = -1;
            for (int y = 0; y < pixels.Height; y++)
                for (int x = 0; x < pixels.Width; x++)
                {
                    if (buffer[y * stride + x * 4 + 3] <= 8) continue; // alpha
                    if (top < 0) top = y;
                    bottom = y;
                    if (x < left) left = x;
                    if (x > right) right = x;
                }

            if (top < 0) return (0, 0);

            // The line box the layout handed the glyph, in the probe's frame.
            double lineWidth = box.Width - probePad * 2;
            double lineHeight = box.Height - probePad * 2;

            double overhang = Math.Max(
                Math.Max(probePad - top, probePad - left),
                Math.Max(bottom + 1 - (probePad + lineHeight), right + 1 - (probePad + lineWidth)));

            // One pixel over the measured overhang: the probe speaks for the
            // font, and other glyphs in it are drawn on the same square but not
            // to the same edges.
            double inset = Math.Max(0, Math.Ceiling(overhang) + 1);

            // Ink centre relative to the line box, which is what the cell
            // centres — so the padding cancels out and does not appear here.
            double inkCentre = (top + bottom + 1) / 2.0 - probePad;
            return (inset, lineHeight / 2 - inkCentre);
        }
        catch
        {
            // Nothing here is worth failing the picker over: an uncorrected
            // grid is a couple of pixels off, not broken.
            return (0, 0);
        }
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
