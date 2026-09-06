// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MeshRF.UiTests;

/// <summary>
/// A control drawn offscreen, with its pixels readable.
///
/// The charts and the map are drawn immediate-mode into a
/// <see cref="DrawingContext"/> rather than composed from controls, so there is
/// no visual tree to assert against and nothing a layout test could see. What
/// came out is the only evidence there is — every rendering bug this suite
/// exists for (a clipped checkbox, an axis printing "-0", a tofu box where a
/// glyph should be, a legend narrower than its own text) was invisible to
/// every other kind of test.
/// </summary>
public sealed class Rendered
{
    private readonly byte[] _rgba;

    public int Width { get; }
    public int Height { get; }

    private Rendered(byte[] rgba, int width, int height)
    {
        _rgba = rgba;
        Width = width;
        Height = height;
    }

    /// <summary>Draws a control at a given size and reads back its pixels.
    /// </summary>
    /// <param name="beforeCapture">Run once the control is laid out and before
    /// the frame is taken. A test that has to say where in the picture to look
    /// can only find out by asking the arranged control, which does not exist
    /// until this point and is gone once the window closes.</param>
    public static Rendered Draw(Control content, int width, int height,
                                Action<Window>? beforeCapture = null)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Black,
            Content = content,
        };

        window.Show();

        // Layout, then drawing, then the frame grab. Several passes because a
        // measure can invalidate an arrange, and a control that sizes itself
        // from its content only knows its bounds once arranged.
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        beforeCapture?.Invoke(window);
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("the headless platform captured no frame");

        var size = frame.PixelSize;
        var buffer = new byte[size.Width * size.Height * 4];
        unsafe
        {
            fixed (byte* p = buffer)
                frame.CopyPixels(
                    new PixelRect(0, 0, size.Width, size.Height),
                    (IntPtr)p, buffer.Length, size.Width * 4);
        }

        // Detached before closing, or the control keeps a visual parent and
        // drawing it again — the same chart at two sizes, say — throws.
        window.Content = null;
        window.Close();

        return new Rendered(buffer, size.Width, size.Height);
    }

    /// <summary>The colour at a pixel.</summary>
    /// <remarks>The headless frame comes back as RGBA. Reading it as BGRA
    /// instead swaps red and blue, which leaves greys and greens looking
    /// perfectly correct and silently turns every cyan into an amber — the
    /// first version of this harness did exactly that, and spent a long time
    /// insisting a chart had not drawn its dots.</remarks>
    public (byte R, byte G, byte B) At(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (_rgba[i], _rgba[i + 1], _rgba[i + 2]);
    }

    /// <summary>How many pixels in a region satisfy a predicate.</summary>
    public int Count(Func<(byte R, byte G, byte B), bool> matches, PixelRect? within = null)
    {
        var area = within ?? new PixelRect(0, 0, Width, Height);
        int found = 0;

        for (int y = Math.Max(0, area.Y); y < Math.Min(Height, area.Y + area.Height); y++)
            for (int x = Math.Max(0, area.X); x < Math.Min(Width, area.X + area.Width); x++)
                if (matches(At(x, y))) found++;

        return found;
    }

    /// <summary>Pixels close to a colour. Skia antialiases everything, so an
    /// exact match finds only the interiors of large fills and misses thin
    /// lines entirely.</summary>
    public int CountNear(string hex, int tolerance = 18, PixelRect? within = null)
    {
        var want = Color.Parse(hex);
        return Count(p => Math.Abs(p.R - want.R) <= tolerance
                       && Math.Abs(p.G - want.G) <= tolerance
                       && Math.Abs(p.B - want.B) <= tolerance, within);
    }

    /// <summary>Pixels that are not the near-black the tests draw onto, which
    /// is how a glyph or a stroke is found without knowing its exact colour.
    /// </summary>
    public int CountInk(PixelRect within, int floor = 0x60) =>
        Count(p => p.R > floor || p.G > floor || p.B > floor, within);

    /// <summary>The rightmost column in a region holding ink. Used to prove a
    /// panel is wide enough for what was drawn inside it.</summary>
    public int RightmostInk(PixelRect within, int floor = 0x60)
    {
        for (int x = Math.Min(Width, within.X + within.Width) - 1; x >= within.X; x--)
            for (int y = within.Y; y < Math.Min(Height, within.Y + within.Height); y++)
            {
                var (r, g, b) = At(x, y);
                if (r > floor || g > floor || b > floor) return x;
            }
        return -1;
    }
}
