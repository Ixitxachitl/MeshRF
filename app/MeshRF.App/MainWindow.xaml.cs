// SPDX-License-Identifier: GPL-3.0-or-later
using System.Linq;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MeshRF.App.ViewModels;
using MeshRF.App.Views;

namespace MeshRF.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statsTimer;
    // The render-synced frame loop. Driven by CompositionTarget.Rendering so it
    // fires once per composition frame (monitor refresh) instead of being
    // coalesced by a DispatcherTimer, which capped us near ~21 Hz even though
    // the UI thread was >98% idle.
    private bool _renderingHooked;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    // Render-loop frame cap. CompositionTarget.Rendering fires at the monitor's
    // refresh rate (often 120/144 Hz). We gate work to a fixed cadence so the
    // waterfall always advances at a constant rows/sec regardless of refresh
    // rate (otherwise faster monitors scroll/stretch the waterfall).
    private const double TargetFps = 60.0;
    private static readonly TimeSpan TargetFrameInterval =
        TimeSpan.FromSeconds(1.0 / TargetFps);
    private TimeSpan _lastProcessedRenderTime = TimeSpan.MinValue;
    private float[] _spectrumBuffer = Array.Empty<float>();

    // Waterfall row pacing. The waterfall advances one row per N received native
    // spectrum frames so the scroll speed tracks received-signal time, not the
    // UI refresh rate. Intervening pulls are max-held into _wfRowAccum so no
    // spectral data is lost between rows. When no new native frames have arrived
    // the waterfall holds still (it only moves "as data is received").
    //
    // The target rows/sec is user-controlled via MainViewModel.WaterfallRowsPerSecond
    // (clamped below). This is pure time resolution: each row spans
    // 1/rowsPerSecond of received time and is independent of FFT/frequency
    // resolution, up to the native frame rate (sample_rate / fft_size).
    private const double MinWaterfallRowsPerSecond = 5.0;
    private const double MaxWaterfallRowsPerSecond = 240.0;
    private const int MaxFramesToPull = 64; // Must handle ~32 frames/tick at 60fps + margin
    private ulong _wfLastFrameCount;
    private float[] _wfRowAccum = Array.Empty<float>();
    private float[] _wfFrameBuffer = Array.Empty<float>(); // Pooled buffer for PullSpectrumFrames
    private List<float[]> _wfRowBatch = new(); // Pooled list for batch push
    private bool _wfRowAccumValid;
    private long _wfAccumFrames;
    private bool _layoutApplied;
    private int _snapshotInFlight;
    private double? _conversationMessagesPaneStar;
    private double? _conversationRightPaneStar;
    private double? _conversationTelemetryPaneStar;
    private double? _conversationLocationHistoryPaneStar;

    // Rolling history of recent spectrum frames, used to freeze a spectrogram
    // of the last detected packet. Holds the most recent HistoryFrames frames.
    private const int HistoryFrames = 64;
    private float[][]? _specHistoryRing;
    private int _specHistoryWrite;
    private int _specHistoryCount;
    private int _specHistoryBinCount;

    private long _lastUiTickStamp;
    private double _uiFpsEma;

    private long _perfWindowStartStamp;
    private int _perfUiTickCount;
    private double _perfUiTickMs;
    private double _perfPullMs;
    private double _perfSpectrumMs;
    private double _perfWaterfallMs;
    private int _perfStatsTickCount;
    private double _perfStatsMs;

    public MainWindow()
    {
        InitializeComponent();
        ApplySavedLayout();

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100), // 10 Hz
        };
        _statsTimer.Tick += OnStatsTick;
        Loaded   += OnLoaded;
        Closing  += OnClosing;
        Unloaded += (_, _) =>
        {
            HookRendering(false);
            _statsTimer.Stop();
        };
        MainTabs.SelectionChanged += (_, _) => ApplyConversationPaneLayoutToCurrentTab();
    }

    private void HookRendering(bool hook)
    {
        if (hook == _renderingHooked) return;
        if (hook)
            CompositionTarget.Rendering += OnUiTick;
        else
            CompositionTarget.Rendering -= OnUiTick;
        _renderingHooked = hook;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        HookRendering(true);
        _statsTimer.Start();
        if (!_layoutApplied)
        {
            ApplySavedLayout();
            _layoutApplied = true;
        }

        // When the waterfall recomputes auto-levels, mirror them back onto
        // the VM so the manual sliders track. Only do this when AutoLevels
        // is on; otherwise the user-driven slider values must win.
        Waterfall.AutoLevelsChanged += (floor, ceil) =>
        {
            if (DataContext is not MainViewModel vm) return;
            if (!vm.WaterfallAutoLevels) return;
            vm.WaterfallFloorDb = floor;
            vm.WaterfallCeilDb = ceil;
        };

        if (DataContext is MainViewModel mvm)
            mvm.PacketDecoded += OnPacketDecoded;

        Map.NodeDoubleClicked += node =>
        {
            if (DataContext is MainViewModel vm)
                vm.OpenConversationForNodeCommand.Execute(node);
        };
        Map.NodeRightClicked += node =>
        {
            NodesGrid.SelectedItem = node;
            if (NodesGrid.ContextMenu is { } cm)
            {
                cm.PlacementTarget = Map;
                cm.IsOpen = true;
            }
        };
        Map.WaypointRightClicked += wp =>
        {
            if (DataContext is not MainViewModel vm) return;
            var result = MessageBox.Show(
                this,
                $"Delete waypoint \"{wp.DisplayName}\"?",
                "Delete waypoint",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK) return;

            vm.RemoveWaypoints(new[] { wp });
        };
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveLayout();
    }

    // Marks that a CRC-valid packet was just decoded. A bad frame or a false
    // positive (preamble that never decodes) never reaches here, so the
    // last-packet panel only ever shows genuine packets. The whole packet is
    // already buffered in the native IQ ring by the time it decodes, so we
    // snapshot immediately — any extra delay just ages the packet toward the
    // far end of the ring and risks the preamble scrolling out.
    private void OnPacketDecoded()
    {
        if (System.Threading.Interlocked.Exchange(ref _snapshotInFlight, 1) != 0)
            return;
        _ = FreezeLastPacketAsync();
    }

    // Double-clicking a node row opens (or focuses) a DM conversation tab.
    private void NodesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is System.Windows.Controls.DataGrid grid &&
            grid.SelectedItem is MeshRF.Nodes.NodeRecord node)
        {
            vm.OpenConversationForNodeCommand.Execute(node);
        }
    }

    // Pressing Delete in the node list removes the selected node(s).
    private void NodesGrid_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            DeleteSelectedNodes();
            e.Handled = true;
        }
    }

    // Context-menu "Delete" removes the selected node(s).
    private void OnDeleteNodes(object sender, RoutedEventArgs e) => DeleteSelectedNodes();

    // Context-menu "Copy" uses DataGrid clipboard export (tab-separated rows).
    private void OnCopyNodes(object sender, RoutedEventArgs e)
    {
        NodesGrid.Focus();
        if (System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, NodesGrid))
            System.Windows.Input.ApplicationCommands.Copy.Execute(null, NodesGrid);
    }

    private void WaypointsGrid_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete)
        {
            DeleteSelectedWaypoints();
            e.Handled = true;
        }
    }

    private void OnDeleteWaypoints(object sender, RoutedEventArgs e) => DeleteSelectedWaypoints();

    // Context-menu "Traceroute" sends a Meshtastic-style route-discovery request
    // to the selected node (rate-limited to one per cooldown by the view model).
    private async void OnTraceroute(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var node = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .FirstOrDefault();
        if (node is null) return;
        await vm.TracerouteAsync(node);
    }

    // Context-menu "Request position" asks the selected node to reply with its
    // location (rate-limited to one per cooldown by the view model).
    private async void OnRequestPosition(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var node = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .FirstOrDefault();
        if (node is null) return;
        await vm.RequestPositionAsync(node);
    }

    // Context-menu "Request node info" asks the selected node(s) to reply
    // with NodeInfo without resetting stored keys.
    private void OnRequestNodeInfo(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .ToList();
        if (selected.Count == 0) return;
        vm.RequestNodeInfoOnly(selected);
    }

    // Context-menu "Exchange node info" asks the selected node(s) to reply
    // with NodeInfo without clearing any stored keys.
    private void OnExchangeNodeInfo(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .ToList();
        if (selected.Count == 0) return;
        vm.ExchangeNodeInfo(selected);
    }

    // Context-menu "Exchange location" asks for the node's location and also
    // sends our current location directly to that node.
    private async void OnExchangeLocation(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var node = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .FirstOrDefault();
        if (node is null) return;
        await vm.ExchangeLocationAsync(node);
    }

    // Context-menu "Request new keys" forgets the stored key(s) and asks the
    // selected node(s) to re-send their NodeInfo so a changed key can be trusted.
    private void OnRequestKeys(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .ToList();
        if (selected.Count == 0) return;
        vm.RequestKeys(selected);
    }

    private void DeleteSelectedNodes()
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .ToList();
        if (selected.Count == 0) return;

        var label = selected.Count == 1
            ? $"node \"{(string.IsNullOrWhiteSpace(selected[0].LongName) ? selected[0].DisplayId : selected[0].LongName)}\""
            : $"{selected.Count} nodes";
        var result = MessageBox.Show(
            this,
            $"Delete {label}? This removes them from the node database.",
            "Delete nodes",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        vm.RemoveNodes(selected);
    }

    private void DeleteSelectedWaypoints()
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = WaypointsGrid.SelectedItems
            .OfType<MeshRF.Waypoints.WaypointRecord>()
            .ToList();
        if (selected.Count == 0) return;

        var label = selected.Count == 1
            ? $"waypoint \"{selected[0].DisplayName}\""
            : $"{selected.Count} waypoints";
        var result = MessageBox.Show(
            this,
            $"Delete {label}? This removes them from the waypoint database.",
            "Delete waypoints",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        vm.RemoveWaypoints(selected);
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        long t0 = Stopwatch.GetTimestamp();
        vm.RefreshStats();
        long t1 = Stopwatch.GetTimestamp();

        _perfStatsTickCount++;
        _perfStatsMs += TicksToMilliseconds(t1 - t0);
        FlushUiPerfSummary(vm, t1);
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // CompositionTarget.Rendering can fire more than once for the same
        // composition frame; skip the duplicate so FPS/perf accounting and the
        // waterfall scroll advance exactly once per rendered frame.
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderingTime) return;
            TimeSpan monitorFrame = _lastRenderingTime == TimeSpan.MinValue
                ? TargetFrameInterval
                : re.RenderingTime - _lastRenderingTime;
            _lastRenderingTime = re.RenderingTime;

            // Cap to TargetFps. The render callback fires at the monitor refresh
            // (often 120/144 Hz); process a frame only once we're at least half a
            // monitor-frame short of the target interval. This self-adapts to the
            // refresh rate and lands as close to 60 as the rate's divisors allow,
            // keeping the cadence (and thus waterfall scroll) stable.
            if (_lastProcessedRenderTime != TimeSpan.MinValue)
            {
                TimeSpan sinceProcessed = re.RenderingTime - _lastProcessedRenderTime;
                if (sinceProcessed < TargetFrameInterval - new TimeSpan(monitorFrame.Ticks / 2))
                    return;
            }
            _lastProcessedRenderTime = re.RenderingTime;
        }

        long now = Stopwatch.GetTimestamp();
        if (_lastUiTickStamp != 0)
        {
            double dt = (now - _lastUiTickStamp) / (double)Stopwatch.Frequency;
            if (dt > 0)
            {
                double instantFps = 1.0 / dt;
                if (_uiFpsEma <= 0)
                    _uiFpsEma = instantFps;
                else
                    _uiFpsEma += (instantFps - _uiFpsEma) * 0.12;
                vm.UiFrameRateHz = _uiFpsEma;
            }
        }
        _lastUiTickStamp = now;

        long pullTicks = 0;
        long spectrumTicks = 0;
        long waterfallTicks = 0;

        // Don't pull spectrum when stopped — the native side caches the
        // last frame and would keep scrolling the waterfall.
        if (!vm.IsRunning)
        {
            long idleEnd = Stopwatch.GetTimestamp();
            _perfUiTickCount++;
            _perfUiTickMs += TicksToMilliseconds(idleEnd - now);
            FlushUiPerfSummary(vm, idleEnd);
            return;
        }

        // Apply current colormap selection.
        Waterfall.Colormap = ParseColormap(vm.WaterfallColormap);
        // Keep the frozen snapshot's colormap matched to the live waterfall.
        LastPacket.Colormap = Waterfall.Colormap;

        var n = vm.Core.SpectrumSize;
        if (n <= 0) return;
        if (_spectrumBuffer.Length != n) _spectrumBuffer = new float[n];

        // Keep the frequency-axis span and centre in sync with the running pipeline.
        var rate = vm.Core.SampleRateHz;
        if (rate > 0) vm.SpectrumSpanHz = rate;
        var centre = vm.Core.SpectrumCenterHz;
        if (centre > 0) vm.SpectrumCenterHz = centre;

        long tPull0 = Stopwatch.GetTimestamp();
        var written = vm.Core.PullSpectrum(_spectrumBuffer);
        long tPull1 = Stopwatch.GetTimestamp();
        pullTicks = tPull1 - tPull0;
        if (written > 0)
        {
            var spectrum = _spectrumBuffer.AsSpan(0, written);

            long tSpec0 = Stopwatch.GetTimestamp();
            Spectrum.Update(spectrum);
            long tSpec1 = Stopwatch.GetTimestamp();
            spectrumTicks = tSpec1 - tSpec0;

            long tWf0 = Stopwatch.GetTimestamp();
            AdvanceWaterfall(vm, spectrum, rate, written);
            long tWf1 = Stopwatch.GetTimestamp();
            waterfallTicks = tWf1 - tWf0;
        }

        long end = Stopwatch.GetTimestamp();
        _perfUiTickCount++;
        _perfUiTickMs += TicksToMilliseconds(end - now);
        _perfPullMs += TicksToMilliseconds(pullTicks);
        _perfSpectrumMs += TicksToMilliseconds(spectrumTicks);
        _perfWaterfallMs += TicksToMilliseconds(waterfallTicks);
        FlushUiPerfSummary(vm, end);
    }

    // Advances the waterfall in proportion to received signal time rather than
    // UI frames. Each call max-holds the freshly pulled spectrum into the row
    // accumulator and emits a finished row only once enough native frames have
    // been received to fill one row at WaterfallRowsPerSecond. If no new native
    // frames have arrived since the last call (delta == 0), nothing is pushed
    // so the waterfall stays still until data actually arrives.
    private const int NativeFrameRingCapacity = 256; // Must match kFrameRingCapacity in C++.

    private void AdvanceWaterfall(
        MainViewModel vm, ReadOnlySpan<float> spectrum, uint sampleRate, int bins)
    {
        ulong frameCount = vm.Core.SpectrumFrameCount;

        // First read or pipeline (re)start: re-baseline without scrolling.
        if (_wfLastFrameCount == 0 || frameCount < _wfLastFrameCount)
        {
            _wfLastFrameCount = frameCount;
            return;
        }

        if (frameCount <= _wfLastFrameCount)
            return; // No new frames.

        // If we're too far behind (ring buffer overflow), skip ahead to avoid
        // pulling silent/stale frames. Leave a margin to avoid boundary issues.
        ulong behind = frameCount - _wfLastFrameCount;
        if (behind > (ulong)(NativeFrameRingCapacity - MaxFramesToPull))
        {
            _wfLastFrameCount = frameCount - (ulong)(NativeFrameRingCapacity / 2);
            _wfRowAccumValid = false;
            _wfAccumFrames = 0;
        }

        // Calculate the desired frames per row (time resolution).
        double rowsPerSecond = Math.Clamp(
            vm.WaterfallRowsPerSecond,
            MinWaterfallRowsPerSecond,
            MaxWaterfallRowsPerSecond);
        int framesPerRow = 1;
        if (sampleRate > 0 && bins > 0)
        {
            double nativeFrameRate = (double)sampleRate / bins;
            framesPerRow = (int)Math.Round(nativeFrameRate / rowsPerSecond);
            if (framesPerRow < 1) framesPerRow = 1;
        }

        // Ensure the pooled frame buffer is large enough.
        int bufferSize = MaxFramesToPull * bins;
        if (_wfFrameBuffer.Length < bufferSize)
            _wfFrameBuffer = new float[bufferSize];

        int framesPulled = vm.Core.PullSpectrumFrames(_wfFrameBuffer, _wfLastFrameCount, MaxFramesToPull);

        if (framesPulled == 0)
            return;

        // Only advance by what we actually pulled (avoid gaps during stutter).
        _wfLastFrameCount += (ulong)framesPulled;

        // Accumulate frames into rows, batching completed rows for a single push.
        _wfRowBatch.Clear();

        for (int frameIdx = 0; frameIdx < framesPulled; frameIdx++)
        {
            var frame = _wfFrameBuffer.AsSpan(frameIdx * bins, bins);

            // Accumulate this frame into the current row via max-hold.
            if (!_wfRowAccumValid || _wfRowAccum.Length != bins)
            {
                if (_wfRowAccum.Length != bins)
                    _wfRowAccum = new float[bins];
                frame.CopyTo(_wfRowAccum);
                _wfRowAccumValid = true;
            }
            else
            {
                for (int i = 0; i < bins; i++)
                    if (frame[i] > _wfRowAccum[i])
                        _wfRowAccum[i] = frame[i];
            }
            _wfAccumFrames++;

            // Complete a row when we have enough frames.
            if (_wfAccumFrames >= framesPerRow)
            {
                // Clone the row for the batch (will be pushed later).
                var row = (float[])_wfRowAccum.Clone();
                _wfRowBatch.Add(row);
                PushSpectrumHistory(row);
                _wfRowAccumValid = false;
                _wfAccumFrames = 0;
            }
        }

        // Batch-push all completed rows at once (single lock/unlock cycle).
        if (_wfRowBatch.Count > 0)
            Waterfall.PushBatch(_wfRowBatch);
    }

    private static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private void FlushUiPerfSummary(MainViewModel vm, long nowStamp)
    {
        if (_perfWindowStartStamp == 0)
        {
            _perfWindowStartStamp = nowStamp;
            return;
        }

        double windowMs = TicksToMilliseconds(nowStamp - _perfWindowStartStamp);
        if (windowMs < 1000.0) return;

        double uiAvg = _perfUiTickCount > 0 ? _perfUiTickMs / _perfUiTickCount : 0.0;
        double pullAvg = _perfUiTickCount > 0 ? _perfPullMs / _perfUiTickCount : 0.0;
        double specAvg = _perfUiTickCount > 0 ? _perfSpectrumMs / _perfUiTickCount : 0.0;
        double wfAvg = _perfUiTickCount > 0 ? _perfWaterfallMs / _perfUiTickCount : 0.0;
        double statsAvg = _perfStatsTickCount > 0 ? _perfStatsMs / _perfStatsTickCount : 0.0;

        double uiBusyPct = _perfUiTickMs * 100.0 / windowMs;
        double statsBusyPct = _perfStatsMs * 100.0 / windowMs;

        var (mapRenders, mapMs) = Map?.DrainRenderStats() ?? (0, 0.0);
        double mapBusyPct = mapMs * 100.0 / windowMs;

        vm.UiPerfSummary =
            $"ui {uiAvg:0.0}ms (pull {pullAvg:0.0} spec {specAvg:0.0} wf {wfAvg:0.0}) " +
            $"stats {statsAvg:0.0}ms@{_perfStatsTickCount}/s busy ui {uiBusyPct:0}% stats {statsBusyPct:0}% " +
            $"map {mapRenders}/s {mapMs:0.0}ms busy {mapBusyPct:0}%";

        _perfWindowStartStamp = nowStamp;
        _perfUiTickCount = 0;
        _perfUiTickMs = 0.0;
        _perfPullMs = 0.0;
        _perfSpectrumMs = 0.0;
        _perfWaterfallMs = 0.0;
        _perfStatsTickCount = 0;
        _perfStatsMs = 0.0;
    }

    private void PushSpectrumHistory(ReadOnlySpan<float> frame)
    {
        if (frame.Length <= 0)
            return;

        if (_specHistoryRing is null || _specHistoryBinCount != frame.Length)
        {
            _specHistoryBinCount = frame.Length;
            _specHistoryRing = new float[HistoryFrames][];
            for (int i = 0; i < HistoryFrames; i++)
                _specHistoryRing[i] = new float[_specHistoryBinCount];
            _specHistoryWrite = 0;
            _specHistoryCount = 0;
        }

        frame.CopyTo(_specHistoryRing[_specHistoryWrite]);
        _specHistoryWrite = (_specHistoryWrite + 1) % HistoryFrames;
        if (_specHistoryCount < HistoryFrames)
            _specHistoryCount++;
    }

    private float[] GetHistoryFrameByAge(int index)
    {
        int oldest = (_specHistoryWrite - _specHistoryCount + HistoryFrames) % HistoryFrames;
        int slot = (oldest + index) % HistoryFrames;
        return _specHistoryRing![slot];
    }

    // Snapshots the last detected packet as a high-time-resolution STFT
    // spectrogram computed natively from buffered modem-rate IQ, cropped
    // (zoomed) to the LoRa channel so the individual chirps are visible.
    //
    // PullPacketSpectrogram is CPU-heavy (IQ ring copy + energy locator FFTs +
    // 512-frame STFT) so it runs on a thread-pool thread; only the final row
    // push and visibility update are marshalled back to the UI thread.
    private static WaterfallColormap ParseColormap(string? name) => name switch
    {
        "Inferno" => WaterfallColormap.Inferno,
        "Meshtastic" => WaterfallColormap.Meshtastic,
        _ => WaterfallColormap.Turbo,
    };

    private async Task FreezeLastPacketAsync()
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;

            // Compute max rows based on LoRa parameters. Slow modes (high SF,
            // low BW) need many more STFT frames to capture the full packet.
            // STFT: 512-point FFT with 128-sample hop.
            // Max packet airtime ≈ (preamble + header + max_payload) symbols.
            // At modem rate = BW * 2 oversampling, symbol = 2^SF * 2 samples.
            const int kFft = 512;
            const int kHop = 128;
            const int nFreq = 256;

            int sf = Math.Clamp(vm.OverrideSf, (byte)7, (byte)12);
            double bwHz = Math.Max(7_800.0, vm.OverrideBwKhz * 1000.0);
            double modemRate = bwHz * 2.0; // 2x oversampling
            double symbolSamples = (1 << sf) * 2.0; // symbol at modem rate

            // Estimate max packet: 16 preamble + 4.25 sync + 8 header + 255 payload symbols.
            // For SF12/125k, 255-byte packet is ~280 symbols ≈ 9 seconds.
            double maxSymbols = 16.0 + 4.25 + 8.0 + 280.0;
            double maxSamples = maxSymbols * symbolSamples;
            int nTime = Math.Max(2048, (int)Math.Ceiling((maxSamples - kFft) / kHop) + 1);
            nTime = Math.Min(nTime, 16384); // Cap at 16K to avoid huge allocations

            // Pull spectrogram and compute contrast off the UI thread.
            (int rows, float[] grid, double floor, double ceil) PullAndComputeContrast()
            {
                var grid = new float[nTime * nFreq];
                int written = vm.Core.PullPacketSpectrogram(grid, nTime, nFreq);
                if (written <= 0)
                    return (0, Array.Empty<float>(), -100.0, 0.0);

                int sampleCount = written * nFreq;
                var (floor, ceil) = ComputeContrastLevels(grid.AsSpan(0, sampleCount));
                return (written, grid, floor, ceil);
            }

            static int ComputeRetryDelayMs(MainViewModel vm)
            {
                // Base retry on LoRa symbol time so slow modes (high SF / low BW)
                // wait longer to accumulate enough history for the first packet.
                int sf = Math.Clamp(vm.OverrideSf, (byte)5, (byte)12);
                double bwHz = Math.Max(7_800.0, vm.OverrideBwKhz * 1000.0);
                double symbolMs = ((1 << sf) / bwHz) * 1000.0;

                // Roughly preamble-scale wait with hard bounds for UI responsiveness.
                int delayMs = (int)Math.Round(symbolMs * 24.0);
                return Math.Clamp(delayMs, 80, 900);
            }

            // Pull the packed spectrogram off the UI thread; commit once.
            var (rows, grid, floor, ceil) = await Task.Run(PullAndComputeContrast)
                                                .ConfigureAwait(true);

            // The very first packet after RX start can arrive before enough
            // IQ history has accumulated for a robust native snapshot. Retry
            // once shortly after decode before falling back.
            if (rows <= 0)
            {
                await Task.Delay(ComputeRetryDelayMs(vm)).ConfigureAwait(true);
                (rows, grid, floor, ceil) = await Task.Run(PullAndComputeContrast)
                                                 .ConfigureAwait(true);
            }

            if (rows <= 0)
            {
                FreezeLastPacketFromHistory(vm);
                return;
            }

            int sampleCount = rows * nFreq;
            LastPacket.FloorDb = floor;
            LastPacket.CeilDb = ceil;
            LastPacket.Colormap = ParseColormap(vm.WaterfallColormap);
            LastPacket.ReplaceFrames(grid.AsSpan(0, sampleCount), rows, nFreq);
            LastPacketTitle.Text = $"Last packet  {DateTime.Now:M/d/yyyy h:mm:ss tt}";
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _snapshotInFlight, 0);
        }
    }

    // Compute robust display levels from a spectrogram for high contrast.
    // This is O(n log n) for sorting so it should be called off the UI thread.
    private static (double floor, double ceil) ComputeContrastLevels(ReadOnlySpan<float> values)
    {
        if (values.Length < 16)
            return (-100.0, 0.0);

        var vals = new float[values.Length];
        int valid = 0;
        for (int i = 0; i < values.Length; i++)
        {
            float v = values[i];
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            vals[valid++] = v;
        }
        if (valid < 16)
            return (-100.0, 0.0);

        Array.Sort(vals, 0, valid);
        float p05 = vals[(int)Math.Clamp(Math.Round((valid - 1) * 0.05), 0, valid - 1)];
        float p995 = vals[(int)Math.Clamp(Math.Round((valid - 1) * 0.995), 0, valid - 1)];

        double floor = p05 - 2.0;
        double ceil = p995 + 2.0;
        if (ceil - floor < 24.0) ceil = floor + 24.0;

        return (floor, ceil);
    }

    // Fallback: replays the rolling history into the frozen last-packet
    // spectrogram, cropped (zoomed) to just the LoRa channel around DC.
    private void FreezeLastPacketFromHistory(MainViewModel vm)
    {
        if (_specHistoryCount == 0 || _specHistoryRing is null) return;

        // The spectrum spans the full device sample rate, centered on DC
        // (LoRa is offset-tuned to DC). The channel is 250 kHz wide; show a
        // little margin (1.5x) so the chirp edges are visible.
        const double zoomHz = 350_000.0; // ~1.4x the 250 kHz channel
        // Fall back to the known device rate (2.4 MHz) if the VM hasn't been
        // ticked yet, so the snapshot is still zoomed on the very first packet.
        double spanHz = vm.SpectrumSpanHz > 0 ? vm.SpectrumSpanHz
                       : vm.Core.SampleRateHz > 0 ? vm.Core.SampleRateHz
                       : 2_400_000.0;

        int binCount = _specHistoryBinCount;
        int half = (int)Math.Round(zoomHz / spanHz * binCount / 2.0);
        half = Math.Clamp(half, 16, binCount / 2);
        int center = binCount / 2;
        int lo = center - half;
        int width = half * 2;

        var frames = new List<float[]>(_specHistoryCount);
        var slice = new float[width];
        for (int i = 0; i < _specHistoryCount; i++)
        {
            var f = GetHistoryFrameByAge(i);
            Array.Copy(f, lo, slice, 0, width);
            frames.Add((float[])slice.Clone());
        }
        ApplySnapshotContrast(frames);
        LastPacket.Colormap = ParseColormap(vm.WaterfallColormap);
        LastPacket.ReplaceFrames(frames);
        LastPacketTitle.Text = $"Last packet  {DateTime.Now:M/d/yyyy h:mm:ss tt}";
    }

    private void ApplySnapshotContrast(IReadOnlyList<float[]> frames)
    {
        // Derive robust display levels from this specific snapshot so frozen
        // packets stay high-contrast regardless of the live waterfall levels.
        int count = 0;
        for (int i = 0; i < frames.Count; i++)
            count += frames[i]?.Length ?? 0;
        if (count < 16) return;

        var vals = new float[count];
        int p = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var row = frames[i];
            if (row is null) continue;
            for (int j = 0; j < row.Length; j++)
            {
                float v = row[j];
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                vals[p++] = v;
            }
        }
        if (p < 16) return;

        Array.Sort(vals, 0, p);
        float p05 = vals[(int)Math.Clamp(Math.Round((p - 1) * 0.05), 0, p - 1)];
        float p995 = vals[(int)Math.Clamp(Math.Round((p - 1) * 0.995), 0, p - 1)];

        double floor = p05 - 2.0;
        double ceil = p995 + 2.0;
        if (ceil - floor < 24.0) ceil = floor + 24.0;

        LastPacket.FloorDb = floor;
        LastPacket.CeilDb = ceil;
    }

    private void ApplySavedLayout()
    {
        var settings = AppSettings.Load();

        ApplyWindowBounds(settings);
        ApplyStarPair(MainLayoutGrid.ColumnDefinitions[0], settings.MainLeftPaneStar,
                      MainLayoutGrid.ColumnDefinitions[2], settings.MainRightPaneStar);

        var leftTop = settings.MainLeftTopPaneStar ?? settings.MainTopPaneStar;
        var leftBottom = settings.MainLeftBottomPaneStar ?? settings.MainBottomPaneStar;
        ApplyStarPair(LeftPaneGrid.RowDefinitions[0], leftTop,
                  LeftPaneGrid.RowDefinitions[2], leftBottom);

        var rightTop = settings.MainRightTopPaneStar ?? settings.MainTopPaneStar;
        var rightBottom = settings.MainRightBottomPaneStar ?? settings.MainBottomPaneStar;
        ApplyStarPair(RightPaneGrid.RowDefinitions[0], rightTop,
                  RightPaneGrid.RowDefinitions[2], rightBottom);

        ApplyStarPair(SpectrumLayoutGrid.RowDefinitions[0], settings.SpectrumTopPaneStar,
                      SpectrumLayoutGrid.RowDefinitions[2], settings.SpectrumBottomPaneStar);
        ApplyStarPair(MessagesLayoutGrid.RowDefinitions[0], settings.MessagesTopPaneStar,
                      MessagesLayoutGrid.RowDefinitions[2], settings.MessagesBottomPaneStar);

        _conversationMessagesPaneStar = settings.ConversationMessagesPaneStar;
        _conversationRightPaneStar = settings.ConversationRightPaneStar;
        _conversationTelemetryPaneStar = settings.ConversationTelemetryPaneStar;
        _conversationLocationHistoryPaneStar = settings.ConversationLocationHistoryPaneStar;

        ApplyWaypointsColumnWidths(settings.WaypointColumnWidths);

        IdentityExpander.IsExpanded = settings.IdentityExpanded;
        RestoreSelectedTab(settings);
        Map.LoadFromSettings(settings);
        ApplyConversationPaneLayoutToCurrentTab();
        _layoutApplied = true;
    }

    private void ApplyWindowBounds(AppSettings settings)
    {
        double width = settings.WindowWidth ?? Width;
        double height = settings.WindowHeight ?? Height;
        width = Math.Max(MinWidth, width);
        height = Math.Max(MinHeight, height);

        var left = settings.WindowLeft;
        var top = settings.WindowTop;
        bool hasPosition = left is not null && top is not null;
        if (hasPosition && IsVisibleOnAnyScreen(left!.Value, top!.Value, width, height))
        {
            Left = left.Value;
            Top = top.Value;
        }

        Width = width;
        Height = height;

        if (Enum.TryParse<WindowState>(settings.WindowState, out var savedState) &&
            savedState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
        }
    }

    private static bool IsVisibleOnAnyScreen(double left, double top, double width, double height)
    {
        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        return left + Math.Min(width, 80) > screenLeft &&
               top + Math.Min(height, 80) > screenTop &&
               left < screenRight - 40 &&
               top < screenBottom - 40;
    }

    private static void ApplyStarPair(System.Windows.Controls.RowDefinition firstRow, double? firstStar,
                                      System.Windows.Controls.RowDefinition secondRow, double? secondStar)
    {
        if (firstStar is not > 0 || secondStar is not > 0) return;
        firstRow.Height = new GridLength(firstStar.Value, GridUnitType.Star);
        secondRow.Height = new GridLength(secondStar.Value, GridUnitType.Star);
    }

    private static void ApplyStarPair(System.Windows.Controls.ColumnDefinition firstColumn, double? firstStar,
                                      System.Windows.Controls.ColumnDefinition secondColumn, double? secondStar)
    {
        if (firstStar is not > 0 || secondStar is not > 0) return;
        firstColumn.Width = new GridLength(firstStar.Value, GridUnitType.Star);
        secondColumn.Width = new GridLength(secondStar.Value, GridUnitType.Star);
    }

    private void RestoreSelectedTab(AppSettings settings)
    {
        if (DataContext is not MainViewModel vm)
            return;

        if (settings.SelectedConversationNode != 0)
        {
            var convo = vm.Tabs.OfType<ConversationViewModel>()
                               .FirstOrDefault(t => t.NodeNum == settings.SelectedConversationNode);
            if (convo is not null)
            {
                vm.SelectedTab = convo;
                return;
            }
        }

        if (settings.SelectedChannelIndex >= 0)
        {
            var channel = vm.Tabs.OfType<ChannelViewModel>()
                                .FirstOrDefault(t => t.Config.Index == settings.SelectedChannelIndex);
            if (channel is not null)
                vm.SelectedTab = channel;
        }
    }

    private void SaveLayout()
    {
        var settings = AppSettings.Load();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = Math.Max(MinWidth, bounds.Width);
        settings.WindowHeight = Math.Max(MinHeight, bounds.Height);
        settings.WindowState = (WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal).ToString();

        SaveStarPair(MainLayoutGrid.ColumnDefinitions[0], MainLayoutGrid.ColumnDefinitions[2],
                     out var mainLeft, out var mainRight);
        settings.MainLeftPaneStar = mainLeft;
        settings.MainRightPaneStar = mainRight;

        SaveStarPair(LeftPaneGrid.RowDefinitions[0], LeftPaneGrid.RowDefinitions[2],
                 out var leftTop, out var leftBottom);
        settings.MainLeftTopPaneStar = leftTop;
        settings.MainLeftBottomPaneStar = leftBottom;

        SaveStarPair(RightPaneGrid.RowDefinitions[0], RightPaneGrid.RowDefinitions[2],
                 out var rightTop, out var rightBottom);
        settings.MainRightTopPaneStar = rightTop;
        settings.MainRightBottomPaneStar = rightBottom;

        // Keep legacy shared stars updated for backward compatibility with older builds.
        settings.MainTopPaneStar = rightTop ?? leftTop;
        settings.MainBottomPaneStar = rightBottom ?? leftBottom;

        SaveStarPair(SpectrumLayoutGrid.RowDefinitions[0], SpectrumLayoutGrid.RowDefinitions[2],
                     out var spectrumTop, out var spectrumBottom);
        settings.SpectrumTopPaneStar = spectrumTop;
        settings.SpectrumBottomPaneStar = spectrumBottom;

        SaveStarPair(MessagesLayoutGrid.RowDefinitions[0], MessagesLayoutGrid.RowDefinitions[2],
                     out var messagesTop, out var messagesBottom);
        settings.MessagesTopPaneStar = messagesTop;
        settings.MessagesBottomPaneStar = messagesBottom;

        CaptureConversationPaneLayout();
        settings.ConversationMessagesPaneStar = _conversationMessagesPaneStar;
        settings.ConversationRightPaneStar = _conversationRightPaneStar;
        settings.ConversationTelemetryPaneStar = _conversationTelemetryPaneStar;
        settings.ConversationLocationHistoryPaneStar = _conversationLocationHistoryPaneStar;

        settings.WaypointColumnWidths = SaveWaypointsColumnWidths();

        settings.IdentityExpanded = IdentityExpander.IsExpanded;

        settings.SelectedChannelIndex = -1;
        settings.SelectedConversationNode = 0;
        if (DataContext is MainViewModel vm)
        {
            switch (vm.SelectedTab)
            {
                case ChannelViewModel channel:
                    settings.SelectedChannelIndex = channel.Config.Index;
                    break;
                case ConversationViewModel conversation:
                    settings.SelectedConversationNode = conversation.NodeNum;
                    break;
            }

            settings.NodeFilterSearch        = vm.NodeSearchText;
            settings.NodeFilterHops          = vm.NodeHopsFilter;
            settings.NodeFilterKey           = vm.NodeKeyFilter;
            settings.NodeFilterLocation      = vm.NodeLocationFilter;
            settings.NodeFilterIgnored       = vm.NodeIgnoredFilter;
            settings.NodeFilterTemperature   = vm.NodeTemperatureFilter;
            settings.NodeFilterHumidity      = vm.NodeHumidityFilter;
            settings.NodeFilterPressure      = vm.NodePressureFilter;
            settings.MapNodeLabelMode        = vm.MapNodeLabelMode;
            settings.NodeFilterDistanceKm    = vm.NodeDistanceKmText;
            settings.NodeFilterMaxAgeMinutes = vm.NodeMaxAgeMinutesText;
        }

        Map.SaveToSettings(settings);
        settings.Save();
    }

    private void ConversationLayoutGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid root) return;
        ApplyConversationPaneLayout(root);
    }

    private void ConversationMainSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var layout = FindAncestorByName<Grid>(d, "ConversationLayoutGrid");
        if (layout is null || layout.ColumnDefinitions.Count < 3) return;
        SaveProportionalPair(layout.ColumnDefinitions[0], layout.ColumnDefinitions[2],
                             out _conversationMessagesPaneStar, out _conversationRightPaneStar);
    }

    private void ConversationTelemetrySplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not DependencyObject d) return;
        var panel = FindAncestorByName<Grid>(d, "ConversationTelemetryGrid");
        if (panel is null || panel.RowDefinitions.Count < 4) return;
        SaveStarPair(panel.RowDefinitions[1], panel.RowDefinitions[3],
                     out _conversationTelemetryPaneStar, out _conversationLocationHistoryPaneStar);
    }

    private void ApplyConversationPaneLayoutToCurrentTab()
    {
        if (MainTabs.SelectedItem is not ConversationViewModel) return;
        var tabItem = MainTabs.ItemContainerGenerator.ContainerFromItem(MainTabs.SelectedItem) as TabItem;
        if (tabItem is null) return;
        var layout = FindVisualDescendantByName<Grid>(tabItem, "ConversationLayoutGrid");
        if (layout is null) return;
        ApplyConversationPaneLayout(layout);
    }

    private void ApplyConversationPaneLayout(Grid root)
    {
        if (root.ColumnDefinitions.Count >= 3)
        {
            ApplyStarPair(root.ColumnDefinitions[0], _conversationMessagesPaneStar,
                          root.ColumnDefinitions[2], _conversationRightPaneStar);
        }

        var telemetry = FindVisualDescendantByName<Grid>(root, "ConversationTelemetryGrid");
        if (telemetry is not null && telemetry.RowDefinitions.Count >= 4)
        {
            ApplyStarPair(telemetry.RowDefinitions[1], _conversationTelemetryPaneStar,
                          telemetry.RowDefinitions[3], _conversationLocationHistoryPaneStar);
        }
    }

    private void CaptureConversationPaneLayout()
    {
        foreach (var item in MainTabs.Items)
        {
            if (item is not ConversationViewModel) continue;
            var tabItem = MainTabs.ItemContainerGenerator.ContainerFromItem(item) as TabItem;
            if (tabItem is null) continue;

            var layout = FindVisualDescendantByName<Grid>(tabItem, "ConversationLayoutGrid");
            if (layout is not null && layout.ColumnDefinitions.Count >= 3)
            {
                SaveProportionalPair(layout.ColumnDefinitions[0], layout.ColumnDefinitions[2],
                                     out _conversationMessagesPaneStar, out _conversationRightPaneStar);
            }

            var telemetry = FindVisualDescendantByName<Grid>(tabItem, "ConversationTelemetryGrid");
            if (telemetry is not null && telemetry.RowDefinitions.Count >= 4)
            {
                SaveStarPair(telemetry.RowDefinitions[1], telemetry.RowDefinitions[3],
                             out _conversationTelemetryPaneStar, out _conversationLocationHistoryPaneStar);
            }

            if (layout is not null || telemetry is not null)
                return;
        }
    }

    private static T? FindAncestorByName<T>(DependencyObject? start, string name)
        where T : FrameworkElement
    {
        var current = start;
        while (current is not null)
        {
            if (current is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
                return typed;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void SaveProportionalPair(ColumnDefinition firstColumn,
                                             ColumnDefinition secondColumn,
                                             out double? firstStar,
                                             out double? secondStar)
    {
        if (firstColumn.Width.IsStar && secondColumn.Width.IsStar)
        {
            firstStar = firstColumn.Width.Value;
            secondStar = secondColumn.Width.Value;
            return;
        }

        double first = firstColumn.ActualWidth;
        double second = secondColumn.ActualWidth;
        if (first > 0 && second > 0)
        {
            firstStar = first;
            secondStar = second;
            return;
        }

        firstStar = null;
        secondStar = null;
    }

    private static T? FindVisualDescendantByName<T>(DependencyObject? root, string name)
        where T : FrameworkElement
    {
        if (root is null) return null;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && string.Equals(typed.Name, name, StringComparison.Ordinal))
                return typed;

            var nested = FindVisualDescendantByName<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void SaveStarPair(System.Windows.Controls.ColumnDefinition firstColumn,
                                     System.Windows.Controls.ColumnDefinition secondColumn,
                                     out double? firstStar,
                                     out double? secondStar)
    {
        firstStar = firstColumn.Width.IsStar ? firstColumn.Width.Value : null;
        secondStar = secondColumn.Width.IsStar ? secondColumn.Width.Value : null;
    }

    private static void SaveStarPair(System.Windows.Controls.RowDefinition firstRow,
                                     System.Windows.Controls.RowDefinition secondRow,
                                     out double? firstStar,
                                     out double? secondStar)
    {
        firstStar = firstRow.Height.IsStar ? firstRow.Height.Value : null;
        secondStar = secondRow.Height.IsStar ? secondRow.Height.Value : null;
    }

    private void ApplyWaypointsColumnWidths(IReadOnlyList<double>? widths)
    {
        if (widths is null || widths.Count == 0) return;
        var cols = WaypointsGrid.Columns;
        int n = Math.Min(widths.Count, cols.Count);
        for (int i = 0; i < n; i++)
        {
            double w = widths[i];
            if (double.IsNaN(w) || double.IsInfinity(w) || w < 24.0) continue;
            cols[i].Width = new System.Windows.Controls.DataGridLength(
                w,
                System.Windows.Controls.DataGridLengthUnitType.Pixel);
        }
    }

    private List<double> SaveWaypointsColumnWidths()
    {
        var cols = WaypointsGrid.Columns;
        var widths = new List<double>(cols.Count);
        for (int i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            double w = col.ActualWidth;
            if (double.IsNaN(w) || double.IsInfinity(w) || w < 24.0)
                w = 24.0;
            widths.Add(w);
        }
        return widths;
    }
}
