// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;

namespace MeshRF.App.Views;

public enum WaterfallColormap
{
    Turbo,
    Inferno,
    Meshtastic,
}

/// <summary>
/// Scrolling dBFS waterfall. Stores incoming spectrum frames in a ring
/// buffer at their native bin count and incrementally updates a WriteableBitmap
/// on pushes. Resizing still triggers a full re-render from the same source data,
/// so it's pixel-stable across resizes.
/// </summary>
public sealed class WaterfallView : Image
{
    public static readonly DependencyProperty FloorDbProperty =
        DependencyProperty.Register(nameof(FloorDb), typeof(double), typeof(WaterfallView),
            new PropertyMetadata(-100.0, (d, _) => ((WaterfallView)d).OnLevelsChanged()));
    public static readonly DependencyProperty CeilDbProperty =
        DependencyProperty.Register(nameof(CeilDb), typeof(double), typeof(WaterfallView),
            new PropertyMetadata(0.0, (d, _) => ((WaterfallView)d).OnLevelsChanged()));
    public static readonly DependencyProperty ColormapProperty =
        DependencyProperty.Register(nameof(Colormap), typeof(WaterfallColormap), typeof(WaterfallView),
            new PropertyMetadata(WaterfallColormap.Turbo, (d, _) => ((WaterfallView)d).OnLevelsChanged()));
    public static readonly DependencyProperty AutoLevelsProperty =
        DependencyProperty.Register(nameof(AutoLevels), typeof(bool), typeof(WaterfallView),
            new PropertyMetadata(true));
    public static readonly DependencyProperty TimeHorizontalProperty =
        DependencyProperty.Register(nameof(TimeHorizontal), typeof(bool), typeof(WaterfallView),
            new PropertyMetadata(false, (d, _) => ((WaterfallView)d).OnTimeHorizontalChanged()));
    public static readonly DependencyProperty SmoothPixelsProperty =
        DependencyProperty.Register(nameof(SmoothPixels), typeof(bool), typeof(WaterfallView),
            new PropertyMetadata(false, (d, _) => ((WaterfallView)d).OnSmoothPixelsChanged()));

    public double FloorDb { get => (double)GetValue(FloorDbProperty); set => SetValue(FloorDbProperty, value); }
    public double CeilDb  { get => (double)GetValue(CeilDbProperty);  set => SetValue(CeilDbProperty, value); }
    public WaterfallColormap Colormap { get => (WaterfallColormap)GetValue(ColormapProperty); set => SetValue(ColormapProperty, value); }
    public bool AutoLevels { get => (bool)GetValue(AutoLevelsProperty); set => SetValue(AutoLevelsProperty, value); }
    public bool SmoothPixels { get => (bool)GetValue(SmoothPixelsProperty); set => SetValue(SmoothPixelsProperty, value); }

    /// <summary>When true, time runs along the horizontal axis (left = oldest,
    /// right = newest) and frequency along the vertical axis (bottom = low).
    /// Used for the frozen last-packet snapshot so chirp sweeps stretch out
    /// horizontally instead of as cramped vertical diagonals.</summary>
    public bool TimeHorizontal { get => (bool)GetValue(TimeHorizontalProperty); set => SetValue(TimeHorizontalProperty, value); }

    /// <summary>Raised whenever AutoLevels recomputes the floor/ceil window.
    /// The owner can mirror these onto the bound sliders.</summary>
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

    // When true, DP-change callbacks for FloorDb/CeilDb/Colormap are
    // ignored. Used during a Push to coalesce multiple DP writes into a
    // single Render at the end.
    private bool _suppressRender;

    private void OnLevelsChanged()
    {
        if (_suppressRender) return;
        Render();
    }

    public WaterfallView()
    {
        Stretch = Stretch.Fill;
        SnapsToDevicePixels = true;
        ApplyBitmapScalingMode();
        SizeChanged += (_, _) => { EnsureBitmap(); Render(); };
    }

    private void OnSmoothPixelsChanged() => ApplyBitmapScalingMode();

    private void OnTimeHorizontalChanged()
    {
        if (_bmp is null)
        {
            EnsureBitmap();
            Render();
            return;
        }

        ResizeRing(GetDesiredCapacity(_w, _h));
        Render();
    }

    private void ApplyBitmapScalingMode() =>
        RenderOptions.SetBitmapScalingMode(this,
            SmoothPixels ? BitmapScalingMode.Fant : BitmapScalingMode.NearestNeighbor);

    private int GetDesiredCapacity(int width, int height)
    {
        int axis = TimeHorizontal ? width : height;
        return axis > 0 ? axis : 1;
    }

