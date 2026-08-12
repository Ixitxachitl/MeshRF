// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MeshRF.AvaloniaApp;

public enum WaterfallColormap
{
    Turbo,
    Inferno,
    Meshtastic,
}

/// <summary>
/// Scrolling dBFS waterfall, ported from MeshRF.App's WPF WaterfallView but
/// scoped down to what this app currently drives: a live scrolling ring
/// buffer with auto-levels. The WPF version's ScaleToFit/TimeHorizontal/
/// SmoothPixels toggles exist to support a frozen "last packet" snapshot
/// panel, which isn't built in this app yet. This port also always does a
/// full-frame re-render on Push rather than WPF's incremental single-row
/// bitmap shift — the shift is an optimization for a native ~60fps push
/// rate; at this app's spectrum poll rate a full re-render is cheap enough
/// that the extra complexity isn't worth it.
/// </summary>
public sealed class WaterfallView : Image
{
    public static readonly StyledProperty<double> FloorDbProperty =
        AvaloniaProperty.Register<WaterfallView, double>(nameof(FloorDb), -100.0);
    public static readonly StyledProperty<double> CeilDbProperty =
        AvaloniaProperty.Register<WaterfallView, double>(nameof(CeilDb), 0.0);
    public static readonly StyledProperty<bool> AutoLevelsProperty =
        AvaloniaProperty.Register<WaterfallView, bool>(nameof(AutoLevels), true);
    public static readonly StyledProperty<WaterfallColormap> ColormapProperty =
        AvaloniaProperty.Register<WaterfallView, WaterfallColormap>(nameof(Colormap), WaterfallColormap.Turbo);

    public double FloorDb { get => GetValue(FloorDbProperty); set => SetValue(FloorDbProperty, value); }
    public double CeilDb { get => GetValue(CeilDbProperty); set => SetValue(CeilDbProperty, value); }
    public bool AutoLevels { get => GetValue(AutoLevelsProperty); set => SetValue(AutoLevelsProperty, value); }
    public WaterfallColormap Colormap { get => GetValue(ColormapProperty); set => SetValue(ColormapProperty, value); }

    /// <summary>Raised whenever AutoLevels recomputes the floor/ceil window.
    /// The owner can mirror these onto bound sliders/labels.</summary>
    public event Action<double, double>? AutoLevelsChanged;

    // Ring buffer of frames. Each row has length _binCount. Newest row is at
    // index (_head - 1) mod _capacity. _filled counts valid rows.
    private float[]? _ring;
    private int _binCount;
    private int _capacity;
    private int _head;
    private int _filled;

    private WriteableBitmap? _bmp;
    private int _w;
    private int _h;
    private int[]? _x0Map;
    private int[]? _x1Map;
    private int _xMapW;
    private int _xMapBins;

    // Auto-level smoothing state.
    private double _autoFloor = -100.0;
    private double _autoCeil = 0.0;
    private int _autoFrameCounter;

    // When true, FloorDb/CeilDb change callbacks are ignored — used during a
    // Push to coalesce auto-levels writes into the single Render at the end.
    private bool _suppressRender;

    static WaterfallView()
    {
        FloorDbProperty.Changed.AddClassHandler<WaterfallView>((v, _) => v.OnLevelsChanged());
        CeilDbProperty.Changed.AddClassHandler<WaterfallView>((v, _) => v.OnLevelsChanged());
        ColormapProperty.Changed.AddClassHandler<WaterfallView>((v, _) => { v._lutValid = false; v.OnLevelsChanged(); });
    }

    public WaterfallView()
    {
        Stretch = Stretch.Fill;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        SizeChanged += (_, _) => { EnsureBitmap(); Render(); };
    }

    private void OnLevelsChanged()
    {
        if (_suppressRender) return;
        Render();
    }

    private void EnsureBitmap()
    {
        double actualWidth = Bounds.Width;
        double actualHeight = Bounds.Height;

        // Once a bitmap exists, don't tear it down or shrink the ring just
        // because this control is temporarily off-screen (e.g. an inactive
        // tab) — keep accumulating frames so the waterfall is intact when
        // shown again.
        if (_bmp != null && (actualWidth < 1 || actualHeight < 1)) return;

        var w = (int)Math.Max(64, Math.Round(actualWidth));
        var h = (int)Math.Max(64, Math.Round(actualHeight));
        if (_bmp != null && _w == w && _h == h) return;

        _w = w;
        _h = h;
        _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        Source = _bmp;
        _x0Map = null;
        _x1Map = null;
        _xMapW = 0;
        _xMapBins = 0;
        ResizeRing(h > 0 ? h : 1);
    }

