// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
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

        // Restore window geometry / splitter proportions before first show.
        ApplyLayout(AppSettings.Load());

        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16), // ~60 Hz — matches the native pull-rate assumption above.
        };
        _spectrumTimer.Tick += (_, _) => PullSpectrum();
        _spectrumTimer.Start();

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

        double rowsPerSecond = Math.Clamp(_viewModel.WaterfallRowsPerSecond, 5, 240);
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

    private async void OnEditWaypoint(object? sender, RoutedEventArgs e)
    {
        if (WaypointsGridProxy.SelectedItem is not WaypointRecord wp) return;
        var result = await WaypointEditWindow.EditAsync(this, wp);
        if (result is null) return;
        await _viewModel.UpdateWaypointAsync(wp, result.Name, result.Description, result.Latitude, result.Longitude);
    }

    /// <summary>"React…" — pick a glyph, then send it as a tapback.</summary>
    private async void OnReactPick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelMessage message }) return;
        var glyph = await EmojiPickerWindow.PickAsync(this);
        if (string.IsNullOrEmpty(glyph)) return;
        await _viewModel.SendReactionAsync(message, glyph);
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

    private void OnClearMessages(object? sender, RoutedEventArgs e) =>
        _viewModel.SelectedTab?.Messages.Clear();

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, _viewModel.LogLines);
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    private void OnClearLog(object? sender, RoutedEventArgs e) => _viewModel.LogLines.Clear();

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

    private void OnOpenChannelSettings(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ChannelTabViewModel channel }) return;
        ChannelSettingsWindow.Open(this, _viewModel, channel);
    }
}
