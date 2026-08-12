// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Attached property that keeps a ListBox pinned to its newest (last) item as
/// items are appended. Port of MeshRF.App's Behaviors/AutoScroll, used by the
/// chat lists so new traffic stays in view.
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("IsEnabled", typeof(AutoScrollBehavior));

    public static void SetIsEnabled(ListBox target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(ListBox target) => target.GetValue(IsEnabledProperty);

    // Keeps the per-list handler so it can be detached when disabled or when
    // the list is re-bound to a different collection.
    private static readonly Dictionary<ListBox, (INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler)> Hooks = new();

    static AutoScrollBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            if (e.NewValue is true) Attach(list);
            else Detach(list);
        });

        // The attached property is often applied before the ItemsSource binding
        // resolves — and inside a DataTemplate it usually is. Without this the
        // first Attach finds a null ItemsSource, gives up, and the list never
        // scrolls or hooks. Re-attach whenever the source appears or changes.
        ItemsControl.ItemsSourceProperty.Changed.AddClassHandler<ListBox>((list, _) =>
        {
            if (GetIsEnabled(list)) Attach(list);
        });
    }

    private static void Attach(ListBox list)
    {
        Detach(list);
        if (list.ItemsSource is not INotifyCollectionChanged incc) return;

        void Handler(object? _, NotifyCollectionChangedEventArgs args)
        {
            if (args.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
            // Defer: the container for the new item doesn't exist until the
            // list has processed the change.
            Dispatcher.UIThread.Post(() => ScrollToEnd(list), DispatcherPriority.Background);
        }

        incc.CollectionChanged += Handler;
        Hooks[list] = (incc, Handler);

        // History is already loaded by the time this attaches, so there's no
        // CollectionChanged to react to — scroll explicitly. Containers aren't
        // realized until the list has been laid out, and one pass isn't always
        // enough for a virtualizing panel to settle, so keep nudging it until
        // the last item is actually at the bottom (bounded, so a list that
        // can't scroll doesn't spin).
        ScheduleInitialScroll(list, attempts: 5);
    }

    private static void ScheduleInitialScroll(ListBox list, int attempts)
    {
        if (attempts <= 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!GetIsEnabled(list)) return;
            ScrollToEnd(list);
            ScheduleInitialScroll(list, attempts - 1);
        }, DispatcherPriority.Loaded);
    }

    private static void Detach(ListBox list)
    {
        if (!Hooks.Remove(list, out var hook)) return;
        hook.Source.CollectionChanged -= hook.Handler;
    }

    private static void ScrollToEnd(ListBox list)
    {
        if (list.ItemCount == 0) return;
        try { list.ScrollIntoView(list.ItemCount - 1); }
        catch { /* list torn down mid-post */ }
    }
}
