// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Window geometry and splitter-pane persistence, stored in settings.json
/// (WindowWidth/Height/Left/Top/WindowState plus the *PaneStar values).
/// </summary>
public partial class MainWindow
{
    private bool _layoutApplied;

    /// <summary>Restore window size/position/state and splitter proportions.
    /// Called once, before the window is shown.</summary>
    private void ApplyLayout(AppSettings settings)
    {
        ApplyWindowBounds(settings);

        ApplyStarPair(MainLayoutGrid.ColumnDefinitions[0], MainLayoutGrid.ColumnDefinitions[2],
                      settings.MainLeftPaneStar, settings.MainRightPaneStar);
        ApplyStarPair(LeftPaneGrid.RowDefinitions[0], LeftPaneGrid.RowDefinitions[2],
                      settings.MainLeftTopPaneStar, settings.MainLeftBottomPaneStar);
        ApplyStarPair(RightPaneGrid.RowDefinitions[0], RightPaneGrid.RowDefinitions[2],
                      settings.MainRightTopPaneStar, settings.MainRightBottomPaneStar);
        // Nodes/waypoints grid: row 3 = nodes, row 5 = waypoints, row 4 the splitter.
        ApplyStarPair(NodesWaypointsGrid.RowDefinitions[3], NodesWaypointsGrid.RowDefinitions[5],
                      settings.NodesPaneStar, settings.WaypointsPaneStar);
        ApplyStarPair(SpectrumLayoutGrid.RowDefinitions[0], SpectrumLayoutGrid.RowDefinitions[2],
                      settings.SpectrumTopPaneStar, settings.SpectrumBottomPaneStar);
        // Messages grid: row 1 = chat area (tabs + reply banner + composer),
        // row 3 = log; rows 0 and 2 are the header and the splitter.
        ApplyStarPair(MessagesLayoutGrid.RowDefinitions[1], MessagesLayoutGrid.RowDefinitions[3],
                      settings.MessagesTopPaneStar, settings.MessagesBottomPaneStar);

        ApplyLastPacketExpandedState(settings.LastPacketExpanded, persist: false);
        Map.Attach(_viewModel, settings);

        ApplyColumnWidths(NodesGridProxy, settings.NodeColumnWidths);
        ApplyColumnWidths(WaypointsGridProxy, settings.WaypointColumnWidths);

        // Restoring the sort has to wait for the grid: sorting through the
        // column (rather than poking the collection view) is what also lights
        // up the header's direction arrow, and columns aren't realized yet
        // when ApplyLayout runs from the constructor.
        _pendingNodeSortPath = settings.NodeSortMemberPath;
        _pendingNodeSortDescending = settings.NodeSortDescending;
        _lastNodeSort = (settings.NodeSortMemberPath ?? string.Empty, settings.NodeSortDescending);
        NodesGridProxy.Loaded += OnNodesGridLoaded;
        NodesGridProxy.Sorting += OnNodesGridSorting;

        _layoutApplied = true;
    }

    private string? _pendingNodeSortPath;
    private bool _pendingNodeSortDescending;
    private (string Path, bool Descending) _lastNodeSort;