    private void ResizeRing(int newCapacity)
    {
        if (newCapacity <= 0) newCapacity = 1;

        if (_binCount == 0)
        {
            _ring = null;
            _capacity = newCapacity;
            _head = 0;
            _filled = 0;
            return;
        }

        var fresh = new float[(long)newCapacity * _binCount];
        int keep = Math.Min(newCapacity, _filled);
        for (int i = 0; i < keep; i++)
        {
            // Preserve chronological order (oldest -> newest) across resizes.
            int oldRow = (_head - keep + i + _capacity) % _capacity;
            int newRow = i;
            Array.Copy(_ring!, (long)oldRow * _binCount,
                       fresh, (long)newRow * _binCount, _binCount);
        }

        _ring = fresh;
        _capacity = newCapacity;
        _head = keep % newCapacity;
        _filled = keep;
    }

    public void Push(ReadOnlySpan<float> frame)
    {
        if (frame.Length == 0) return;
        if (_bmp is null) EnsureBitmap();

        if (frame.Length != _binCount)
        {
            _binCount = frame.Length;
            _ring = new float[(long)_capacity * _binCount];
            _head = 0;
            _filled = 0;
            _x0Map = null;
            _x1Map = null;
            _xMapW = 0;
            _xMapBins = 0;
        }
        if (_ring is null || _capacity == 0) return;

        int dstRow = _head;
        long offset = (long)dstRow * _binCount;
        if (offset > int.MaxValue) return;
        frame.CopyTo(_ring.AsSpan((int)offset, _binCount));
        _head = (_head + 1) % _capacity;
        if (_filled < _capacity) _filled++;

        _suppressRender = true;
        try
        {
            if (AutoLevels && (++_autoFrameCounter % 10) == 0)
                UpdateAutoLevels(frame);
        }
        finally
        {
            _suppressRender = false;
        }

        Render();
    }

    private void UpdateAutoLevels(ReadOnlySpan<float> latest)
    {
        // Sample-quantile estimate over the most recent frame. We pick:
        //   floor = 5th percentile  (approx noise floor)
        //   ceil  = 99th percentile + 6 dB headroom (so peaks stay bright)
        // and exponentially smooth toward the new value to avoid jitter.
        var n = latest.Length;
        if (n < 16) return;
        Span<float> tmp = n <= 4096 ? stackalloc float[n] : new float[n];
        int valid = 0;
        for (int i = 0; i < n; i++)
        {
            var v = latest[i];
            if (!float.IsNaN(v) && !float.IsInfinity(v))
                tmp[valid++] = v;
        }
        if (valid < 16) return;
        var slice = tmp[..valid];
        slice.Sort();
        var p5 = slice[(int)(valid * 0.05)];
        var p99 = slice[(int)(valid * 0.99)];
        var newFloor = p5 - 3.0;
        var newCeil = p99 + 6.0;
        if (newCeil - newFloor < 20.0) newCeil = newFloor + 20.0; // min 20 dB span

        const double a = 0.25; // smoothing factor (per recompute)
        _autoFloor = _autoFloor * (1 - a) + newFloor * a;
        _autoCeil = _autoCeil * (1 - a) + newCeil * a;

        FloorDb = _autoFloor;
        CeilDb = _autoCeil;
        AutoLevelsChanged?.Invoke(_autoFloor, _autoCeil);
    }

    // 256-entry BGRA color LUT, rebuilt when Colormap changes.
    private readonly uint[] _lut = new uint[256];
    private bool _lutValid;

