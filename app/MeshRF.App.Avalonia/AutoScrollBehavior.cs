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
    /// Keeps <paramref name="message"/> at the screen position it occupied
    /// before the layout its growth is about to cause.
    /// </summary>
    /// <remarks>
    /// A virtualizing panel estimates its total extent from the heights of the
    /// rows it has realized, so one row growing re-estimates every row still
    /// virtualized and can slide the viewport thousands of pixels off the
    /// content it was showing — traced live, one chip on a 423-message list
    /// moved the anchored row by 4315 pixels.
    ///
    /// Restoring the position is done in two stages because the two failure
    /// modes need opposite medicine. While the row is off screen, its measured
    /// drift lives in estimate-space — a revision that wrote that number into
    /// the offset oscillated +4315, −1076, +3652 against the re-estimates and
    /// ended clamped at the bottom — so the only move made is ScrollIntoView,
    /// the panel's own convergent routine. Once the row overlaps the viewport
    /// the remaining error is bounded by one screen, small enough that an
    /// offset write only realizes neighbours; it is applied in passes that
    /// must strictly shrink the error, and the first pass that doesn't stops
    /// the hold — visibly close and stationary beats fighting the panel.
    /// </remarks>
    private static void HoldInView(ListBox list, ChannelMessage message)
    {
        if (Holding.Contains(list)) return;

        int index = IndexOf(list, message);
        if (index < 0) return;

        // Only a message the reader could see is worth following: growth of an
        // off-screen row moves nothing they are looking at, and an ACK for
        // something two hundred rows up must not haul the view to it.
        var top = TopOf(list, index);
        if (top is null || !IsOnScreen(list, index)) return;

        Holding.Add(list);
        HoldPass(list, index, wantTop: top.Value, prevError: double.PositiveInfinity, passes: 5, token: Claim(list));
    }

    private static void HoldPass(ListBox list, int index, double wantTop, double prevError, int passes, int token)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!StillOwns(list, token) || passes <= 0) { Holding.Remove(list); return; }

            if (!IsOnScreen(list, index))
            {
                // Estimate-space: the only safe move is the panel's own. It
                // lands the row at a viewport edge; the next pass walks it
                // back to where it was.
                try { list.ScrollIntoView(index); } catch { /* torn down mid-post */ }
                HoldPass(list, index, wantTop, double.PositiveInfinity, passes - 1, token);
                return;
            }

            var now = TopOf(list, index);
            var scroll = ScrollOf(list);
            if (now is null || scroll is null) { Holding.Remove(list); return; }

            double delta = now.Value - wantTop;

            // In place (within a pixel), or a pass that failed to shrink the
            // error — the latter is the panel answering back, and pressing on
            // is what once oscillated to the bottom. The row is visible and
            // near its old position either way.
            if (Math.Abs(delta) <= 1 || Math.Abs(delta) >= prevError) { Holding.Remove(list); return; }

            double limit = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, limit));
            HoldPass(list, index, wantTop, Math.Abs(delta), passes - 1, token);
        }, DispatcherPriority.Background);
    }

    /// <summary>Whether a row's container is realized and overlaps the
    /// viewport at all. Containers are realized well past the viewport, so
    /// realized alone does not mean the reader can see it — or that its
    /// measured position is trustworthy.</summary>
    private static bool IsOnScreen(ListBox list, int index)
    {
        if (list.ContainerFromIndex(index) is not { } container) return false;
        if (ScrollOf(list) is not { } scroll) return false;
        if (container.TranslatePoint(default, scroll) is not { } origin) return false;

        return origin.Y + container.Bounds.Height > 0 && origin.Y < scroll.Viewport.Height;
    }

    /// <summary>Where a row's top edge sits relative to the viewport, or null
    /// when the row isn't realized.</summary>
    private static double? TopOf(ListBox list, int index) =>
        ScrollOf(list) is { } scroll
            ? list.ContainerFromIndex(index)?.TranslatePoint(default, scroll)?.Y
            : null;

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
