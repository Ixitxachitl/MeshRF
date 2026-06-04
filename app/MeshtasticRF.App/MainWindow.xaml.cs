// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using MeshtasticRF.App.ViewModels;
using MeshtasticRF.App.Views;

namespace MeshtasticRF.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private float[] _spectrumBuffer = Array.Empty<float>();

    // Rolling history of recent spectrum frames, used to freeze a spectrogram
    // of the last detected packet. Holds the most recent HistoryFrames frames.
    private const int HistoryFrames = 64;
    private readonly Queue<float[]> _specHistory = new();
    // Frames still to capture after a packet was detected before snapshotting,
    // so the snapshot includes the header/payload that follow the preamble.
    // -1 means "no capture pending".
    private int _captureCountdown = -1;
    private bool _packetPending;

    public MainWindow()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50), // 20 Hz
        };
        _timer.Tick += OnTick;
        Loaded   += OnLoaded;
        Unloaded += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _timer.Start();
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
            mvm.PacketDetected += OnPacketDetected;
    }

    // Marks that a packet was just detected. We keep capturing a few more
    // frames (so the snapshot covers the header/payload that trail the
    // preamble) before freezing the spectrogram.
    private void OnPacketDetected()
    {
        _packetPending = true;
        // Wait just long enough for the full (short) packet to trail into the
        // IQ ring, then snapshot. The ring only holds ~0.5 s, so we must grab
        // it well before that or the packet scrolls out and we capture silence.
        _captureCountdown = 4; // ~0.2 s at 20 Hz
    }

    private void OnCloseLastPacket(object sender, RoutedEventArgs e)
        => LastPacketPanel.Visibility = Visibility.Collapsed;

    // Double-clicking a node row opens (or focuses) a DM conversation tab.
    private void NodesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is System.Windows.Controls.DataGrid grid &&
            grid.SelectedItem is MeshtasticRF.Nodes.NodeRecord node)
        {
            vm.OpenConversationForNodeCommand.Execute(node);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        vm.RefreshStats();

        // Don't pull spectrum when stopped — the native side caches the
        // last frame and would keep scrolling the waterfall.
        if (!vm.IsRunning) return;

        // Apply current colormap selection.
        Waterfall.Colormap = vm.WaterfallColormap == "Inferno"
            ? WaterfallColormap.Inferno
            : WaterfallColormap.Turbo;

        var n = vm.Core.SpectrumSize;
        if (n <= 0) return;
        if (_spectrumBuffer.Length != n) _spectrumBuffer = new float[n];

        // Keep the frequency-axis span in sync with the device sample rate.
        var rate = vm.Core.SampleRateHz;
        if (rate > 0) vm.SpectrumSpanHz = rate;

        var written = vm.Core.PullSpectrum(_spectrumBuffer);
        if (written > 0)
        {
            Spectrum.Update(_spectrumBuffer.AsSpan(0, written));
            Waterfall.Push(_spectrumBuffer.AsSpan(0, written));

            // Keep a rolling history so we can freeze the last packet.
            var frame = _spectrumBuffer.AsSpan(0, written).ToArray();
            _specHistory.Enqueue(frame);
            while (_specHistory.Count > HistoryFrames) _specHistory.Dequeue();

            // If a packet was detected, capture a few trailing frames then
            // snapshot the accumulated history into the last-packet panel.
            if (_packetPending && _captureCountdown > 0)
            {
                if (--_captureCountdown == 0)
                {
                    FreezeLastPacket();
                    _packetPending = false;
                    _captureCountdown = -1;
                }
            }
        }
    }

    // Snapshots the last detected packet as a high-time-resolution STFT
    // spectrogram computed natively from buffered modem-rate IQ, cropped
    // (zoomed) to the LoRa channel so the individual chirps are visible.
    private void FreezeLastPacket()
    {
        if (DataContext is not MainViewModel vm) return;

        // Fine-grained STFT from native IQ history: nTime rows over ~150 ms,
        // each nFreq wide across the cropped channel. This resolves the chirp
        // sweeps that the 20 Hz waterfall history cannot.
        const int nTime = 256;
        const int nFreq = 256;
        var grid = new float[nTime * nFreq];
        int rows = vm.Core.PullPacketSpectrogram(grid, nTime, nFreq);
        if (rows <= 0)
        {
            // Fallback: replay the coarse waterfall history if no IQ yet.
            FreezeLastPacketFromHistory(vm);
            return;
        }

        LastPacket.Clear();
        for (int t = 0; t < rows; t++)
        {
            var row = new float[nFreq];
            Array.Copy(grid, t * nFreq, row, 0, nFreq);
            LastPacket.Push(row);
        }
        LastPacketTitle.Text = $"Last packet  {DateTime.Now:HH:mm:ss}";
        LastPacketPanel.Visibility = Visibility.Visible;
    }

    // Fallback: replays the rolling history into the frozen last-packet
    // spectrogram, cropped (zoomed) to just the LoRa channel around DC.
    private void FreezeLastPacketFromHistory(MainViewModel vm)
    {
        if (_specHistory.Count == 0) return;

        // The spectrum spans the full device sample rate, centered on DC
        // (LoRa is offset-tuned to DC). The channel is 250 kHz wide; show a
        // little margin (1.5x) so the chirp edges are visible.
        const double zoomHz = 350_000.0; // ~1.4x the 250 kHz channel
        // Fall back to the known device rate (2.4 MHz) if the VM hasn't been
        // ticked yet, so the snapshot is still zoomed on the very first packet.
        double spanHz = vm.SpectrumSpanHz > 0 ? vm.SpectrumSpanHz
                       : vm.Core.SampleRateHz > 0 ? vm.Core.SampleRateHz
                       : 2_400_000.0;

        int binCount = _specHistory.Peek().Length;
        int half = (int)Math.Round(zoomHz / spanHz * binCount / 2.0);
        half = Math.Clamp(half, 16, binCount / 2);
        int center = binCount / 2;
        int lo = center - half;
        int width = half * 2;

        LastPacket.Clear();
        var slice = new float[width];
        foreach (var f in _specHistory)
        {
            Array.Copy(f, lo, slice, 0, width);
            LastPacket.Push((float[])slice.Clone());
        }
        LastPacketTitle.Text = $"Last packet  {DateTime.Now:HH:mm:ss}";
        LastPacketPanel.Visibility = Visibility.Visible;
    }
}