    private void EnsureBitmap()
    {
        // When this control lives on an inactive tab it gets detached from the
        // visual tree and its ActualWidth/Height collapse to 0. Once a bitmap
        // already exists, don't tear it down or shrink the ring in that case —
        // keep accumulating frames in the background so the waterfall is intact
        // when the tab is shown again. But if we have never created a bitmap
        // yet, fall through and build a default-sized one so the ring gets a
        // capacity and incoming frames aren't dropped before first layout.
        if (_bmp != null && (ActualWidth < 1 || ActualHeight < 1)) return;

        var w = (int)Math.Max(64, Math.Round(ActualWidth));
        var h = (int)Math.Max(64, Math.Round(ActualHeight));
        if (_bmp != null && _w == w && _h == h) return;

        _w = w;
        _h = h;
        _bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        Source = _bmp;
        _x0Map = null;
        _x1Map = null;
        _xMapW = 0;
        _xMapBins = 0;
        ResizeRing(GetDesiredCapacity(w, h));
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
            // Rendering assumes newest is at (_head - 1), so reversing here can
            // make the waterfall appear mirrored after width/height changes.
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

        // Coalesce: any FloorDb/CeilDb writes from auto-levels won't
        // re-render mid-push; we render once at the end.
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

        if (!TryRenderLatestRow(frame))
            Render();
    }

    /// <summary>
    /// Batch-push multiple frames efficiently. Performs a single bitmap lock/unlock
    /// and shifts rows once by the batch size, avoiding per-frame overhead.
    /// </summary>
    public void PushBatch(IReadOnlyList<float[]> frames)
    {
        if (frames.Count == 0) return;
        if (_bmp is null) EnsureBitmap();

        int bins = frames[0].Length;
        if (bins == 0) return;

        if (bins != _binCount)
        {
            _binCount = bins;
            _ring = new float[(long)_capacity * _binCount];
            _head = 0;
            _filled = 0;
            _x0Map = null;
            _x1Map = null;
            _xMapW = 0;
            _xMapBins = 0;
        }
        if (_ring is null || _capacity == 0) return;

        // Copy all frames into the ring buffer.
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Length != bins) continue;

