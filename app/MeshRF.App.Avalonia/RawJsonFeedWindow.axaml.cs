// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Live feed of every decoded packet as JSON. Ported from MeshRF.App's
/// RawJsonFeedWindow, plus an export button that app doesn't have.
/// </summary>
public partial class RawJsonFeedWindow : Window
{
    private RadioViewModel? _viewModel;

    public RawJsonFeedWindow()
    {
        InitializeComponent();
    }

    private async void OnClear(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        int count = _viewModel.DecodedPacketJsonEntries.Count;
        if (count == 0) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Clear feed",
                $"Clear {count} decoded packet{(count == 1 ? "" : "s")} from the feed? This cannot be undone.",
                confirmText: "Clear"))
            return;
        _viewModel.ClearDecodedPacketJsonFeedCommand.Execute(null);
    }

    public static void Show(Window owner, RadioViewModel viewModel)
    {
        var w = new RawJsonFeedWindow { DataContext = viewModel, _viewModel = viewModel };

        // Tail the feed, but only while the toggle is on — scrolling back
        // through history shouldn't be yanked to the bottom by new traffic.
        void OnEntriesChanged(object? _, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            if (w.AutoScrollToggle.IsChecked != true) return;
            // Measured before the new row lays out, so this is where the reader
            // was: parked at the bottom means tail, anywhere else means they're
            // reading an expanded packet and shouldn't be dragged away from it.
            if (w.JsonList.Scroll is { } scroll &&
                scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y > 8)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (w.JsonList.ItemCount > 0) w.JsonList.ScrollIntoView(w.JsonList.ItemCount - 1);
            }, DispatcherPriority.Background);
        }

        viewModel.DecodedPacketJsonEntries.CollectionChanged += OnEntriesChanged;
        w.Closed += (_, _) => viewModel.DecodedPacketJsonEntries.CollectionChanged -= OnEntriesChanged;

        w.Show(owner);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_viewModel.BuildDecodedPacketJsonFeedText());
    }

    /// <summary>Writes the feed to a file. The exported text is the exact JSON,
    /// not the display variant with its wrapping hints, so the result parses.</summary>
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (_viewModel.DecodedPacketJsonEntries.Count == 0)
        {
            _viewModel.StatusText = "Nothing to export — the feed is empty.";
            return;
        }

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export JSON feed",
            SuggestedFileName = $"meshrf-packets-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_viewModel.BuildDecodedPacketJsonFeedText());
            _viewModel.StatusText = string.Format(CultureInfo.CurrentCulture,
                "Exported {0} packets to {1}", _viewModel.DecodedPacketJsonEntries.Count, file.Name);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Export failed: {ex.Message}";
        }
    }
}