    private void EnsureLut()
    {
        if (_lutValid) return;
        _lutValid = true;
        var cmap = Colormap;
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            byte r, g, b;
            if (cmap == WaterfallColormap.Turbo) TurboMap(t, out r, out g, out b);
            else if (cmap == WaterfallColormap.Meshtastic) MeshtasticMap(t, out r, out g, out b);
            else InfernoMap(t, out r, out g, out b);
            _lut[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    private void EnsureColumnMap(int width, int bins)
    {
        if (_x0Map is not null && _x1Map is not null && _xMapW == width && _xMapBins == bins)
            return;

        _x0Map = new int[width];
        _x1Map = new int[width];
        _xMapW = width;
        _xMapBins = bins;
        for (int x = 0; x < width; x++)
        {
            int start = (int)((long)x * bins / width);
            int end = (int)(((long)(x + 1) * bins + width - 1) / width);
            if (start < 0) start = 0; else if (start >= bins) start = bins - 1;
            if (end <= start) end = start + 1;
            if (end > bins) end = bins;
            _x0Map[x] = start;
            _x1Map[x] = end;
        }
    }

    private unsafe void Render()
    {
        if (_bmp is null) return;

        EnsureLut();

        var floor = FloorDb;
        var ceil = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        var invRange = 255f / (float)(ceil - floor);
        var floorF = (float)floor;

        using (var fb = _bmp.Lock())
        {
            int stride = fb.RowBytes;
            byte* back = (byte*)fb.Address.ToPointer();
            int w = _w;
            int h = _h;
            int n = _binCount;

            if (_ring is null || n == 0)
            {
                for (int y = 0; y < h; y++)
                {
                    uint* dstRow = (uint*)(back + y * stride);
                    for (int x = 0; x < w; x++) dstRow[x] = 0xFF000000u;
                }
                InvalidateVisual();
                return;
            }

            EnsureColumnMap(w, n);
            var x0 = _x0Map!;
            var x1 = _x1Map!;

            fixed (uint* lut = _lut)
            fixed (float* ring = _ring)
            {
                for (int y = 0; y < h; y++)
                {
                    uint* dstRow = (uint*)(back + y * stride);
                    if (y >= _filled)
                    {
                        for (int x = 0; x < w; x++) dstRow[x] = 0xFF000000u;
                        continue;
                    }
                    int srcRow = (_head - 1 - y + _capacity) % _capacity;
                    long rowOffset = (long)srcRow * n;
                    float* src = ring + rowOffset;
                    for (int x = 0; x < w; x++)
                    {
                        float v = float.NegativeInfinity;
                        for (int sx = x0[x]; sx < x1[x]; sx++)
                        {
                            float candidate = src[sx];
                            if (!float.IsNaN(candidate) && !float.IsInfinity(candidate) && candidate > v)
                                v = candidate;
                        }
                        if (float.IsNegativeInfinity(v)) v = floorF;
                        int idx = (int)((v - floorF) * invRange);
                        if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                        dstRow[x] = lut[idx];
                    }
                }
            }
        }
        InvalidateVisual();
    }

    private static byte ToByte(float f)
    {
        if (f < 0f) f = 0f; else if (f > 1f) f = 1f;
        return (byte)(f * 255f + 0.5f);
    }

    private static void TurboMap(float t, out byte r, out byte g, out byte b)
    {
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        // Polynomial per channel. Quintic in t.
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t3 * t;
        float t5 = t4 * t;
        float fr = 0.13572138f + 4.61539260f * t - 42.66032258f * t2
                 + 132.13108234f * t3 - 152.94239396f * t4 + 59.28637943f * t5;
        float fg = 0.09140261f + 2.19418839f * t + 4.84296658f * t2
                 - 14.18503333f * t3 + 4.27729857f * t4 + 2.82956604f * t5;
        float fb = 0.10667330f + 12.64194608f * t - 60.58204836f * t2
                 + 110.36276771f * t3 - 89.90310912f * t4 + 27.34824973f * t5;
        r = ToByte(fr);
        g = ToByte(fg);
        b = ToByte(fb);
    }

    private static void InfernoMap(float t, out byte r, out byte g, out byte b)
    {
        ReadOnlySpan<int> stops = stackalloc int[]
        {
            0, 0, 0,
            40, 0, 80,
            170, 30, 80,
            240, 90, 40,
            255, 200, 40,
            255, 255, 220,
        };
        if (t <= 0f) { r = 0; g = 0; b = 0; return; }
        if (t >= 1f) { r = 255; g = 255; b = 220; return; }
        float seg = t * 5f;
        int i = (int)seg;
        float f = seg - i;
        int a = i * 3;
        int c = (i + 1) * 3;
        r = (byte)(stops[a] + (stops[c] - stops[a]) * f);
        g = (byte)(stops[a + 1] + (stops[c + 1] - stops[a + 1]) * f);
        b = (byte)(stops[a + 2] + (stops[c + 2] - stops[a + 2]) * f);
    }

    // Meshtastic-themed ramp: white -> Meshtastic green (#67EA94) -> yellow -> blue -> black.
    private static void MeshtasticMap(float t, out byte r, out byte g, out byte b)
    {
        ReadOnlySpan<int> stops = stackalloc int[]
        {
            255, 255, 255,
            103, 234, 148,
            255, 255, 0,
            0, 0, 255,
            0, 0, 0,
        };
        if (t <= 0f) { r = 255; g = 255; b = 255; return; }
        if (t >= 1f) { r = 0; g = 0; b = 0; return; }
        float seg = t * 4f;
        int i = (int)seg;
        float f = seg - i;
        int a = i * 3;
        int c = (i + 1) * 3;
        r = (byte)(stops[a] + (stops[c] - stops[a]) * f);
        g = (byte)(stops[a + 1] + (stops[c + 1] - stops[a + 1]) * f);
        b = (byte)(stops[a + 2] + (stops[c + 2] - stops[a + 2]) * f);
    }
}
