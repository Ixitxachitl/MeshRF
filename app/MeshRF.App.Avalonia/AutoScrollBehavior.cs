// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF.Messages;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Attached property that scrolls a ListBox to its newest (last) item when one
/// is appended. Port of MeshRF.App's Behaviors/AutoScroll, used by the chat
/// lists so new traffic stays in view.
///
/// Scrolls on a new entry and on nothing else: between entries the list is the
/// user's to scroll, so the wheel works normally and reading back through
/// history is not interrupted until the next message arrives.
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

    /// <summary>
    /// Per-list subscriptions to each message's own reaction list.
    /// </summary>
    /// <remarks>
    /// A reaction makes a row taller without adding one, and a virtualizing
    /// panel estimates its total extent from the heights of the rows it has
    /// realized — so one row growing re-estimates the whole thing. Measured on
    /// a 500-message list, two chips on the newest message moved the extent by
    /// nine thousand pixels, which left a view pinned to the bottom that far
    /// above it. Following the reaction itself is what keeps the newest message
    /// in view.
    /// </remarks>
    private static readonly Dictionary<ListBox, List<(INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler)>> ReactionHooks = new();

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

    // Deliberately no ScrollChanged hook. An earlier revision chased the scroll
    // extent, to follow content that grew taller without gaining items. It made
    // the wheel unusable: a virtualizing panel's extent changes as containers
    // realize during a scroll, so the first tick away from the bottom looked
    // like in-place growth while the list still counted as "at the bottom", and
    // the view was pulled straight back down.
    //
    // A reaction is caught by watching the thing that actually happened — the
    // row's own reaction list — rather than by watching the extent. That fires
    // once, on a real event, and never during a scroll, so it cannot fight the
    // wheel the way extent-chasing did.

    private static void Attach(ListBox list)
    {
        Detach(list);
        if (list.ItemsSource is not INotifyCollectionChanged incc) return;

        void Handler(object? _, NotifyCollectionChangedEventArgs args)
        {
            // Re-hooked on any change: a message that has just arrived can be
            // reacted to, and one that has aged out of the cap must not be left
            // holding a subscription.
            HookReactions(list);

            if (args.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
            // Defer: the container for the new item doesn't exist until the
            // list has processed the change.
            Dispatcher.UIThread.Post(() => ScrollToEnd(list), DispatcherPriority.Background);
        }

        incc.CollectionChanged += Handler;
        Hooks[list] = (incc, Handler);
        HookReactions(list);

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

    /// <summary>
    /// Subscribes to every visible message's reactions, replacing whatever was
    /// subscribed before.
    /// </summary>
    /// <remarks>
    /// Rebuilt wholesale rather than diffed: the list is capped at a few
    /// hundred, and a diff would have to be right about every way a message
    /// leaves it to avoid either a leak or a row that silently stops following.
    /// </remarks>
    private static void HookReactions(ListBox list)
    {
        UnhookReactions(list);
        if (list.ItemsSource is not IEnumerable<object> items) return;

        var hooks = new List<(INotifyCollectionChanged, NotifyCollectionChangedEventHandler)>();
        foreach (var item in items)
        {
            if (item is not ChannelMessage message) continue;

            void OnReacted(object? _, NotifyCollectionChangedEventArgs __) => KeepPinned(list);
            message.Reactions.CollectionChanged += OnReacted;
            hooks.Add((message.Reactions, OnReacted));
        }

        if (hooks.Count > 0) ReactionHooks[list] = hooks;
    }

    private static void UnhookReactions(ListBox list)
    {
        if (!ReactionHooks.Remove(list, out var hooks)) return;
        foreach (var (source, handler) in hooks) source.CollectionChanged -= handler;
    }

    /// <summary>
    /// Holds the view at the bottom across a row growing, and does nothing
    /// anywhere else.
    /// </summary>
    /// <remarks>
    /// Whether we were at the bottom is read now, before the layout that the
    /// new chip will trigger — afterwards the extent has already moved and the
    /// answer is meaningless. Someone reading back through history is left
    /// alone, which is the half the old extent-chasing got wrong.
    /// </remarks>
    private static void KeepPinned(ListBox list)
    {
        if (!AtBottom(list)) return;
        Dispatcher.UIThread.Post(() => ScrollToEnd(list), DispatcherPriority.Background);
    }

    /// <summary>Within a couple of pixels of the end, which is what "pinned"
    /// means after a fractional layout.</summary>
    private static bool AtBottom(ListBox list)
    {
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return false;

        double hidden = scroll.Extent.Height - scroll.Viewport.Height;
        return hidden <= 0 || scroll.Offset.Y >= hidden - 2;
    }

    private static void Detach(ListBox list)
    {
        UnhookReactions(list);
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
