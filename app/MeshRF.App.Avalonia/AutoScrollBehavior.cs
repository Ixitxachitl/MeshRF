// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF.Messages;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Attached behaviour that keeps a chat ListBox showing what the reader cares
/// about as its content changes underneath them. Two different things, and the
/// toggle only governs one:
///
/// <list type="bullet">
/// <item>A new message arrives — the list jumps to it, so a reply lands in
/// view. This is what the AutoScroll toggle turns on and off.</item>
/// <item>A message already in the list grows — a reaction chip appears, a
/// delivery mark lands — and the view holds still, so the message that changed
/// stays exactly where the reader was looking at it. This is a correction for
/// how a virtualizing panel measures, not a preference, so it runs whether the
/// toggle is on or off.</item>
/// </list>
///
/// Between those two events the list is the user's to scroll: the wheel works
/// normally and reading back through history is never interrupted.
/// </summary>
public static class AutoScrollBehavior
{
    /// <summary>Whether a newly added message pulls the view to the bottom.
    /// Holding a growing message in view does not depend on this.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("IsEnabled", typeof(AutoScrollBehavior));

    public static void SetIsEnabled(ListBox target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(ListBox target) => target.GetValue(IsEnabledProperty);

    // Keeps the per-list handler so it can be detached when the list is
    // re-bound to a different collection.
    private static readonly Dictionary<ListBox, (INotifyCollectionChanged Source, NotifyCollectionChangedEventHandler Handler)> Hooks = new();

    /// <summary>
    /// Per-list subscriptions to each message's own changes, held as the calls
    /// that undo them.
    /// </summary>
    /// <remarks>
    /// A row can grow without the list gaining one, and there is no collection
    /// event for that — so each message is watched directly. Two things do it:
    /// a reaction chip appearing, and a delivery mark landing on an outgoing
    /// message, which repaints the body's inlines and can rewrap the text.
    /// </remarks>
    private static readonly Dictionary<ListBox, List<Action>> RowHooks = new();

    /// <summary>Lists holding a message in place right now, so a burst of
    /// changes — an ACK and the glyph it raises, a display refresh touching
    /// every row — collapses into one correction against one anchor.</summary>
    private static readonly HashSet<ListBox> Holding = new();

    /// <summary>Which correction each list is currently running. A new message
    /// arriving mid-correction takes the list over rather than being dropped:
    /// jumping to it is the stronger intent, and the two would otherwise pull
    /// the view in opposite directions.</summary>
    private static readonly Dictionary<ListBox, int> Sequence = new();

    private static int Claim(ListBox list)
    {
        int token = Sequence.TryGetValue(list, out var current) ? current + 1 : 1;
        Sequence[list] = token;
        return token;
    }

    private static bool StillOwns(ListBox list, int token) =>
        Sequence.TryGetValue(list, out var current) && current == token;

    static AutoScrollBehavior()
    {
        // Attached either way. The value decides whether a new message pulls
        // the view down, and is read at the moment one arrives; the hooks that
        // hold a growing message in place are needed regardless, and detaching
        // them with the toggle is what once left the view drifting whenever
        // AutoScroll was off.
        IsEnabledProperty.Changed.AddClassHandler<ListBox>((list, _) => Attach(list));

        // The attached property is often applied before the ItemsSource binding
        // resolves — and inside a DataTemplate it usually is. Without this the
        // first Attach finds a null ItemsSource, gives up, and the list never
        // scrolls or hooks. Re-attach whenever the source appears or changes.
        ItemsControl.ItemsSourceProperty.Changed.AddClassHandler<ListBox>((list, _) => Attach(list));
    }

    // Deliberately no ScrollChanged hook. An earlier revision chased the scroll
    // extent, to follow content that grew taller without gaining items. It made
    // the wheel unusable: a virtualizing panel's extent changes as containers
    // realize during a scroll, so the first tick away from the bottom looked
    // like in-place growth while the list still counted as "at the bottom", and
    // the view was pulled straight back down.
    //
    // Growth is caught by watching the thing that actually happened — the row's
    // own reaction list, its own delivery mark. Those fire once, on a real
    // event, and never during a scroll, so they cannot fight the wheel.

    private static void Attach(ListBox list)
    {
        Detach(list);
        if (list.ItemsSource is not INotifyCollectionChanged incc) return;

        void Handler(object? _, NotifyCollectionChangedEventArgs args)
        {
            // Re-hooked on any change: a message that has just arrived can be
            // reacted to or acknowledged, and one that has aged out of the cap
            // must not be left holding a subscription.
            HookRows(list);

            if (!GetIsEnabled(list)) return;
            if (args.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
            // Deferred, and more than once: the container for the new item
            // doesn't exist until the list has processed the change, and a row
            // taller than the panel's estimate — a reply, which carries the
            // quoted original above its own body — moves the extent again as it
            // measures, leaving a single scroll short of the bottom.
            ScrollToEndSettling(list);
        }

        incc.CollectionChanged += Handler;
        Hooks[list] = (incc, Handler);
        HookRows(list);

        // History is already loaded by the time this attaches, so there's no
        // CollectionChanged to react to — scroll explicitly. Containers aren't
        // realized until the list has been laid out, and one pass isn't always
        // enough for a virtualizing panel to settle, so keep nudging it until
        // the last item is actually at the bottom (bounded, so a list that
        // can't scroll doesn't spin).
        if (GetIsEnabled(list)) ScheduleInitialScroll(list, attempts: 5);
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
    /// Subscribes to every visible message's reactions and property changes,
    /// replacing whatever was subscribed before.
    /// </summary>
    /// <remarks>
    /// Rebuilt wholesale rather than diffed: the list is capped at a few
    /// hundred, and a diff would have to be right about every way a message
    /// leaves it to avoid either a leak or a row that silently stops following.
    /// </remarks>
    private static void HookRows(ListBox list)
    {
        UnhookRows(list);
        if (list.ItemsSource is not IEnumerable<object> items) return;

        var undo = new List<Action>();
        foreach (var item in items)
        {
            if (item is not ChannelMessage message) continue;

            void OnReacted(object? _, NotifyCollectionChangedEventArgs __) => HoldInView(list, message);
            message.Reactions.CollectionChanged += OnReacted;
            undo.Add(() => message.Reactions.CollectionChanged -= OnReacted);

            // Every property, not the delivery mark alone: any of them can
            // rewrap the body or the header, and holding a message that didn't
            // actually move costs a comparison. The burst one ACK raises — the
            // state, the glyph, the copy line — collapses into one correction.
            void OnChanged(object? _, PropertyChangedEventArgs __) => HoldInView(list, message);
            message.PropertyChanged += OnChanged;
            undo.Add(() => message.PropertyChanged -= OnChanged);
        }

        if (undo.Count > 0) RowHooks[list] = undo;
    }

    private static void UnhookRows(ListBox list)
    {
        if (!RowHooks.Remove(list, out var undo)) return;
        foreach (var unsubscribe in undo) unsubscribe();
    }

    /// <summary>
    /// Pins <paramref name="message"/> to the screen position it already
    /// occupies, across the layout its growth is about to cause.
    /// </summary>
    /// <remarks>
    /// A virtualizing panel estimates its total extent from the heights of the
    /// rows it has realized, so one row growing re-estimates every row still
    /// virtualized. Measured on a 500-message list, two chips on one message
    /// moved the extent by nine thousand pixels — enough that the view slid
    /// away from what the reader was looking at, and enough that scrolling to
    /// the end instead landed on the newest message with the reacted one gone
    /// off the top.
    ///
    /// So neither end of the list is the anchor: the message that changed is.
    /// Its offset within the viewport is read now, before the layout, and put
    /// back afterwards — which leaves the reader exactly where they were,
    /// whether they were at the bottom or a hundred messages back.
    /// </remarks>
    private static void HoldInView(ListBox list, ChannelMessage message)
    {
        if (Holding.Contains(list)) return;

        var scroll = ScrollOf(list);
        if (scroll is null) return;

        int index = IndexOf(list, message);
        if (index < 0) return;

        // No container means the message is virtualized away — it isn't on
        // screen, so there is nothing to hold and the reader sees no jump.
        var before = TopOf(list, index, scroll);
        if (before is null) return;

        Holding.Add(list);
        HoldPass(list, scroll, index, before.Value, passes: 3, token: Claim(list));
    }

    private static void HoldPass(ListBox list, ScrollViewer scroll, int index, double top, int passes, int token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!StillOwns(list, token)) { Holding.Remove(list); return; }

            var now = TopOf(list, index, scroll);
            if (now is null)
            {
                // The re-estimate carried the anchored row clean out of the
                // realized range — a chip on the newest message at the bottom
                // does this, sliding the content thousands of pixels. Bring the
                // row back the panel's own way; the next pass measures where it
                // landed and restores its exact prior position.
                try { list.ScrollIntoView(index); } catch { /* torn down */ }
                if (passes <= 1) { Holding.Remove(list); return; }
                HoldPass(list, scroll, index, top, passes - 1, token);
                return;
            }

            double drift = now.Value - top;

            if (Math.Abs(drift) > 0.5) ScrollBy(scroll, drift);

            // Settled, or out of passes. Either way this is the last look, so
            // make the anchored message whole if its new chips hang below the
            // fold — holding its top would otherwise leave the reader pinned to
            // a reaction they can't see.
            if (Math.Abs(drift) <= 0.5 || passes <= 1)
            {
                RevealBottom(list, scroll, index);
                Holding.Remove(list);
                return;
            }

            HoldPass(list, scroll, index, top, passes - 1, token);
        }, DispatcherPriority.Background);
    }

