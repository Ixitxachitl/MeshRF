// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Attached property that keeps a <see cref="DataGrid"/> pinned to its newest
/// (last) row: once when the grid is bound, and again whenever a row is
/// appended. The history windows sort oldest-first, so the newest sample is
/// the one at the bottom.
/// </summary>
/// <remarks>
/// The list counterpart is <see cref="AutoScrollBehavior"/>. This one is
/// separate rather than generalised because DataGrid is not an ItemsControl in
/// Avalonia — it has its own ScrollIntoView and no shared base to hang the
/// property off.
/// </remarks>
public static class DataGridAutoScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("IsEnabled", typeof(DataGridAutoScrollBehavior));

    public static void SetIsEnabled(DataGrid target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DataGrid target) => target.GetValue(IsEnabledProperty);

    private static readonly Dictionary<DataGrid, (INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler)> Hooks = new();

    static DataGridAutoScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true) Attach(grid);
            else Detach(grid);
        });

        // The property is usually applied before the ItemsSource binding
        // resolves, so the first Attach finds nothing to hook. Re-attach
        // whenever the source appears or is swapped.
        DataGrid.ItemsSourceProperty.Changed.AddClassHandler<DataGrid>((grid, _) =>
        {
            if (GetIsEnabled(grid)) Attach(grid);
        });
    }

    private static void Attach(DataGrid grid)
    {
        Detach(grid);
        if (grid.ItemsSource is not INotifyCollectionChanged incc) return;

        void Handler(object? _, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
            // Deferred: the new row has no container until the grid has
            // processed the change.
            Dispatcher.UIThread.Post(() => ScrollToEnd(grid), DispatcherPriority.Background);
        }

        incc.CollectionChanged += Handler;
        Hooks[grid] = (incc, Handler);

        // History is loaded before the window opens, so there is no collection
        // change to ride in on — pin explicitly. One pass is not always enough
        // for the row heights to settle, so keep nudging (bounded, so a grid
        // that cannot scroll does not spin).
        ScheduleInitialScroll(grid, attempts: 5);
    }

    private static void ScheduleInitialScroll(DataGrid grid, int attempts)
    {
        if (attempts <= 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!GetIsEnabled(grid)) return;
            ScrollToEnd(grid);
            ScheduleInitialScroll(grid, attempts - 1);
        }, DispatcherPriority.Loaded);
    }

    private static void Detach(DataGrid grid)
    {
        if (!Hooks.Remove(grid, out var hook)) return;
        hook.Source.CollectionChanged -= hook.Handler;
    }

    private static void ScrollToEnd(DataGrid grid)
    {
        if (grid.ItemsSource is not IList { Count: > 0 } items) return;
        try { grid.ScrollIntoView(items[^1], null); }
        catch { /* grid torn down mid-post */ }
    }
}