    /// <summary>Records the sort as the user makes it. Sorting fires before the
    /// grid applies it, so read the resulting state on the next dispatcher
    /// pass — this way the choice survives even if the app exits without a
    /// clean Closing.</summary>
    private void OnNodesGridSorting(object? sender, DataGridColumnEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            var current = _viewModel.CurrentNodeSort;
            if (!string.IsNullOrEmpty(current.Path)) _lastNodeSort = current;
        }, DispatcherPriority.Background);

    private void OnNodesGridLoaded(object? sender, RoutedEventArgs e)
    {
        NodesGridProxy.Loaded -= OnNodesGridLoaded;
        if (string.IsNullOrWhiteSpace(_pendingNodeSortPath)) return;

        foreach (var column in NodesGridProxy.Columns)
        {
            if (!string.Equals(column.SortMemberPath, _pendingNodeSortPath, StringComparison.Ordinal))
                continue;
            // Sorting through the column also lights up the header arrow. Note
            // it does not populate the collection view's SortDescriptions the
            // way a user's header click does, which is why SaveLayout keeps the
            // _lastNodeSort fallback — otherwise a restored-but-untouched sort
            // would save as empty and be forgotten.
            column.Sort(_pendingNodeSortDescending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending);
            return;
        }
    }

    /// <summary>Restore saved pixel widths onto the grid's columns, in order.</summary>
    private static void ApplyColumnWidths(DataGrid grid, List<double>? widths)
    {
        if (widths is null) return;
        for (int i = 0; i < widths.Count && i < grid.Columns.Count; i++)
        {
            if (widths[i] > 0)
                grid.Columns[i].Width = new DataGridLength(widths[i], DataGridLengthUnitType.Pixel);
        }
    }

    private static List<double> SaveColumnWidths(DataGrid grid) =>
        grid.Columns.Select(c => Math.Round(c.ActualWidth)).ToList();

    private void ApplyWindowBounds(AppSettings settings)
    {
        double width = Math.Max(MinWidth > 0 ? MinWidth : 0, settings.WindowWidth ?? Width);
        double height = Math.Max(MinHeight > 0 ? MinHeight : 0, settings.WindowHeight ?? Height);

        if (settings.WindowLeft is double left && settings.WindowTop is double top &&
            IsVisibleOnAnyScreen(left, top, width, height))
        {
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        Width = width;
        Height = height;

        WindowState = string.Equals(settings.WindowState, nameof(WindowState.Maximized), StringComparison.OrdinalIgnoreCase)
            ? WindowState.Maximized
            : WindowState.Normal;
    }

    /// <summary>True if enough of the saved rectangle lands on a connected
    /// screen to grab — guards against restoring onto a monitor that's since
    /// been unplugged. Mirrors MeshRF.App's check against the virtual desktop.</summary>
    private bool IsVisibleOnAnyScreen(double left, double top, double width, double height)
    {
        var all = Screens?.All;
        if (all is null || all.Count == 0) return false;

        foreach (var screen in all)
        {
            var b = screen.Bounds;
            if (left + Math.Min(width, 80) > b.X &&
                top + Math.Min(height, 80) > b.Y &&
                left < b.X + b.Width - 40 &&
                top < b.Y + b.Height - 40)
                return true;
        }
        return false;
    }

    /// <summary>Persist window geometry and splitter proportions. Called on close.</summary>
    private void SaveLayout()
    {
        if (!_layoutApplied) return; // Never persist a layout we never applied.

        // Re-load rather than reusing the view model's instance: this writes a
        // different slice of the same file, and the view model may have saved
        // since. (MeshRF.App's SaveLayout does the same.)
        var settings = AppSettings.Load();

        // While maximized, Width/Height report the maximized size; FrameSize
        // isn't the restore size either. Only record geometry when normal, so
        // un-maximizing next launch restores the user's real window size.
        if (WindowState == WindowState.Normal)
        {
            settings.WindowLeft = Position.X;
            settings.WindowTop = Position.Y;
            settings.WindowWidth = Math.Max(MinWidth > 0 ? MinWidth : 0, Width);
            settings.WindowHeight = Math.Max(MinHeight > 0 ? MinHeight : 0, Height);
        }
        settings.WindowState = WindowState == WindowState.Maximized
            ? nameof(WindowState.Maximized)
            : nameof(WindowState.Normal);

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

        SaveStarPair(NodesWaypointsGrid.RowDefinitions[3], NodesWaypointsGrid.RowDefinitions[5],
                     out var nodesStar, out var waypointsStar);
        settings.NodesPaneStar = nodesStar;
        settings.WaypointsPaneStar = waypointsStar;

        SaveStarPair(SpectrumLayoutGrid.RowDefinitions[0], SpectrumLayoutGrid.RowDefinitions[2],
                     out var specTop, out var specBottom);
        settings.SpectrumTopPaneStar = specTop;
        settings.SpectrumBottomPaneStar = specBottom;

        SaveStarPair(MessagesLayoutGrid.RowDefinitions[1], MessagesLayoutGrid.RowDefinitions[3],
                     out var msgTop, out var msgBottom);
        settings.MessagesTopPaneStar = msgTop;
        settings.MessagesBottomPaneStar = msgBottom;

        settings.LastPacketExpanded = _lastPacketExpanded;
        Map.SaveToSettings(settings);

        settings.NodeColumnWidths = SaveColumnWidths(NodesGridProxy);
        settings.WaypointColumnWidths = SaveColumnWidths(WaypointsGridProxy);

        var (sortPath, sortDescending) = _viewModel.CurrentNodeSort;
        if (string.IsNullOrEmpty(sortPath)) (sortPath, sortDescending) = _lastNodeSort;
        settings.NodeSortMemberPath = sortPath;
        settings.NodeSortDescending = sortDescending;

        settings.Save();
    }

    private static void ApplyStarPair(DefinitionBase first, DefinitionBase second, double? firstStar, double? secondStar)
    {
        if (firstStar is not > 0 || secondStar is not > 0) return;
        SetStar(first, firstStar.Value);
        SetStar(second, secondStar.Value);
    }

    private static void SaveStarPair(DefinitionBase first, DefinitionBase second, out double firstStar, out double secondStar)
    {
        firstStar = GetStar(first);
        secondStar = GetStar(second);
    }

    // ColumnDefinition/RowDefinition don't share a Width/Height member, so the
    // star value has to be read/written through the concrete type.
    private static void SetStar(DefinitionBase def, double star)
    {
        switch (def)
        {
            case ColumnDefinition c: c.Width = new GridLength(star, GridUnitType.Star); break;
            case RowDefinition r: r.Height = new GridLength(star, GridUnitType.Star); break;
        }
    }

    private static double GetStar(DefinitionBase def) => def switch
    {
        // Prefer the measured pixel size: it's the proportion the user actually
        // dragged to. The stored pair is only ever used as a ratio.
        ColumnDefinition c => c.ActualWidth > 0 ? c.ActualWidth : c.Width.Value,
        RowDefinition r => r.ActualHeight > 0 ? r.ActualHeight : r.Height.Value,
        _ => 1.0,
    };
}
