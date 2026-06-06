// SPDX-License-Identifier: GPL-3.0-or-later
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using MeshRF.App.ViewModels;
using MeshRF.App.Views;

namespace MeshRF.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private float[] _spectrumBuffer = Array.Empty<float>();

    // Rolling history of recent spectrum frames, used to freeze a spectrogram
    // of the last detected packet. Holds the most recent HistoryFrames frames.
    private const int HistoryFrames = 64;
    private readonly Queue<float[]> _specHistory = new();

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

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
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
            mvm.PacketDecoded += OnPacketDecoded;
    }

    // Marks that a CRC-valid packet was just decoded. A bad frame or a false
    // positive (preamble that never decodes) never reaches here, so the
    // last-packet panel only ever shows genuine packets. The whole packet is
    // already buffered in the native IQ ring by the time it decodes, so we
    // snapshot immediately — any extra delay just ages the packet toward the
    // far end of the ring and risks the preamble scrolling out.
    private void OnPacketDecoded()
    {
        FreezeLastPacket();
    }

    private void OnCloseLastPacket(object sender, RoutedEventArgs e)
        => LastPacketPanel.Visibility = Visibility.Collapsed;

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

    // Context-menu "Traceroute" sends a Meshtastic-style route-discovery request
    // to the selected node (rate-limited to one per cooldown by the view model).
    private void OnTraceroute(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var node = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .FirstOrDefault();
        if (node is null) return;
        vm.Traceroute(node);
    }

    // Context-menu "Request position" asks the selected node to reply with its
    // location (rate-limited to one per cooldown by the view model).
    private void OnRequestPosition(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var node = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .FirstOrDefault();
        if (node is null) return;
        vm.RequestPosition(node);
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
        }
    }

    // Snapshots the last detected packet as a high-time-resolution STFT
    // spectrogram computed natively from buffered modem-rate IQ, cropped
    // (zoomed) to the LoRa channel so the individual chirps are visible.
    private void FreezeLastPacket()
    {
        if (DataContext is not MainViewModel vm) return;

        // Fine-grained STFT from native IQ history. Native code chooses the
        // packet-length time window; use enough rows that long frames do not
        // collapse into a cramped snapshot.
        const int nTime = 512;
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
