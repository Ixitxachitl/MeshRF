// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The marks on the map's layer toggles. Both were Unicode glyphs and both
/// failed in the way a glyph fails: U+2592 is a checkerboard, which at 28px
/// is a smudge, and U+25EB is absent from enough interface fonts to fall back
/// to another one and come out the wrong size. Neither failure is visible to
/// a test that only reads the control tree — what was drawn is the evidence.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class MapLayerIconTests(HeadlessAvalonia avalonia)
{
    private static readonly string[] Drawn = ["HeatmapButton", "BuildingsButton"];

    private static ToggleButton Toggle(MapPanel panel, string name) =>
        panel.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == name);

    /// <summary>Every part of the mark, the root of the content included.</summary>
    private static IEnumerable<Visual> MarkParts(ToggleButton button)
    {
        var root = Assert.IsAssignableFrom<Visual>(button.Content);
        return root.GetVisualDescendants().Prepend(root);
    }

    /// <summary>
    /// A drawn mark, not a typed one. This is the whole point of the change:
    /// a shape the app carries is the same shape wherever it runs, where a
    /// glyph is at the mercy of whichever font the machine substitutes.
    /// </summary>
    [Fact]
    public void TheLayerMarksAreDrawnRatherThanTyped() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var panel = new MapPanel();
        var window = new Window { Width = 900, Height = 700, Content = panel };
        window.Show();

        foreach (var name in Drawn)
        {
            var button = Toggle(panel, name);
            Assert.Empty(MarkParts(button).OfType<TextBlock>());
            Assert.Contains(MarkParts(button), v => v is Shape or Border);
        }

        window.Content = null;
        window.Close();
    }));

    /// <summary>
    /// The mark paints with the button's own Foreground, so it survives the
    /// toggle going checked and the face filling with the accent colour. A
    /// brush fixed at the point it was written would disappear into it.
    /// </summary>
    [Fact]
    public void TheMarksTakeTheButtonsForegroundInBothStates() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var panel = new MapPanel();
        var window = new Window { Width = 900, Height = 700, Content = panel };
        window.Show();

        foreach (bool check in new[] { false, true })
            foreach (var name in Drawn)
            {
                var button = Toggle(panel, name);
                button.IsChecked = check;

                var brushes = MarkParts(button).SelectMany(v => v switch
                {
                    Shape s => new[] { s.Fill, s.Stroke },
                    Border b => new[] { b.Background, b.BorderBrush },
                    _ => [],
                }).Where(b => b is not null).ToList();

                Assert.NotEmpty(brushes);
                Assert.All(brushes, b => Assert.Same(button.Foreground, b));
            }

        window.Content = null;
        window.Close();
    }));

    /// <summary>
    /// And it actually lands on the button. The count is what the old glyphs
    /// could not manage: the mark has to carry the 28px face, not sit in it as
    /// a few faint strokes.
    /// </summary>
    [Fact]
    public void EachMarkCarriesItsButton() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var panel = new MapPanel();
        var faces = new Dictionary<string, PixelRect>();

        var shot = Rendered.Draw(panel, 900, 700, window =>
        {
            foreach (var name in Drawn)
            {
                var button = Toggle(panel, name);
                var corner = button.TranslatePoint(new Point(0, 0), window)!.Value;
                faces[name] = new PixelRect((int)corner.X, (int)corner.Y,
                                            (int)button.Bounds.Width, (int)button.Bounds.Height);
            }
        });

        foreach (var name in Drawn)
        {
            int lit = shot.Count(p => p.R > 0xC0 && p.G > 0xC0 && p.B > 0xC0, faces[name]);
            Assert.True(lit > 50, $"{name} drew only {lit} lit pixels; the mark is faint or missing");
        }
    }));
}