    /// <summary>Scrolls the least amount that brings the anchored message's
    /// lower edge inside the viewport, and nothing if it already is.</summary>
    private static void RevealBottom(ListBox list, ScrollViewer scroll, int index)
    {
        if (list.ContainerFromIndex(index) is not { } container) return;
        if (container.TranslatePoint(default, scroll) is not { } origin) return;

        double overflow = origin.Y + container.Bounds.Height - scroll.Viewport.Height;
        // Never at the cost of the message's own top: a bubble taller than the
        // viewport is read from the top down.
        if (overflow > 0.5 && overflow < origin.Y) ScrollBy(scroll, overflow);
    }

    private static void ScrollBy(ScrollViewer scroll, double delta)
    {
        double limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, limit));
    }

    /// <summary>Where a realized row's top edge sits within the viewport, or
    /// null when the row isn't realized.</summary>
    private static double? TopOf(ListBox list, int index, ScrollViewer scroll) =>
        list.ContainerFromIndex(index)?.TranslatePoint(default, scroll)?.Y;

    private static int IndexOf(ListBox list, ChannelMessage message)
    {
        if (list.ItemsSource is IList<ChannelMessage> typed) return typed.IndexOf(message);
        if (list.ItemsSource is System.Collections.IList untyped) return untyped.IndexOf(message);
        return -1;
    }

    private static ScrollViewer? ScrollOf(ListBox list) =>
        list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>
    /// Scrolls to the newest message across the next few layout passes, so the
    /// view lands at the bottom rather than where the bottom was when the first
    /// pass ran — a new row measures taller or shorter than the panel guessed,
    /// which moves the extent out from under a single scroll.
    /// </summary>
    private static void ScrollToEndSettling(ListBox list, int passes = 3)
    {
        // Takes the list over: any hold in flight is for a message the reader
        // is no longer being shown.
        Holding.Remove(list);
        ScrollToEndPass(list, passes, Claim(list));
    }

    private static void ScrollToEndPass(ListBox list, int remaining, int token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!StillOwns(list, token) || !GetIsEnabled(list)) return;

            ScrollToEnd(list);
            if (remaining <= 1) return;

            ScrollToEndPass(list, remaining - 1, token);
        }, DispatcherPriority.Background);
    }

    private static void Detach(ListBox list)
    {
        UnhookRows(list);
        // Drops the token, which stops any correction still posting passes.
        Holding.Remove(list);
        Sequence.Remove(list);
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
