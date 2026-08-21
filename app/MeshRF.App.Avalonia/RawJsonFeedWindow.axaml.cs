// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

        viewModel.DecodedPacketJsonEntries.CollectionChanged += w.OnEntriesChanged;
        w.Closed += (_, _) => viewModel.DecodedPacketJsonEntries.CollectionChanged -= w.OnEntriesChanged;

        // Open on the newest packet. The feed is already populated, so there is
        // no collection change to ride in on, and the rows need a layout pass
        // before their heights are known — hence the bounded retry.
        w.Opened += (_, _) => w.ScheduleInitialScroll(attempts: 4);

        w.Show(owner);
    }

    // ----- Scrolling -----
    //
    // The feed both grows at the bottom and is trimmed at the top, and rows
    // change height when opened, so the view has to be put back deliberately
    // after every change. Two cases, decided before the change lays out:
    // tailing (pinned to the newest packet) or reading, where the entry at the
    // top of the viewport is the anchor and is held exactly where it was.

    private DecodedPacketJsonEntry? _anchor;
    private double _anchorDelta;
    private bool _adjustPending;

    /// <summary>Set once the opening scroll has settled. Expansion state lives
    /// on the entry and survives the window, so the rows for packets that were
    /// left open raise IsCheckedChanged as they are built — before this, those
    /// are the bindings catching up, not a click to follow.</summary>
    private bool _ready;

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A trim arrives as an Add followed by a Remove. Only the first is
        // measured: by the second the state captured here is still the truth,
        // since neither has laid out yet.
        if (_adjustPending) return;
        _adjustPending = true;

        // Read now, before layout: afterwards the extent has already moved and
        // "were they at the bottom?" no longer has an answer.
        bool tail = AutoScrollToggle.IsChecked == true && IsAtBottom();
        if (!tail) CaptureAnchor();

        Dispatcher.UIThread.Post(() =>
        {
            _adjustPending = false;
            if (tail) JsonScroll.ScrollToEnd();
            else RestoreAnchor();
        }, DispatcherPriority.Loaded);
    }

    private void ScheduleInitialScroll(int attempts)
    {
        if (attempts <= 0)
        {
            _ready = true;
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (AutoScrollToggle.IsChecked == true) JsonScroll.ScrollToEnd();
            ScheduleInitialScroll(attempts - 1);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Remembers the entry crossing the top of the viewport, and how
    /// far above that edge it starts.</summary>
    private void CaptureAnchor()
    {
        _anchor = null;
        if (_viewModel is null) return;

        var entries = _viewModel.DecodedPacketJsonEntries;
        double offset = JsonScroll.Offset.Y;
        for (int i = 0; i < entries.Count; i++)
        {
            if (JsonList.ContainerFromIndex(i) is not { } row) continue;
            if (row.Bounds.Bottom <= offset) continue;
            _anchor = entries[i];
            _anchorDelta = row.Bounds.Y - offset;
            return;
        }
    }

    private void RestoreAnchor()
    {
        var anchor = _anchor;
        _anchor = null;
        if (anchor is null || _viewModel is null) return;

        // Gone only if the anchor itself was the entry trimmed off the top, in
        // which case there is nothing to hold still.
        int index = _viewModel.DecodedPacketJsonEntries.IndexOf(anchor);
        if (index < 0 || JsonList.ContainerFromIndex(index) is not { } row) return;
        SetOffset(row.Bounds.Y - _anchorDelta);
    }

    /// <summary>Scrolls an entry that was just opened far enough to show its
    /// body.</summary>
    private void OnEntryExpansionChanged(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not ToggleButton { IsChecked: true, DataContext: DecodedPacketJsonEntry entry }) return;
        if (_viewModel is null) return;

        // Resolved after layout, and by entry rather than by row: the body only
        // has a height once it has been measured, and a packet arriving in the
        // meantime can shift every index.
        Dispatcher.UIThread.Post(() =>
        {
            int index = _viewModel.DecodedPacketJsonEntries.IndexOf(entry);
            if (index >= 0 && JsonList.ContainerFromIndex(index) is { } row) BringRowIntoView(row);
        }, DispatcherPriority.Loaded);
    }

    private void BringRowIntoView(Control row)
    {
        if (row.TranslatePoint(default, JsonList) is not { } origin) return;

        double top = origin.Y;
        double height = row.Bounds.Height;
        double viewport = JsonScroll.Viewport.Height;
        double offset = JsonScroll.Offset.Y;

        // A dump taller than the window is aligned to its top: scrolling to its
        // end instead would push the header that says which packet it is off
        // the screen.
        if (height >= viewport || top < offset) SetOffset(top);
        else if (top + height > offset + viewport) SetOffset(top + height - viewport);
    }

    /// <summary>Within a few pixels of the end, which is what "tailing" means
    /// after a fractional layout.</summary>
    private bool IsAtBottom()
    {
        double hidden = JsonScroll.Extent.Height - JsonScroll.Viewport.Height;
        return hidden <= 0 || JsonScroll.Offset.Y >= hidden - 8;
    }

    private void SetOffset(double y)
    {
        double max = Math.Max(0, JsonScroll.Extent.Height - JsonScroll.Viewport.Height);
        y = Math.Clamp(y, 0, max);
        if (Math.Abs(y - JsonScroll.Offset.Y) > 0.5)
            JsonScroll.Offset = JsonScroll.Offset.WithY(y);
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