            int dstRow = _head;
            long offset = (long)dstRow * _binCount;
            if (offset > int.MaxValue) continue;
            frame.AsSpan().CopyTo(_ring.AsSpan((int)offset, _binCount));
            _head = (_head + 1) % _capacity;
            if (_filled < _capacity) _filled++;
        }

        // Auto-levels on last frame only.
        _suppressRender = true;
        try
        {
            if (AutoLevels)
            {
                _autoFrameCounter += frames.Count;
                if (_autoFrameCounter >= 10)
                {
                    _autoFrameCounter = 0;
                    UpdateAutoLevels(frames[frames.Count - 1]);
                }
            }
        }
        finally
        {
            _suppressRender = false;
        }

        // Batch-render all new rows in one lock cycle.
        if (!TryRenderBatch(frames))
            Render();
    }

    private unsafe bool TryRenderBatch(IReadOnlyList<float[]> frames)
    {
        if (_bmp is null || TimeHorizontal) return false;
        if (_ring is null || _binCount == 0) return false;

        int w = _w;
        int h = _h;
        int n = _binCount;
        int batchSize = frames.Count;
        if (w <= 0 || h <= 0 || n <= 0 || batchSize <= 0) return false;

        EnsureLut();
        EnsureColumnMap(w, n);

        var floor = FloorDb;
        var ceil = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        var invRange = 255f / (float)(ceil - floor);
        var floorF = (float)floor;

        _bmp.Lock();
        try
        {
            int stride = _bmp.BackBufferStride;
            byte* back = (byte*)_bmp.BackBuffer.ToPointer();

            // Shift existing rows down by batchSize (not 1).
            int rowsToShift = Math.Min(_filled - batchSize, h - batchSize);
            if (rowsToShift > 0)
            {
                for (int y = h - 1; y >= batchSize; y--)
                {
                    int srcY = y - batchSize;
                    if (srcY < 0 || srcY >= rowsToShift + batchSize) continue;
                    byte* src = back + srcY * stride;
                    byte* dst = back + y * stride;
                    Buffer.MemoryCopy(src, dst, stride, stride);
                }
            }

            // Render all new rows at once (newest = row 0).
            fixed (uint* lut = _lut)
            {
                var x0 = _x0Map!;
                var x1 = _x1Map!;
                for (int fi = 0; fi < batchSize && fi < h; fi++)
                {
                    // frames[batchSize-1] is newest, goes to row 0.
                    var frame = frames[batchSize - 1 - fi];
                    if (frame.Length != n) continue;
                    uint* dstRowPtr = (uint*)(back + fi * stride);
                    for (int x = 0; x < w; x++)
                    {
                        float v = float.NegativeInfinity;
                        for (int sx = x0[x]; sx < x1[x]; sx++)
                        {
                            float candidate = frame[sx];
                            if (!float.IsNaN(candidate) && !float.IsInfinity(candidate) && candidate > v)
                                v = candidate;
                        }
                        if (float.IsNegativeInfinity(v)) v = floorF;
                        int idx = (int)((v - floorF) * invRange);
                        if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                        dstRowPtr[x] = lut[idx];
                    }
                }
            }

            // Fill any remaining unfilled rows with black.
            if (_filled < h)
            {
                for (int y = _filled; y < h; y++)
                {
                    uint* dst = (uint*)(back + y * stride);
                    for (int x = 0; x < w; x++) dst[x] = 0xFF000000u;
                }
            }

            _bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
            return true;
        }
        finally
        {
            _bmp.Unlock();
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

    private unsafe bool TryRenderLatestRow(ReadOnlySpan<float> frame)
    {
        if (_bmp is null || TimeHorizontal) return false;
        if (_ring is null || _binCount == 0 || frame.Length != _binCount) return false;

        int w = _w;
        int h = _h;
        int n = _binCount;
        if (w <= 0 || h <= 0 || n <= 0) return false;

        EnsureLut();
        EnsureColumnMap(w, n);

        var floor = FloorDb;
        var ceil = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        var invRange = 255f / (float)(ceil - floor);
        var floorF = (float)floor;

        _bmp.Lock();
        try
        {
            int stride = _bmp.BackBufferStride;
            byte* back = (byte*)_bmp.BackBuffer.ToPointer();

            int rowsToShift = Math.Min(_filled - 1, h - 1);
            for (int y = rowsToShift; y >= 1; y--)
            {
                byte* src = back + (y - 1) * stride;
                byte* dst = back + y * stride;
                Buffer.MemoryCopy(src, dst, stride, stride);
            }

            uint* dstRow0 = (uint*)back;
            fixed (uint* lut = _lut)
            {
                var x0 = _x0Map!;
                var x1 = _x1Map!;
                for (int x = 0; x < w; x++)
                {
                    float v = float.NegativeInfinity;
                    for (int sx = x0[x]; sx < x1[x]; sx++)
                    {
                        float candidate = frame[sx];
                        if (!float.IsNaN(candidate) && !float.IsInfinity(candidate) && candidate > v)
                            v = candidate;
                    }
                    if (float.IsNegativeInfinity(v)) v = floorF;
                    int idx = (int)((v - floorF) * invRange);
                    if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                    dstRow0[x] = lut[idx];
                }
            }

            if (_filled < h)
            {
                for (int y = _filled; y < h; y++)
                {
                    uint* dst = (uint*)(back + y * stride);
                    for (int x = 0; x < w; x++) dst[x] = 0xFF000000u;
                }
            }

            _bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
            return true;
        }
        finally
        {
            _bmp.Unlock();
        }
    }

    public void Clear()
    {
        _filled = 0;
        _head = 0;
        _autoFloor = FloorDb;
        _autoCeil = CeilDb;
        _autoFrameCounter = 0;
        if (_bmp is not null) Render();
    }

    /// <summary>
    /// Replaces the ring-buffer contents with a chronological snapshot
    /// (oldest -> newest) and renders once. Intended for frozen packet views.
    /// </summary>
    public void ReplaceFrames(IReadOnlyList<float[]> frames)
    {
        if (frames.Count == 0)
        {
            Clear();
            return;
        }

        if (_bmp is null) EnsureBitmap();

        int bins = frames[0]?.Length ?? 0;
        if (bins <= 0) return;

        if (bins != _binCount)
        {
            _binCount = bins;
            _ring = new float[(long)_capacity * _binCount];
            _head = 0;
            _filled = 0;
        }
        if (_ring is null || _capacity == 0) return;

        int take = Math.Min(frames.Count, _capacity);
        int start = frames.Count - take;
        _head = 0;
        _filled = 0;

        for (int i = 0; i < take; i++)
        {
            var src = frames[start + i];
            if (src is null || src.Length != _binCount) continue;

            long offset = (long)_head * _binCount;
            src.AsSpan().CopyTo(_ring.AsSpan((int)offset, _binCount));
            _head = (_head + 1) % _capacity;
            if (_filled < _capacity) _filled++;
        }

        Render();
    }

    /// <summary>
    /// Replaces the ring-buffer contents from a row-major frame grid
    /// (<paramref name="frameCount"/> rows, <paramref name="bins"/> columns).
    /// Intended for frozen packet views to avoid per-row allocations.
    /// </summary>
    public void ReplaceFrames(ReadOnlySpan<float> frames, int frameCount, int bins)
    {
        if (frameCount <= 0 || bins <= 0)
        {
            Clear();
            return;
        }

        long required = (long)frameCount * bins;
        if (required > frames.Length) return;

        if (_bmp is null) EnsureBitmap();

        if (bins != _binCount)
        {
            _binCount = bins;
            _ring = new float[(long)_capacity * _binCount];
            _head = 0;
            _filled = 0;
        }
        if (_ring is null || _capacity == 0) return;

        int take = Math.Min(frameCount, _capacity);
        int start = frameCount - take;
        _head = 0;
        _filled = 0;

        for (int i = 0; i < take; i++)
        {
            int srcOffset = (start + i) * bins;
            long dstOffset = (long)_head * _binCount;
            frames.Slice(srcOffset, bins).CopyTo(_ring.AsSpan((int)dstOffset, _binCount));
            _head = (_head + 1) % _capacity;
            if (_filled < _capacity) _filled++;
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
        _autoCeil  = _autoCeil  * (1 - a) + newCeil  * a;

        FloorDb = _autoFloor;
        CeilDb  = _autoCeil;
        AutoLevelsChanged?.Invoke(_autoFloor, _autoCeil);
    }

    // 256-entry BGRA color LUT, rebuilt when the colormap changes.
    private uint[] _lut = new uint[256];
    private WaterfallColormap _lutMap = (WaterfallColormap)(-1);

    private void EnsureLut()
    {
        var cmap = Colormap;
        if (cmap == _lutMap) return;
        _lutMap = cmap;
        for (int i = 0; i < 256; i++)
        {
            float t = i / 255f;
            byte r, g, b;
            if (cmap == WaterfallColormap.Turbo) TurboMap(t, out r, out g, out b);
            else if (cmap == WaterfallColormap.Meshtastic) MeshtasticMap(t, out r, out g, out b);
            else                                 InfernoMap(t, out r, out g, out b);
            _lut[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    private void Render()
    {
        if (_bmp is null) return;

        EnsureLut();

        var floor = FloorDb;
        var ceil = CeilDb;
        if (ceil <= floor) ceil = floor + 1.0;
        var invRange = 255f / (float)(ceil - floor);
        var floorF = (float)floor;

        _bmp.Lock();
        try
        {
            unsafe
            {
                int stride = _bmp.BackBufferStride;
                byte* back = (byte*)_bmp.BackBuffer.ToPointer();
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
                    _bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
                    return;
                }

                if (TimeHorizontal)
                {
                    RenderTimeHorizontal(back, stride, w, h, n, floorF, invRange);
                    _bmp.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
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
            _bmp.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
        }
        finally
        {
            _bmp.Unlock();
        }
    }

    // Transposed render: x axis = time (left = oldest, right = newest),
    // y axis = frequency (bottom = low bin, top = high bin). LoRa chirp
    // sweeps therefore stretch across the panel width instead of appearing
    // as cramped vertical diagonals.
    private unsafe void RenderTimeHorizontal(
        byte* back, int stride, int w, int h, int n, float floorF, float invRange)
    {
        if (_filled <= 0)
        {
            for (int y = 0; y < h; y++)
            {
                uint* blank = (uint*)(back + y * stride);
                for (int x = 0; x < w; x++) blank[x] = 0xFF000000u;
            }
            return;
        }

        // Oldest valid row in chronological order.
        int oldestRow = (_head - _filled + _capacity) % _capacity;

        // Precompute frequency bin for each output row (top = high freq).
        Span<int> myStack = stackalloc int[Math.Min(h, 4096)];
        int[]? heap = h > myStack.Length ? new int[h] : null;
        Span<int> my = heap ?? myStack;
        for (int y = 0; y < h; y++)
        {
            int sy = (int)((long)(h - 1 - y) * n / h);
            if (sy < 0) sy = 0; else if (sy >= n) sy = n - 1;
            my[y] = sy;
        }

        Span<int> t0Stack = stackalloc int[Math.Min(w, 4096)];
        Span<int> t1Stack = stackalloc int[Math.Min(w, 4096)];
        int[]? t0Heap = w > t0Stack.Length ? new int[w] : null;
        int[]? t1Heap = w > t1Stack.Length ? new int[w] : null;
        Span<int> t0 = t0Heap ?? t0Stack;
        Span<int> t1 = t1Heap ?? t1Stack;
        for (int x = 0; x < w; x++)
        {
            int start = (int)((long)x * _filled / w);
            int end = (int)(((long)(x + 1) * _filled + w - 1) / w);
            if (start < 0) start = 0; else if (start >= _filled) start = _filled - 1;
            if (end <= start) end = start + 1;
            if (end > _filled) end = _filled;
            t0[x] = start;
            t1[x] = end;
        }

        fixed (uint* lut = _lut)
        fixed (float* ring = _ring)
        {
            for (int x = 0; x < w; x++)
            {
                int start = t0[x];
                int end = t1[x];
                for (int y = 0; y < h; y++)
                {
                    float v = float.NegativeInfinity;
                    int bin = my[y];
                    for (int t = start; t < end; t++)
                    {
                        int srcRow = (oldestRow + t) % _capacity;
                        float candidate = ring[(long)srcRow * n + bin];
                        if (!float.IsNaN(candidate) && !float.IsInfinity(candidate) &&
                            candidate > v)
                            v = candidate;
                    }
                    if (float.IsNegativeInfinity(v)) v = floorF;
                    int idx = (int)((v - floorF) * invRange);
                    if (idx < 0) idx = 0; else if (idx > 255) idx = 255;
                    ((uint*)(back + y * stride))[x] = lut[idx];
                }
            }
        }
    }

    // Google Turbo polynomial approximation
    // (from Anton Mikhailov, "Turbo, An Improved Rainbow Colormap for
    //  Visualization", Google AI Blog 2019; coefficients per
    //  https://gist.github.com/mikhailov-work/0d177465a8151eb6ede1768d51d476c7).
    private static void TurboMap(float t, out byte r, out byte g, out byte b)
    {
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        // Polynomial per channel. Quintic in t.
        float t2 = t * t;
        float t3 = t2 * t;
        float t4 = t3 * t;
        float t5 = t4 * t;
        float fr = 0.13572138f + 4.61539260f * t  - 42.66032258f * t2
                 + 132.13108234f * t3 - 152.94239396f * t4 + 59.28637943f * t5;
        float fg = 0.09140261f + 2.19418839f * t  +  4.84296658f * t2
                 -  14.18503333f * t3 +   4.27729857f * t4 +  2.82956604f * t5;
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
            0,0,0,
            40,0,80,
            170,30,80,
            240,90,40,
            255,200,40,
            255,255,220,
        };
        if (t <= 0f) { r = 0; g = 0; b = 0; return; }
        if (t >= 1f) { r = 255; g = 255; b = 220; return; }
        float seg = t * 5f;
        int i = (int)seg;
        float f = seg - i;
        int a = i * 3;
        int c = (i + 1) * 3;
        r = (byte)(stops[a]     + (stops[c]     - stops[a])     * f);
        g = (byte)(stops[a + 1] + (stops[c + 1] - stops[a + 1]) * f);
        b = (byte)(stops[a + 2] + (stops[c + 2] - stops[a + 2]) * f);
    }

    // Meshtastic-themed ramp: white -> Meshtastic green -> yellow -> blue -> black,
    // running from the dB floor (t=0) to the dB ceil (t=1). Meshtastic brand
    // green is #67EA94.
    private static void MeshtasticMap(float t, out byte r, out byte g, out byte b)
    {
        ReadOnlySpan<int> stops = stackalloc int[]
        {
            255,255,255, // white
            103,234,148, // Meshtastic green (#67EA94)
            255,255,0,   // yellow
            0,0,255,     // blue
            0,0,0,       // black
        };
        if (t <= 0f) { r = 255; g = 255; b = 255; return; }
        if (t >= 1f) { r = 0; g = 0; b = 0; return; }
        float seg = t * 4f;
        int i = (int)seg;
        float f = seg - i;
        int a = i * 3;
        int c = (i + 1) * 3;
        r = (byte)(stops[a]     + (stops[c]     - stops[a])     * f);
        g = (byte)(stops[a + 1] + (stops[c + 1] - stops[a + 1]) * f);
        b = (byte)(stops[a + 2] + (stops[c + 2] - stops[a + 2]) * f);
    }

    private static byte ToByte(float v)
    {
        var s = v * 255f;
        if (s < 0f) return 0;
        if (s > 255f) return 255;
        return (byte)s;
    }
}
