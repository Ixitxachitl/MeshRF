// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MeshRF.App.Views;

public partial class RawJsonFeedWindow : Window
{
    private NotifyCollectionChangedEventHandler? _collectionHandler;

    public RawJsonFeedWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Prime Emoji.Wpf shaping/cache so first row expansion does not pay
        // one-time initialization cost on the interaction path.
        var warmup = new Emoji.Wpf.TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            Text = "warmup 🙂",
            TextWrapping = TextWrapping.Wrap,
        };
        warmup.Measure(new Size(300, double.PositiveInfinity));

        // Subscribe to collection changes for auto-scroll.
        if (JsonList.ItemsSource is INotifyCollectionChanged col)
        {
            _collectionHandler = OnCollectionChanged;
            col.CollectionChanged += _collectionHandler;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (JsonList.ItemsSource is INotifyCollectionChanged col && _collectionHandler is not null)
        {
            col.CollectionChanged -= _collectionHandler;
            _collectionHandler = null;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (AutoScrollToggle.IsChecked != true) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ScrollToBottom);
    }

    private void ScrollToBottom()
    {
        if (JsonList.Items.Count == 0) return;
        JsonList.ScrollIntoView(JsonList.Items[JsonList.Items.Count - 1]);
    }
}

