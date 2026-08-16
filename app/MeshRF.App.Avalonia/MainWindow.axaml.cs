// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF.Channels;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

public partial class MainWindow : Window
{
    private readonly RadioViewModel _viewModel = new();
    private readonly DispatcherTimer _spectrumTimer;
    private float[] _spectrumBuffer = Array.Empty<float>();

    // Waterfall row pacing state — mirrors MeshRF.App's MainWindow.xaml.cs
    // AdvanceWaterfall: paces the scroll speed against actual received
    // signal time (via PullSpectrumFrames/SpectrumFrameCount) rather than UI
    // frames, so "Speed" (rows/sec) means the same thing regardless of poll
    // rate. Completed rows are max-hold accumulated from the native frames
    // that make up one row's time slice.
    //
    // MaxFramesToPull and the poll rate below are not independently tunable:
    // the native side (Core.cpp's kWaterfallMaxFramesToPull/kWaterfallTargetFps)
    // decimates its history-frame stride assuming a consumer that pulls up to
    // 64 frames per call at 60 Hz (i.e. drains up to 3840 frames/sec). Polling
    // slower than that starves the ring buffer, which was the root cause of
    // the waterfall looking both "too slow" and "too zoomed out" — falling
    // behind constantly triggered the overflow skip-ahead path below,
    // dropping elapsed time instead of rendering it.
    // Scroll-speed bounds, and UI policy rather than a pipeline limit. The hard
    // ceiling is the native history frame rate — min(sample_rate / 1024, 3840)
    // Hz after Core.cpp's compute_history_frame_stride decimation — because
    // framesPerRow bottoms out at 1 and no setting can emit rows faster than
    // frames arrive. That is ~2340/s at 2.4 MS/s, so this cap is well under it;
    // it is set by what stays readable, since at 60 fps 480/s already advances
    // eight rows per drawn frame.
    private const double MinWaterfallRowsPerSecond = 5.0;
    private const double MaxWaterfallRowsPerSecond = 480.0;
    private const int MaxFramesToPull = 64;
    private const int NativeFrameRingCapacity = 256; // Must match kFrameRingCapacity in C++.
    private ulong _wfLastFrameCount;
    private float[] _wfFrameBuffer = Array.Empty<float>();
    private float[] _wfRowAccum = Array.Empty<float>();
    private bool _wfRowAccumValid;
    private int _wfAccumFrames;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // DragOver/Drop are attached events, so they are subscribed here rather
        // than in the markup.
        MainTabs.AddHandler(DragDrop.DragOverEvent, OnTabsDragOver);
        MainTabs.AddHandler(DragDrop.DropEvent, OnTabsDrop);

        // handledEventsToo, and so not a PointerPressed="..." attribute in the
        // markup: TabControl derives from SelectingItemsControl, whose own
        // pointer-press handler selects the tab and marks the event handled.
        // That runs as a class handler on this same element, ahead of any
        // instance handler, so a plain bubbling subscription is simply never
        // invoked and no drag can ever start. Subscribing this way lets the
        // selection happen first and still gives us the press.
        MainTabs.AddHandler(InputElement.PointerPressedEvent, OnTabsPointerPressed,
                            RoutingStrategies.Bubble, handledEventsToo: true);

