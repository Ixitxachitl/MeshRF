// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace MeshtasticRF.App.Behaviors;

/// <summary>
/// Attached behavior for ListBox: when set to true, scrolls the last item
/// into view whenever the bound collection (an INotifyCollectionChanged)
/// gains a new item. Used for the Log and Messages panels so they tail
/// automatically as new lines arrive.
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

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb) return;
        if ((bool)e.NewValue) lb.Loaded += OnLoaded;
        else lb.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBox lb) return;
        Hook(lb);
        lb.DataContextChanged += (_, _) => Hook(lb);
    }

    // We use a single weak handler per ListBox so swapping the ItemsSource
    // (e.g. when SelectedChannel changes) re-subscribes correctly.
    private static void Hook(ListBox lb)
    {
        if (lb.ItemsSource is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged -= OnChanged;
            ncc.CollectionChanged += OnChanged;
            ScrollToLast(lb);
        }

        void OnChanged(object? s, NotifyCollectionChangedEventArgs ev)
        {
            if (ev.Action == NotifyCollectionChangedAction.Add) ScrollToLast(lb);
        }
    }

    private static void ScrollToLast(ListBox lb)
    {
        if (lb.Items.Count == 0) return;
        var last = lb.Items[lb.Items.Count - 1];
        if (last is null) return;
        lb.Dispatcher.BeginInvoke(new Action(() =>
        {
            lb.ScrollIntoView(last);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}
