// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>Periodic layout save, so the layout does not depend on the app
    /// being closed properly to survive.</summary>
    private DispatcherTimer? _layoutAutoSave;

    /// <summary>Layout as it was at the last save, so the autosave writes only
    /// when something has actually moved.</summary>
    private string _savedLayoutSignature = string.Empty;

    /// <summary>Restore window size/position/state and splitter proportions.
    /// Called once, before the window is shown.</summary>
    private void ApplyLayout(AppSettings settings)
    {
        ApplyWindowBounds(settings);

        // The splits between the six panels, and which of them are in windows
        // of their own, are restored together: a popped-out panel collapses
        // the pane it left, so the two cannot be applied independently.
        BuildPanels();
        RestorePanels(settings);

        ApplyStarPair(SpectrumLayoutGrid.RowDefinitions[0], SpectrumLayoutGrid.RowDefinitions[2],
                      settings.SpectrumTopPaneStar, settings.SpectrumBottomPaneStar);

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
        StartLayoutAutoSave();
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

    /// <summary>Restore saved widths onto the grid's columns, in order. A
    /// column declared star-sized stays star-sized, with the saved width as
    /// its weight: the proportion the user dragged survives while the column
    /// still follows the pane, where a pixel width would freeze it.</summary>
    private static void ApplyColumnWidths(DataGrid grid, List<double>? widths)
    {
        if (widths is null) return;
        for (int i = 0; i < widths.Count && i < grid.Columns.Count; i++)
        {
            if (widths[i] <= 0) continue;
            var column = grid.Columns[i];
            column.Width = new DataGridLength(widths[i],
                column.Width.IsStar ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
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

    /// <summary>Persist window geometry and splitter proportions. Called on
    /// close and from the autosave tick.</summary>
    private void SaveLayout()
    {
        if (!_layoutApplied) return; // Never persist a layout we never applied.

        // Re-load rather than reusing the view model's instance: this writes a
        // different slice of the same file, and the view model may have saved
        // since. (MeshRF.App's SaveLayout does the same.)
        var settings = AppSettings.Load();
        CaptureLayout(settings);
        _savedLayoutSignature = LayoutSignature();
        settings.Save();
    }

    /// <summary>
    /// Writes the layout out again when it has moved since the last save.
    /// Closing is the only other thing that saves it, and a machine that dies
    /// with the app running never reaches Closing: without this, a crash that
    /// has nothing to do with this app costs a whole session of splitter,
    /// column and map adjustments.
    /// </summary>
    private void SaveLayoutIfChanged()
    {
        if (!_layoutApplied) return;
        if (LayoutSignature() == _savedLayoutSignature) return;
        SaveLayout();
    }

    /// <summary>The layout as it stands, captured onto a throwaway settings
    /// object. Comparing it against the last saved one keeps the autosave off
    /// the disk while nothing is being dragged, and it covers whatever
    /// <see cref="CaptureLayout"/> writes without a second list to keep in
    /// step with it.</summary>
    private string LayoutSignature()
    {
        var probe = new AppSettings();
        CaptureLayout(probe);
        return JsonSerializer.Serialize(probe);
    }

    private void StartLayoutAutoSave()
    {
        _savedLayoutSignature = LayoutSignature();
        _layoutAutoSave ??= new DispatcherTimer(
            TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => SaveLayoutIfChanged());
        _layoutAutoSave.Start();
    }

    private void StopLayoutAutoSave() => _layoutAutoSave?.Stop();

    /// <summary>Reads the live layout out of the visual tree onto
    /// <paramref name="settings"/>. Everything else on it is left alone.</summary>
    private void CaptureLayout(AppSettings settings)
    {
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

        CapturePanels(settings);

        SaveStarPair(SpectrumLayoutGrid.RowDefinitions[0], SpectrumLayoutGrid.RowDefinitions[2],
                     out var specTop, out var specBottom);
        settings.SpectrumTopPaneStar = specTop;
        settings.SpectrumBottomPaneStar = specBottom;

        settings.LastPacketExpanded = _lastPacketExpanded;
        Map.SaveToSettings(settings);

        settings.NodeColumnWidths = SaveColumnWidths(NodesGridProxy);
        settings.WaypointColumnWidths = SaveColumnWidths(WaypointsGridProxy);

        var (sortPath, sortDescending) = _viewModel.CurrentNodeSort;
        if (string.IsNullOrEmpty(sortPath)) (sortPath, sortDescending) = _lastNodeSort;
        settings.NodeSortMemberPath = sortPath;
        settings.NodeSortDescending = sortDescending;
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

    /// <summary>Fixed size, in pixels. Zero is how a pane whose panel has been
    /// popped out is collapsed, along with the splitter beside it.</summary>
    private static void SetPixels(DefinitionBase def, double px)
    {
        switch (def)
        {
            case ColumnDefinition c: c.Width = new GridLength(px, GridUnitType.Pixel); break;
            case RowDefinition r: r.Height = new GridLength(px, GridUnitType.Pixel); break;
        }
    }

    /// <summary>MinWidth on a column, MinHeight on a row.</summary>
    private static void SetMinimum(DefinitionBase def, double min)
    {
        switch (def)
        {
            case ColumnDefinition c: c.MinWidth = min; break;
            case RowDefinition r: r.MinHeight = min; break;
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