        // Restore window geometry / splitter proportions before first show.
        ApplyLayout(AppSettings.Load());

        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60 Hz — matches the native pull-rate assumption above.
        };
        _spectrumTimer.Tick += (_, _) => PullSpectrum();
        _spectrumTimer.Start();

        _viewModel.PacketDecoded += OnPacketDecoded;

        // Capture layout while the visual tree is still alive; Closed fires
        // after teardown, when the grids' measured sizes are gone.
        Closing += (_, _) => SaveLayout();
        Closed += (_, _) =>
        {
            _spectrumTimer.Stop();
            _viewModel.Dispose();
        };
    }

    private void PullSpectrum()
    {
        var core = _viewModel.Core;
        if (core is null || !_viewModel.IsRunning)
        {
            _wfRowAccumValid = false;
            _wfAccumFrames = 0;
            return;
        }

        var n = core.SpectrumSize;
        if (n <= 0) return;
        if (_spectrumBuffer.Length != n) _spectrumBuffer = new float[n];

        var rate = core.SampleRateHz;
        if (rate > 0) _viewModel.SpectrumSpanHz = rate;
        var centre = core.SpectrumCenterHz;
        if (centre > 0) _viewModel.SpectrumCenterHz = centre;

        var written = core.PullSpectrum(_spectrumBuffer);
        if (written > 0)
            Spectrum.Update(_spectrumBuffer.AsSpan(0, written));

        AdvanceWaterfall(core, rate, n);

        // One render per tick, after every row for this tick is in. Waterfall
        // rows are paced against received signal time, so a fast setting pushes
        // several per tick; rendering each one cost a full rasterization of the
        // control and only the last was ever seen.
        Waterfall.RenderIfDirty();
    }

    private void AdvanceWaterfall(MeshtasticCore core, uint sampleRate, int bins)
    {
        ulong frameCount = core.SpectrumFrameCount;

        // First read or pipeline (re)start: re-baseline without scrolling.
        if (_wfLastFrameCount == 0 || frameCount < _wfLastFrameCount)
        {
            _wfLastFrameCount = frameCount;
            _wfRowAccumValid = false;
            _wfAccumFrames = 0;
            return;
        }
        if (frameCount <= _wfLastFrameCount) return; // No new frames.

        // Too far behind (ring buffer overflow) — skip ahead rather than pull stale frames.
        ulong behind = frameCount - _wfLastFrameCount;
        if (behind > (ulong)(NativeFrameRingCapacity - MaxFramesToPull))
        {
            _wfLastFrameCount = frameCount - (ulong)(NativeFrameRingCapacity / 2);
            _wfRowAccumValid = false;
            _wfAccumFrames = 0;
        }

        double rowsPerSecond = Math.Clamp(_viewModel.WaterfallRowsPerSecond,
                                          MinWaterfallRowsPerSecond, MaxWaterfallRowsPerSecond);
        int framesPerRow = 1;
        if (sampleRate > 0 && bins > 0)
        {
            double historyFrameRate = core.SpectrumHistoryFrameRateHz;
            if (historyFrameRate <= 0) historyFrameRate = (double)sampleRate / bins;
            framesPerRow = (int)Math.Round(historyFrameRate / rowsPerSecond);
            if (framesPerRow < 1) framesPerRow = 1;
        }

        int bufferSize = MaxFramesToPull * bins;
        if (_wfFrameBuffer.Length < bufferSize) _wfFrameBuffer = new float[bufferSize];

        int framesPulled = core.PullSpectrumFrames(_wfFrameBuffer, _wfLastFrameCount, MaxFramesToPull);
        if (framesPulled == 0) return;
        _wfLastFrameCount += (ulong)framesPulled;

        for (int frameIdx = 0; frameIdx < framesPulled; frameIdx++)
        {
            var frame = _wfFrameBuffer.AsSpan(frameIdx * bins, bins);
            // Snapshot history takes RAW frames, not the max-held rows below: a
            // chirp sweeps its bandwidth in a few ms, so a row that max-holds
            // tens of ms is hot at every frequency and renders as a solid bar.
            AppendSnapshotHistory(frame);
            if (!_wfRowAccumValid || _wfRowAccum.Length != bins)
            {
                if (_wfRowAccum.Length != bins) _wfRowAccum = new float[bins];
                frame.CopyTo(_wfRowAccum);
                _wfRowAccumValid = true;
            }
            else
            {
                for (int i = 0; i < bins; i++)
                    if (frame[i] > _wfRowAccum[i]) _wfRowAccum[i] = frame[i];
            }
            _wfAccumFrames++;

            if (_wfAccumFrames >= framesPerRow)
            {
                Waterfall.Push(_wfRowAccum);
                _wfAccumFrames = 0;
                _wfRowAccumValid = false;
            }
        }
    }

    // Rolling history of RAW spectrum frames for the frozen snapshot, kept at
    // full time resolution so chirp structure survives. At ~2000 frames/s this
    // holds roughly a second — long enough to contain a packet, and the burst
    // crop below trims it to just the packet.
    private const int HistoryFrames = 2048;
    private float[][]? _specHistoryRing;
    private int _specHistoryWrite;
    private int _specHistoryCount;
    private int _specHistoryBinCount;

    private void AppendSnapshotHistory(ReadOnlySpan<float> frame)
    {
        if (_specHistoryRing is null || _specHistoryBinCount != frame.Length)
        {
            _specHistoryBinCount = frame.Length;
            _specHistoryRing = new float[HistoryFrames][];
            for (int i = 0; i < HistoryFrames; i++) _specHistoryRing[i] = new float[_specHistoryBinCount];
            _specHistoryWrite = 0;
            _specHistoryCount = 0;
        }
        frame.CopyTo(_specHistoryRing[_specHistoryWrite]);
        _specHistoryWrite = (_specHistoryWrite + 1) % HistoryFrames;
        if (_specHistoryCount < HistoryFrames) _specHistoryCount++;
    }

    /// <summary>Trim the row window down to the burst. Each row's peak is
    /// compared against the window's median peak (the noise floor); rows more
    /// than 6 dB above it are considered signal. Returns the input unchanged if
    /// no clear burst stands out, so a marginal packet still shows something.</summary>
    private static List<float[]> CropToBurst(List<float[]> rows)
    {
        if (rows.Count < 4) return rows;

        var peaks = new float[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            float peak = float.NegativeInfinity;
            foreach (var v in rows[i])
                if (!float.IsNaN(v) && !float.IsInfinity(v) && v > peak) peak = v;
            peaks[i] = float.IsNegativeInfinity(peak) ? 0f : peak;
        }

        var sorted = (float[])peaks.Clone();
        Array.Sort(sorted);
        float noise = sorted[sorted.Length / 2];
        float threshold = noise + 6f;

        int first = -1, last = -1;
        for (int i = 0; i < peaks.Length; i++)
        {
            if (peaks[i] < threshold) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0 || last <= first) return rows;

        const int pad = 2;
        first = Math.Max(0, first - pad);
        last = Math.Min(rows.Count - 1, last + pad);
        return rows.GetRange(first, last - first + 1);
    }

    /// <summary>Thin a burst down to about one frame per output pixel. The
    /// renderer max-holds whatever extra frames land in a column, which
    /// re-smears the chirp — the same averaging that made the snapshot a solid
    /// bar, just at column scale. Picking nearest-neighbour frames keeps each
    /// column a single instant in time, so the sweep stays sharp.</summary>
    private static List<float[]> ThinToWidth(List<float[]> frames, int targetColumns)
    {
        if (targetColumns <= 0 || frames.Count <= targetColumns) return frames;

        var thinned = new List<float[]>(targetColumns);
        for (int i = 0; i < targetColumns; i++)
        {
            int src = (int)((long)i * frames.Count / targetColumns);
            thinned.Add(frames[Math.Min(src, frames.Count - 1)]);
        }
        return thinned;
    }

    /// <summary>Oldest-first history row, index 0 = oldest retained.</summary>
    private float[] GetHistoryFrameByAge(int index)
    {
        int oldest = (_specHistoryWrite - _specHistoryCount + HistoryFrames) % HistoryFrames;
        return _specHistoryRing![(oldest + index) % HistoryFrames];
    }

    // Ping-pong pool for the packet-snapshot spectrogram grid: LastPacket keeps
    // the array it was handed for later re-renders, so the next pull must fill
    // the other one rather than overwrite what's on screen.
    private readonly float[]?[] _snapshotGridPool = new float[2][];
    private int _snapshotGridPoolIndex;
    // Reusable BGRA buffer for the off-thread snapshot rasterization; consumed
    // by the single blit in PresentSnapshot before the next pull reuses it.
    private uint[] _snapshotPixelBuffer = Array.Empty<uint>();
    private int _snapshotInFlight;

    // A CRC-valid packet just decoded. A bad frame or a false positive
    // (preamble that never decodes) never reaches here, so the last-packet
    // panel only ever shows genuine packets. The whole packet is already
    // buffered in the native IQ ring by the time it decodes, so we snapshot
    // immediately — any extra delay just ages the packet toward the far end of
    // the ring and risks the preamble scrolling out.
    private void OnPacketDecoded()
    {
        if (!_lastPacketExpanded) return; // Collapsed: don't pay for a snapshot nobody sees.
        if (Interlocked.Exchange(ref _snapshotInFlight, 1) != 0) return;
        _ = FreezeLastPacketAsync();
    }

    // Collapsed state for the last-packet panel, persisted under the same
    // settings.json key MeshRF.App uses. _lastPacketSavedHeight remembers the
    // expanded row height (which the user can drag) across a collapse.
    private bool _lastPacketExpanded = true;
    private GridLength _lastPacketSavedHeight = new(84);

    private void OnLastPacketToggle(object? sender, PointerReleasedEventArgs e) =>
        ApplyLastPacketExpandedState(!_lastPacketExpanded, persist: true);

    /// <summary>Show or hide the spectrogram, leaving the header strip visible
    /// as the affordance to bring it back. Collapsed the row goes to Auto so it
    /// shrinks to the header and gives its space to the live waterfall.</summary>
    private void ApplyLastPacketExpandedState(bool expanded, bool persist)
    {
        _lastPacketExpanded = expanded;
        var row = WaterfallStackGrid.RowDefinitions[2];

        if (_lastPacketExpanded)
        {
            LastPacket.IsVisible = true;
            row.Height = _lastPacketSavedHeight;
            row.MinHeight = 64;
            LastPacketToggleIcon.Text = "▼";
        }
        else
        {
            _lastPacketSavedHeight = row.Height;
            row.Height = GridLength.Auto;
            row.MinHeight = 0;
            LastPacket.IsVisible = false;
            LastPacketToggleIcon.Text = "▶";
        }

        if (!persist) return;
        var settings = AppSettings.Load();
        settings.LastPacketExpanded = _lastPacketExpanded;
        settings.Save();
    }

    /// <summary>Snapshots the last decoded packet as a high-time-resolution
    /// STFT computed natively from buffered modem-rate IQ, cropped (zoomed) to
    /// the LoRa channel so the individual chirps are visible. Mirrors
    /// MeshRF.App's FreezeLastPacketAsync.
    ///
    /// PullPacketSpectrogram is CPU-heavy (IQ ring copy + energy locator FFTs +
    /// STFT) and so is the bicubic rasterize that follows it, so both run on a
    /// thread-pool thread; only the final blit touches the UI thread.</summary>
    private async Task FreezeLastPacketAsync()
    {
        try
        {
            var core = _viewModel.Core;
            if (core is null) return;

            // Size the grid from the LoRa parameters: slow modes (high SF, low
            // BW) need many more STFT frames to hold the full packet.
            // STFT is a 512-point FFT with a 128-sample hop; at modem rate
            // (BW * 4 oversampling) a symbol is 2^SF * 4 samples.
            const int kFft = 512;
            const int kHop = 128;
            const int nFreq = 256;

            int sf = Math.Clamp((int)_viewModel.OverrideSf, 7, 12);
            double bwHz = Math.Max(7_800.0, _viewModel.OverrideBwKhz * 1000.0);
            double symbolSamples = (1 << sf) * 4.0;

            // 16 preamble + 4.25 sync + 8 header + 280 payload symbols; for
            // SF12/125k a 255-byte packet is ~280 symbols ≈ 9 seconds.
            double maxSamples = (16.0 + 4.25 + 8.0 + 280.0) * symbolSamples;
            int nTime = Math.Max(2048, (int)Math.Ceiling((maxSamples - kFft) / kHop) + 1);
            nTime = Math.Min(nTime, 16384); // Cap to avoid huge allocations.

            // Read presentation parameters on the UI thread up front so the
            // whole pull + contrast + rasterize pipeline can run off-thread.
            var (targetW, targetH) = LastPacket.GetSnapshotTargetSize();
            bool timeHorizontal = LastPacket.TimeHorizontal;
            var colormap = _viewModel.WaterfallColormap;
            int poolIndex = _snapshotGridPoolIndex ^ 1;

            (int rows, float[] grid, double floor, double ceil) PullAndRasterize()
            {
                var grid = _snapshotGridPool[poolIndex];
                if (grid is null || grid.Length < nTime * nFreq)
                    _snapshotGridPool[poolIndex] = grid = new float[nTime * nFreq];

                int written = core.PullPacketSpectrogram(grid, nTime, nFreq);
                if (written <= 0) return (0, Array.Empty<float>(), -100.0, 0.0);

                var (floor, ceil) = ComputeContrastLevels(grid.AsSpan(0, written * nFreq));

                if (_snapshotPixelBuffer.Length < targetW * targetH)
                    _snapshotPixelBuffer = new uint[targetW * targetH];
                WaterfallView.RenderScaledSnapshotPixels(
                    grid, written, nFreq, targetW, targetH, timeHorizontal,
                    floor, ceil, colormap, _snapshotPixelBuffer);
                return (written, grid, floor, ceil);
            }

            var (rows, grid, floor, ceil) = await Task.Run(PullAndRasterize).ConfigureAwait(true);

            // The very first packet after RX start can arrive before enough IQ
            // history has accumulated for a robust native snapshot. Retry once
            // shortly after decode before falling back.
            if (rows <= 0)
            {
                await Task.Delay(ComputeRetryDelayMs()).ConfigureAwait(true);
                (rows, grid, floor, ceil) = await Task.Run(PullAndRasterize).ConfigureAwait(true);
            }

            if (rows <= 0)
            {
                FreezeLastPacketFromHistory();
                return;
            }

            LastPacket.PresentSnapshot(
                floor, ceil, colormap,
                _snapshotPixelBuffer, targetW, targetH,
                grid, rows, nFreq);
            _snapshotGridPoolIndex = poolIndex;
            LastPacketTitle.Text = $"Last packet  {UiFormats.Stamp(DateTime.Now)}";
        }
        finally
        {
            Interlocked.Exchange(ref _snapshotInFlight, 0);
        }
    }

    /// <summary>Base the retry wait on LoRa symbol time so slow modes (high SF
    /// / low BW) wait longer to accumulate history for the first packet, with
    /// hard bounds for UI responsiveness.</summary>
    private int ComputeRetryDelayMs()
    {
        int sf = Math.Clamp((int)_viewModel.OverrideSf, 5, 12);
        double bwHz = Math.Max(7_800.0, _viewModel.OverrideBwKhz * 1000.0);
        double symbolMs = ((1 << sf) / bwHz) * 1000.0;
        return Math.Clamp((int)Math.Round(symbolMs * 24.0), 80, 900);
    }

    /// <summary>Robust display levels for a snapshot (5th/99.5th percentile
    /// with a small margin and a 24 dB minimum span) so frozen packets stay
    /// high-contrast regardless of the live waterfall's levels. O(n log n) —
    /// call it off the UI thread.</summary>
    private static (double floor, double ceil) ComputeContrastLevels(ReadOnlySpan<float> values)
    {
        if (values.Length < 16) return (-100.0, 0.0);

        var vals = new float[values.Length];
        int valid = 0;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            vals[valid++] = v;
        }
        if (valid < 16) return (-100.0, 0.0);

        Array.Sort(vals, 0, valid);
        float p05 = vals[(int)Math.Clamp(Math.Round((valid - 1) * 0.05), 0, valid - 1)];
        float p995 = vals[(int)Math.Clamp(Math.Round((valid - 1) * 0.995), 0, valid - 1)];

        double floor = p05 - 2.0;
        double ceil = p995 + 2.0;
        if (ceil - floor < 24.0) ceil = floor + 24.0;
        return (floor, ceil);
    }

    /// <summary>Fallback when the native IQ ring can't produce a spectrogram:
    /// replay the app-side spectrum history, cropped (zoomed) to the LoRa
    /// channel around DC. Much lower time resolution than the native STFT, so
    /// the burst also needs cropping and thinning to stay legible.</summary>
    private void FreezeLastPacketFromHistory()
    {
        var core = _viewModel.Core;
        if (core is null || _specHistoryRing is null || _specHistoryCount == 0) return;

        int bins = _specHistoryBinCount;
        if (bins <= 0) return;

        // Zoom to roughly 1.4x the 250 kHz channel so the chirp fills the panel
        // instead of being a sliver in the middle of the full span.
        const double zoomHz = 350_000.0;
        double spanHz = _viewModel.SpectrumSpanHz > 0 ? _viewModel.SpectrumSpanHz
                      : core.SampleRateHz > 0 ? core.SampleRateHz
                      : 2_400_000.0;
        int half = Math.Clamp((int)Math.Round(zoomHz / spanHz * bins / 2.0), 16, bins / 2);
        int lo = bins / 2 - half, width = half * 2;

        // Slice the zoom window out of every retained row first.
        var all = new List<float[]>(_specHistoryCount);
        for (int i = 0; i < _specHistoryCount; i++)
        {
            var slice = new float[width];
            Array.Copy(GetHistoryFrameByAge(i), lo, slice, 0, width);
            all.Add(slice);
        }

        // The retained window is ~1-2s of rows but a packet is a few hundred ms,
        // so showing all of them leaves the burst as a narrow off-centre sliver.
        // Crop to the rows that actually carry signal (peak well above the
        // window's noise floor), padded slightly so the edges stay visible.
        var frames = CropToBurst(all);

        // Hand the view roughly one frame per pixel column. The bicubic
        // resample takes 4 taps around each output pixel, so surplus frames
        // aren't averaged in — they're skipped, and the sweep aliases.
        int columns = (int)Math.Round(LastPacket.Bounds.Width);
        if (columns > 0) frames = ThinToWidth(frames, columns);

        LastPacket.ReplaceFrames(frames);
        LastPacketTitle.Text = $"Last packet  {UiFormats.Stamp(DateTime.Now)}";
    }

    private void OnOpenRawJsonLog(object? sender, RoutedEventArgs e) =>
        RawJsonFeedWindow.Show(this, _viewModel);

    // ----- History windows -----

    private void OnOpenNodeTelemetryHistory(object? sender, RoutedEventArgs e)
    {
        if (NodesGridProxy.SelectedItem is not NodeRecord node) return;
        TelemetryHistoryWindow.Show(this, _viewModel.HistoryConversationFor(node.NodeNum));
    }

    private void OnOpenNodeLocationHistory(object? sender, RoutedEventArgs e)
    {
        if (NodesGridProxy.SelectedItem is not NodeRecord node) return;
        LocationHistoryWindow.Show(this, _viewModel.HistoryConversationFor(node.NodeNum));
    }

    private void OnOpenSelfTelemetryHistory(object? sender, RoutedEventArgs e) =>
        TelemetryHistoryWindow.Show(this, _viewModel.HistoryConversationFor(_viewModel.MyNodeNumber));

    private void OnOpenSelfLocationHistory(object? sender, RoutedEventArgs e) =>
        LocationHistoryWindow.Show(this, _viewModel.HistoryConversationFor(_viewModel.MyNodeNumber));

    // ----- Quick send: pick a destination first, like MeshRF.App -----

    /// <summary>Prompts for a channel (or an open DM peer) and runs
    /// <paramref name="send"/> against it. Returns without sending if the
    /// picker is cancelled.</summary>
    private async Task SendPromptedAsync(string prompt,
                                         Func<ChannelConfig?, uint?, Task> send)
    {
        var dest = await ChannelPickerWindow.PickAsync(this, _viewModel, prompt);
        if (dest is null) return;
        await send(dest.Value.Channel, dest.Value.DmNodeNum);
    }

    private async void OnSendNodeInfoPrompted(object? sender, RoutedEventArgs e) =>
        await SendPromptedAsync("Send node info on which channel?", _viewModel.SendNodeInfoOnChannelAsync);

    private async void OnSendPositionPrompted(object? sender, RoutedEventArgs e) =>
        await SendPromptedAsync("Send position on which channel?", _viewModel.SendPositionOnChannelAsync);

    private async void OnSendDeviceMetricsPrompted(object? sender, RoutedEventArgs e) =>
        await SendPromptedAsync("Send device metrics on which channel?", _viewModel.SendDeviceMetricsOnChannelAsync);

    private async void OnSendEnvironmentMetricsPrompted(object? sender, RoutedEventArgs e) =>
        await SendPromptedAsync("Send environment telemetry on which channel?", _viewModel.SendEnvironmentMetricsOnChannelAsync);

    private async void OnSendAirQualityMetricsPrompted(object? sender, RoutedEventArgs e) =>
        await SendPromptedAsync("Send air quality telemetry on which channel?", _viewModel.SendAirQualityMetricsOnChannelAsync);

    private async void OnSendNodeStatusPrompted(object? sender, RoutedEventArgs e) =>
        await SendPromptedAsync("Send status on which channel?", _viewModel.SendNodeStatusOnChannelAsync);

    /// <summary>Centers the map on the selected node's last known position.</summary>
    private void OnShowOnMap(object? sender, RoutedEventArgs e)
    {
        if (NodesGridProxy.SelectedItem is not NodeRecord node) return;
        if (node.Latitude is not double lat || node.Longitude is not double lon)
        {
            _viewModel.StatusText = "That node has no known position.";
            return;
        }
        Map.CenterOn(lat, lon);
    }

    private async void OnCopyNode(object? sender, RoutedEventArgs e)
    {
        if (NodesGridProxy.SelectedItem is not NodeRecord node) return;
        var text = $"{node.DisplayId}\t{node.LongName}\t{node.ShortName}";
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    /// <summary>Double-clicking a node row opens its DM tab, same as MeshRF.App.</summary>
    private void OnNodeDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (NodesGridProxy.SelectedItem is not NodeRecord node) return;
        _viewModel.MessageNodeCommand.Execute(node);
    }

    private async void OnDeleteNodes(object? sender, RoutedEventArgs e) => await ConfirmAndDeleteNodesAsync();

    private async void OnNodesGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        await ConfirmAndDeleteNodesAsync();
    }

    private async Task ConfirmAndDeleteNodesAsync()
    {
        var nodes = NodesGridProxy.SelectedItems.OfType<NodeRecord>().ToList();
        if (nodes.Count == 0) return;

        string message = nodes.Count == 1
            ? $"Delete node \"{NodeLabel(nodes[0])}\"?\n\nThis removes it from the node list. It cannot be undone."
            : $"Delete {nodes.Count} nodes?\n\nThis removes them from the node list. It cannot be undone.";
        if (!await ConfirmDialog.ConfirmAsync(this, nodes.Count == 1 ? "Delete node" : "Delete nodes", message))
            return;

        foreach (var node in nodes)
            _viewModel.DeleteNodeCommand.Execute(node);
    }

    private static string NodeLabel(NodeRecord node) =>
        string.IsNullOrEmpty(node.LongName) ? node.DisplayId : node.LongName;

    private async void OnDeleteWaypoints(object? sender, RoutedEventArgs e) => await ConfirmAndDeleteWaypointsAsync();

    private async void OnWaypointsGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        await ConfirmAndDeleteWaypointsAsync();
    }

    private async Task ConfirmAndDeleteWaypointsAsync()
    {
        var waypoints = WaypointsGridProxy.SelectedItems.OfType<WaypointRecord>().ToList();
        if (waypoints.Count == 0) return;

        string message = waypoints.Count == 1
            ? $"Delete waypoint \"{waypoints[0].DisplayName}\"?\n\nThis cannot be undone."
            : $"Delete {waypoints.Count} waypoints?\n\nThis cannot be undone.";
        if (!await ConfirmDialog.ConfirmAsync(this, waypoints.Count == 1 ? "Delete waypoint" : "Delete waypoints", message))
            return;

        // Sequential, not fire-and-forget: DeleteWaypoint transmits an expire
        // broadcast per waypoint, so overlapping them would race the radio.
        foreach (var wp in waypoints)
            await _viewModel.DeleteWaypointCommand.ExecuteAsync(wp);
    }

    private async void OnEditWaypoint(object? sender, RoutedEventArgs e)
    {
        if (WaypointsGridProxy.SelectedItem is not WaypointRecord wp) return;
        var result = await WaypointEditWindow.EditAsync(this, wp, _viewModel.MyNodeNumber);
        if (result is null) return;
        await _viewModel.UpdateWaypointAsync(wp, result);
    }

    /// <summary>"React…" — pick a glyph, then send it as a tapback.</summary>
    private async void OnReactPick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelMessage message }) return;
        var glyph = await EmojiPickerWindow.PickAsync(this);
        if (string.IsNullOrEmpty(glyph)) return;
        await _viewModel.SendReactionAsync(message, glyph);
    }

    /// <summary>Tapping an existing reaction sends the same one, so agreeing
    /// with a tapback doesn't require picking the emoji again.</summary>
    private async void OnReactionChipTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not MessageReaction reaction) return;
        if (string.IsNullOrEmpty(reaction.Emoji)) return;

        // Tag carries the message; fall back to the containing row in case the
        // ancestor binding didn't resolve.
        var message = button.Tag as ChannelMessage
                      ?? button.FindAncestorOfType<ListBoxItem>()?.DataContext as ChannelMessage;
        if (message is null) return;

        // A tapback is per-person: reacting twice with the same emoji adds
        // nothing, so say that instead of silently doing nothing.
        if (message.HasReactionFrom(reaction.Emoji, _viewModel.MyDisplayName))
        {
            _viewModel.StatusText = $"You already reacted {reaction.Emoji} to that message.";
            return;
        }

        await _viewModel.SendReactionAsync(message, reaction.Emoji);
    }

    private async void OnCopyMessage(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelMessage message }) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(message.Display);
    }

    /// <summary>Copy every message in the selected tab, oldest first.</summary>
    private async void OnCopyMessages(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedTab is not { } tab) return;
        var text = string.Join(Environment.NewLine, tab.Messages.Reverse().Select(m => m.Display));
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    private async void OnClearMessages(object? sender, RoutedEventArgs e)
    {
        var tab = _viewModel.SelectedTab;
        if (tab is null || tab.Messages.Count == 0) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Clear messages",
                $"Clear {tab.Messages.Count} message{(tab.Messages.Count == 1 ? "" : "s")} from {tab.TabHeader}? This cannot be undone.",
                confirmText: "Clear"))
            return;
        tab.Messages.Clear();
    }

    /// <summary>Clear button on a DM tab. Confirmation lives here rather than
    /// in the view model command because a dialog needs an owning window.</summary>
    private async void OnClearConversationMessages(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConversationTabViewModel convo }) return;
        if (convo.Messages.Count == 0) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Clear conversation",
                $"Clear {convo.Messages.Count} message{(convo.Messages.Count == 1 ? "" : "s")} from the conversation with {convo.PeerName}? This cannot be undone.",
                confirmText: "Clear"))
            return;
        convo.ClearMessagesCommand.Execute(null);
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _viewModel.LogLines);
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    private async void OnClearLog(object? sender, RoutedEventArgs e)
    {
        int count = _viewModel.LogLines.Count;
        if (count == 0) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Clear log",
                $"Clear {count} log line{(count == 1 ? "" : "s")}?", confirmText: "Clear"))
            return;
        _viewModel.LogLines.Clear();
    }

    private void OnAbout(object? sender, RoutedEventArgs e) => new AboutWindow().ShowDialog(this);

    /// <summary>Insert a picked emoji at the caret in the compose box.</summary>
    private async void OnPickComposeEmoji(object? sender, RoutedEventArgs e)
    {
        var glyph = await EmojiPickerWindow.PickAsync(this);
        if (string.IsNullOrEmpty(glyph)) return;

        var text = _viewModel.MessageText ?? string.Empty;
        int caret = Math.Clamp(ComposeBox.CaretIndex, 0, text.Length);
        _viewModel.MessageText = text.Insert(caret, glyph);
        ComposeBox.CaretIndex = caret + glyph.Length;
        ComposeBox.Focus();
    }

    /// <summary>Enter sends, Shift+Enter is left alone for future multi-line.</summary>
    private void OnComposeKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        if (e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) return;
        if (_viewModel.SendMessageCommand.CanExecute(null))
            _viewModel.SendMessageCommand.Execute(null);
        e.Handled = true;
    }

    private NodeIdentityWindow? _identityWindow;

    private void OnOpenNodeIdentity(object? sender, RoutedEventArgs e)
    {
        if (_identityWindow is not null) { _identityWindow.Activate(); return; }
        _identityWindow = new NodeIdentityWindow { DataContext = _viewModel };
        _identityWindow.Closed += (_, _) => _identityWindow = null;
        _identityWindow.Show(this);
    }

    private MqttSettingsWindow? _mqttWindow;

    private void OnOpenMqttSettings(object? sender, RoutedEventArgs e)
    {
        if (_mqttWindow is not null) { _mqttWindow.Activate(); return; }
        _mqttWindow = new MqttSettingsWindow { DataContext = _viewModel };
        _mqttWindow.Closed += (_, _) => _mqttWindow = null;
        _mqttWindow.Show(this);
    }

    private ScriptsWindow? _scriptsWindow;

    private void OnOpenScripts(object? sender, RoutedEventArgs e)
    {
        if (_scriptsWindow is not null) { _scriptsWindow.Activate(); return; }
        // The view model goes in as IScriptRuntime, not as a DataContext: the
        // window binds to its own model for everything on disk, and needs the
        // radio only for the master switch and the reload-on-edit.
        _scriptsWindow = new ScriptsWindow(_viewModel);
        _scriptsWindow.Closed += (_, _) => _scriptsWindow = null;
        _scriptsWindow.Show(this);
    }

    // -- Tab drag-to-reorder --------------------------------------------------

    /// <summary>In-process drag payload: the dragged tab's view model itself,
    /// so nothing has to be serialised and the drag can't leave the app.</summary>
    private static readonly DataFormat<object> TabDragFormat =
        DataFormat.CreateInProcessFormat<object>("MeshRF.Tab");

    /// <summary>
    /// Starts the drag straight from the press. Avalonia 12's DoDragDropAsync
    /// only accepts PointerPressedEventArgs, so there is no "wait for the
    /// pointer to move" step here — the platform's own drag loop applies the
    /// click-versus-drag threshold and simply reports None for a plain click.
    /// </summary>
    private async void OnTabsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MainTabs).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual v) return;

        // A press on the close button is a click, not the start of a drag.
        if (v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (v.FindAncestorOfType<TabItem>(includeSelf: true)?.DataContext is not { } dragged) return;
        if (!_viewModel.CanDragTab(dragged)) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TabDragFormat, dragged));
        try
        {
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (InvalidOperationException)
        {
            // A drag is already in progress, or the platform refused to start one.
        }
    }

    private void OnTabsDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ResolveDropTarget(e) is not null ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabsDrop(object? sender, DragEventArgs e)
    {
        if (ResolveDropTarget(e) is not ({ } dragged, { } target)) return;
        _viewModel.ReorderTabPair(dragged, target);
        e.Handled = true;
    }

    /// <summary>The (dragged, target) pair for this drag if it is a legal drop,
    /// otherwise null. Shared so the hover feedback and the drop itself can
    /// never disagree about what is allowed.</summary>
    private (object Dragged, object Target)? ResolveDropTarget(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(TabDragFormat) is not { } dragged) return null;
        if (e.Source is not Visual v) return null;
        if (v.FindAncestorOfType<TabItem>(includeSelf: true)?.DataContext is not { } target) return null;
        return _viewModel.CanReorderTabPair(dragged, target) ? (dragged, target) : null;
    }

    private void OnOpenChannelSettings(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelTabViewModel channel }) return;
        ChannelSettingsWindow.Open(this, _viewModel, channel);
    }
}
