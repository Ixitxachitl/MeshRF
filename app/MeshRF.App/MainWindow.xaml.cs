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
    private bool _layoutApplied;

    // Rolling history of recent spectrum frames, used to freeze a spectrogram
    // of the last detected packet. Holds the most recent HistoryFrames frames.
    private const int HistoryFrames = 64;
    private readonly Queue<float[]> _specHistory = new();

    public MainWindow()
    {
        InitializeComponent();
        ApplySavedLayout();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50), // 20 Hz
        };
        _timer.Tick += OnTick;
        Loaded   += OnLoaded;
        Closing  += OnClosing;
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

    // Context-menu "Exchange node info" asks the selected node(s) to reply
    // with NodeInfo without clearing any stored keys.
    private void OnExchangeNodeInfo(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .ToList();
        if (selected.Count == 0) return;
        vm.RequestNodeInfo(selected);
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

        // Keep the frequency-axis span and centre in sync with the running pipeline.
        var rate = vm.Core.SampleRateHz;
        if (rate > 0) vm.SpectrumSpanHz = rate;
        var centre = vm.Core.SpectrumCenterHz;
        if (centre > 0) vm.SpectrumCenterHz = centre;

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

    private void ApplySavedLayout()
    {
        var settings = AppSettings.Load();

        ApplyWindowBounds(settings);
        ApplyStarPair(MainLayoutGrid.ColumnDefinitions[0], settings.MainLeftPaneStar,
                      MainLayoutGrid.ColumnDefinitions[2], settings.MainRightPaneStar);
        ApplyStarPair(MainLayoutGrid.RowDefinitions[0], settings.MainTopPaneStar,
                      MainLayoutGrid.RowDefinitions[2], settings.MainBottomPaneStar);
        ApplyStarPair(SpectrumLayoutGrid.RowDefinitions[0], settings.SpectrumTopPaneStar,
                      SpectrumLayoutGrid.RowDefinitions[2], settings.SpectrumBottomPaneStar);
        ApplyStarPair(MessagesLayoutGrid.RowDefinitions[0], settings.MessagesTopPaneStar,
                      MessagesLayoutGrid.RowDefinitions[2], settings.MessagesBottomPaneStar);

        IdentityExpander.IsExpanded = settings.IdentityExpanded;
        RestoreSelectedTab(settings);
        Map.LoadFromSettings(settings);
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

        SaveStarPair(MainLayoutGrid.RowDefinitions[0], MainLayoutGrid.RowDefinitions[2],
                     out var mainTop, out var mainBottom);
        settings.MainTopPaneStar = mainTop;
        settings.MainBottomPaneStar = mainBottom;

        SaveStarPair(SpectrumLayoutGrid.RowDefinitions[0], SpectrumLayoutGrid.RowDefinitions[2],
                     out var spectrumTop, out var spectrumBottom);
        settings.SpectrumTopPaneStar = spectrumTop;
        settings.SpectrumBottomPaneStar = spectrumBottom;

        SaveStarPair(MessagesLayoutGrid.RowDefinitions[0], MessagesLayoutGrid.RowDefinitions[2],
                     out var messagesTop, out var messagesBottom);
        settings.MessagesTopPaneStar = messagesTop;
        settings.MessagesBottomPaneStar = messagesBottom;

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
            settings.NodeFilterDistanceKm    = vm.NodeDistanceKmText;
            settings.NodeFilterMaxAgeMinutes = vm.NodeMaxAgeMinutesText;
        }

        Map.SaveToSettings(settings);
        settings.Save();
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
}
