// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeshRF.App.Behaviors;

/// <summary>
/// Attached behavior for ListBox: keeps the list tailed to the bottom. It
/// scrolls to the end whenever the bound collection changes (new item, reset,
/// or replace) and whenever the ItemsSource is swapped — e.g. switching channel
/// tabs or reloading chat history. Used for the Log and Messages panels.
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AutoScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    // The collection we are currently subscribed to, and the handler used, so
    // we can detach cleanly when the ItemsSource is replaced.
    private static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(INotifyCollectionChanged), typeof(AutoScroll));

    private static readonly DependencyProperty HandlerProperty =
        DependencyProperty.RegisterAttached(
            "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(AutoScroll));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;
        if ((bool)e.NewValue)
        {
            lb.Loaded += OnLoaded;
            lb.DataContextChanged += OnDataContextChanged;
        }
        else
        {
            lb.Loaded -= OnLoaded;
            lb.DataContextChanged -= OnDataContextChanged;
        }
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox lb) Hook(lb);
    }

    private static void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ListBox lb) Hook(lb);
    }

    private static void Hook(ListBox lb)
    {
        var current = lb.ItemsSource as INotifyCollectionChanged;
        var tracked = (INotifyCollectionChanged?)lb.GetValue(SourceProperty);

        if (!ReferenceEquals(current, tracked))
        {
            // Detach the handler from the previous collection.
            if (tracked is not null &&
                lb.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler oldHandler)
            {
                tracked.CollectionChanged -= oldHandler;
            }

            if (current is not null)
            {
                NotifyCollectionChangedEventHandler handler = (_, ev) =>
                {
                    if (ev.Action is NotifyCollectionChangedAction.Add
                                  or NotifyCollectionChangedAction.Reset
                                  or NotifyCollectionChangedAction.Replace)
                    {
                        ScrollToBottom(lb);
                    }
                };
                current.CollectionChanged += handler;
                lb.SetValue(HandlerProperty, handler);
            }
            else
            {
                lb.ClearValue(HandlerProperty);
            }

            lb.SetValue(SourceProperty, current);
        }

        // Always tail when (re)hooking, e.g. after a tab switch repopulates the
        // same list or selects a list that was filled while off-screen.
        ScrollToBottom(lb);
    }

    private static void ScrollToBottom(ListBox lb)
    {
        // Defer until after layout so the ScrollViewer's extent reflects any
        // items that were just added; otherwise the scroll lands short.
        lb.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindScrollViewer(lb) is { } sv)
            {
                sv.ScrollToBottom();
            }
            else if (lb.Items.Count > 0)
            {
                lb.ScrollIntoView(lb.Items[lb.Items.Count - 1]);
            }
        }), DispatcherPriority.ContextIdle);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }
}
