// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Popping the six main panels — map, nodes, waypoints, spectrum, chat and
/// log — out into windows of their own, and docking them back.
/// </summary>
/// <remarks>
/// <para>
/// A panel is moved, not rebuilt: the same controls leave the main window's
/// grid and are added to a <see cref="PanelWindow"/>, so selection, scroll
/// position and live data all survive the trip, and nothing has to be kept in
/// step between two copies. Everything the panels bind to hangs off the one
/// view model, which the pop-out window is given as its DataContext.
/// </para>
/// <para>
/// What is left behind then has to be tidied up, which is most of the code
/// here. A grid row emptied of its content still holds its star weight and its
/// MinHeight, so without collapsing it the docked panels would share the space
/// with a gap where the popped-out one used to be. Collapse both sides of a
/// splitter and the pane containing them collapses in turn, so with all six
/// panels out the main window is left as its toolbars and nothing else.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>One panel: the controls it is made of, where they live while
    /// docked, and the window they are in while popped out.</summary>
    private sealed class DockablePanel
    {
        /// <summary>Stable name this panel's state is stored under.</summary>
        public required string Key { get; init; }

        /// <summary>Shown in the pop-out window's title bar.</summary>
        public required string Title { get; init; }

        /// <summary>The grid the parts sit in while docked. Their Grid.Row is
        /// never cleared, so putting them back is just a matter of adding them
        /// to it again.</summary>
        public required Grid Container { get; init; }

        /// <summary>The panel's controls, top to bottom. A panel whose title
        /// row is a separate grid row has two: everything but the last is
        /// docked to the top of the pop-out window, and the last one fills.
        /// </summary>
        public required Control[] Parts { get; init; }

        public PanelWindow? Window { get; set; }

        /// <summary>Where this panel's window was last seen, kept even while
        /// the panel is docked so popping it out again puts the window back
        /// where the user left it.</summary>
        public PanelWindowSettings Bounds { get; set; } = new();

        public bool IsPoppedOut => Window is not null;
    }

    /// <summary>One splitter and the two slots either side of it.</summary>
    private sealed class PaneSplit
    {
        public required DefinitionBase First { get; init; }
        public required DefinitionBase Second { get; init; }

        /// <summary>The splitter's own row or column, zeroed along with it so
        /// a collapsed pane leaves no 5 px seam behind.</summary>
        public required DefinitionBase Divider { get; init; }

        public required Control Splitter { get; init; }

        /// <summary>Whether each side still holds a docked panel. A side that
        /// does not is collapsed to nothing, and the splitter with it.</summary>
        public required Func<bool> FirstFilled { get; init; }
        public required Func<bool> SecondFilled { get; init; }

        /// <summary>The proportion the user last dragged this splitter to, with
        /// both sides docked. Held here rather than read back off the grid,
        /// because a collapsed row measures zero and would otherwise be saved
        /// as the remembered split — the panel would come back with no height.
        /// </summary>
        public double FirstStar { get; set; } = 1;
        public double SecondStar { get; set; } = 1;

        /// <summary>MinWidth/MinHeight as the markup declares them. Dropped to
        /// zero while a slot is collapsed, since a minimum would hold a pane
        /// open that has nothing in it.</summary>
        public double FirstMin { get; init; }
        public double SecondMin { get; init; }

        /// <summary>The pair of settings fields this splitter is stored in.
        /// Carried on the split itself so nothing depends on the order the
        /// splits happen to be listed in.</summary>
        public required Func<AppSettings, (double? First, double? Second)> Load { get; init; }
        public required Action<AppSettings, double, double> Store { get; init; }
    }

    private DockablePanel[] _panels = [];
    private PaneSplit[] _splits = [];

    /// <summary>Set from the moment the panes are resized until the layout pass
    /// that acts on it. In between, what the grids measure is the arrangement
    /// before the change, which must not be mistaken for a split the user
    /// chose — see <see cref="RefreshSplitStars"/>.</summary>
    private bool _splitsStale;

    /// <summary>Panels whose windows are still to be shown. Restoring runs
    /// from the constructor, before the main window itself is up.</summary>
    private readonly List<DockablePanel> _panelsToShow = [];

    private DockablePanel? PanelByKey(string key) =>
        _panels.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    private bool IsDocked(string key) => PanelByKey(key) is { IsPoppedOut: false };

    /// <summary>Builds the panel and splitter model, and puts back whatever was
    /// popped out when the app last closed. Called from ApplyLayout, so the
    /// window comes up already arranged rather than rearranging itself in
    /// front of the user.</summary>
    private void BuildPanels()
    {
        _panels =
        [
            new DockablePanel
            {
                Key = "Map", Title = "Map",
                Container = LeftPaneGrid, Parts = [MapPanelHost],
            },
            new DockablePanel
            {
                Key = "Nodes", Title = "Nodes",
                Container = NodesWaypointsGrid, Parts = [NodesHeaderPanel, NodesGridProxy],
            },
            new DockablePanel
            {
                Key = "Waypoints", Title = "Waypoints",
                Container = NodesWaypointsGrid, Parts = [WaypointsPanelHost],
            },
            new DockablePanel
            {
                Key = "Spectrum", Title = "Spectrum",
                Container = RightPaneGrid, Parts = [SpectrumPanelHost],
            },
            new DockablePanel
            {
                Key = "Chat", Title = "Channels",
                Container = MessagesLayoutGrid, Parts = [ChatHeaderPanel, ChatBodyGrid],
            },
            new DockablePanel
            {
                Key = "Log", Title = "Log",
                Container = MessagesLayoutGrid, Parts = [LogPanelHost],
            },
        ];

        _splits =
        [
            new PaneSplit
            {
                First = MainLayoutGrid.ColumnDefinitions[0],
                Divider = MainLayoutGrid.ColumnDefinitions[1],
                Second = MainLayoutGrid.ColumnDefinitions[2],
                Splitter = MainSplitter,
                FirstMin = MainLayoutGrid.ColumnDefinitions[0].MinWidth,
                SecondMin = MainLayoutGrid.ColumnDefinitions[2].MinWidth,
                FirstFilled = () => IsDocked("Map") || IsDocked("Nodes") || IsDocked("Waypoints"),
                SecondFilled = () => IsDocked("Spectrum") || IsDocked("Chat") || IsDocked("Log"),
                Load = s => (s.MainLeftPaneStar, s.MainRightPaneStar),
                Store = (s, a, b) => { s.MainLeftPaneStar = a; s.MainRightPaneStar = b; },
            },
            new PaneSplit
            {
                First = LeftPaneGrid.RowDefinitions[0],
                Divider = LeftPaneGrid.RowDefinitions[1],
                Second = LeftPaneGrid.RowDefinitions[2],
                Splitter = LeftSplitter,
                FirstMin = LeftPaneGrid.RowDefinitions[0].MinHeight,
                SecondMin = LeftPaneGrid.RowDefinitions[2].MinHeight,
                FirstFilled = () => IsDocked("Map"),
                SecondFilled = () => IsDocked("Nodes") || IsDocked("Waypoints"),
                Load = s => (s.MainLeftTopPaneStar, s.MainLeftBottomPaneStar),
                Store = (s, a, b) => { s.MainLeftTopPaneStar = a; s.MainLeftBottomPaneStar = b; },
            },
            new PaneSplit
            {
                First = RightPaneGrid.RowDefinitions[0],
                Divider = RightPaneGrid.RowDefinitions[1],
                Second = RightPaneGrid.RowDefinitions[2],
                Splitter = RightSplitter,
                FirstMin = RightPaneGrid.RowDefinitions[0].MinHeight,
                SecondMin = RightPaneGrid.RowDefinitions[2].MinHeight,
                FirstFilled = () => IsDocked("Spectrum"),
                SecondFilled = () => IsDocked("Chat") || IsDocked("Log"),
                Load = s => (s.MainRightTopPaneStar, s.MainRightBottomPaneStar),
                Store = (s, a, b) => { s.MainRightTopPaneStar = a; s.MainRightBottomPaneStar = b; },
            },
            // Rows 1 and 3; the nodes title row above them is Auto and empties
            // itself when the panel leaves, so it needs no collapsing.
            new PaneSplit
            {
                First = NodesWaypointsGrid.RowDefinitions[1],
                Divider = NodesWaypointsGrid.RowDefinitions[2],
                Second = NodesWaypointsGrid.RowDefinitions[3],
                Splitter = NodesWaypointsSplitter,
                FirstMin = NodesWaypointsGrid.RowDefinitions[1].MinHeight,
                SecondMin = NodesWaypointsGrid.RowDefinitions[3].MinHeight,
                FirstFilled = () => IsDocked("Nodes"),
                SecondFilled = () => IsDocked("Waypoints"),
                Load = s => (s.NodesPaneStar, s.WaypointsPaneStar),
                Store = (s, a, b) => { s.NodesPaneStar = a; s.WaypointsPaneStar = b; },
            },
            new PaneSplit
            {
                First = MessagesLayoutGrid.RowDefinitions[1],
                Divider = MessagesLayoutGrid.RowDefinitions[2],
                Second = MessagesLayoutGrid.RowDefinitions[3],
                Splitter = MessagesSplitter,
                FirstMin = MessagesLayoutGrid.RowDefinitions[1].MinHeight,
                SecondMin = MessagesLayoutGrid.RowDefinitions[3].MinHeight,
                FirstFilled = () => IsDocked("Chat"),
                SecondFilled = () => IsDocked("Log"),
                Load = s => (s.MessagesTopPaneStar, s.MessagesBottomPaneStar),
                Store = (s, a, b) => { s.MessagesTopPaneStar = a; s.MessagesBottomPaneStar = b; },
            },
        ];

        // The markup's own proportions stand until settings say otherwise.
        foreach (var split in _splits)
        {
            split.FirstStar = GetStar(split.First);
            split.SecondStar = GetStar(split.Second);
        }

        Map.PopOutRequested += (_, _) => TogglePanel("Map");
    }

    /// <summary>Puts the splitters back where they were left, and pops out
    /// again whatever was out when the app last closed.</summary>
    private void RestorePanels(AppSettings settings)
    {
        foreach (var split in _splits)
        {
            var (first, second) = split.Load(settings);
            if (first is > 0 && second is > 0)
            {
                split.FirstStar = first.Value;
                split.SecondStar = second.Value;
            }
        }

        foreach (var panel in _panels)
        {
            if (!settings.PanelWindows.TryGetValue(panel.Key, out var state)) continue;
            panel.Bounds = state;
            if (state.PoppedOut) PopOut(panel);
        }

        ApplySplits();
    }

    /// <summary>Shows the windows for panels restored as popped out, once the
    /// main window itself is up so they come up in front of it.</summary>
    private void ShowRestoredPanelWindows()
    {
        foreach (var panel in _panelsToShow) panel.Window?.Show();
        _panelsToShow.Clear();
    }

    /// <summary>Closes every pop-out window for real, on the way out. Without
    /// this they would each cancel their close and try to dock back into a
    /// window that is being torn down — and, since the app shuts down with its
    /// last window, keep the process alive.</summary>
    private void ClosePanelWindows()
    {
        foreach (var panel in _panels)
        {
            if (panel.Window is not { } window) continue;
            if (_panelsToShow.Contains(panel)) continue;
            window.AllowClose = true;
            window.Close();
        }
    }

    /// <summary>The pop-out button on each panel. Which panel it belongs to
    /// rides on the button's Tag.</summary>
    private void OnPopOutPanel(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string key }) TogglePanel(key);
    }

    private void TogglePanel(string key)
    {
        if (PanelByKey(key) is not { } panel) return;

        // Before the panes move, so a splitter dragged since the last autosave
        // is remembered rather than lost to the rearrangement.
        RefreshSplitStars();

        if (panel.IsPoppedOut) DockBack(panel);
        else PopOut(panel);
        SaveLayout();
    }

    private void PopOut(DockablePanel panel)
    {
        if (panel.IsPoppedOut) return;

        var window = new PanelWindow
        {
            PanelKey = panel.Key,
            Title = "MeshRF — " + panel.Title,
            DataContext = _viewModel,
        };
        window.ApplyBounds(panel.Bounds);
        window.DockBackRequested += (_, _) =>
        {
            RefreshSplitStars();
            DockBack(panel);
            SaveLayout();
        };

        foreach (var part in panel.Parts) panel.Container.Children.Remove(part);
        foreach (var part in panel.Parts)
        {
            // LastChildFill gives the bottom part the rest of the window, so
            // docking every part to the top puts a title row above a table
            // exactly as the grid did.
            DockPanel.SetDock(part, Dock.Top);
            window.PanelHost.Children.Add(part);
        }

        panel.Window = window;
        ApplySplits();

        // Restoring at startup runs before the main window is shown; a pop-out
        // shown then would come up behind it.
        if (IsLoaded) window.Show();
        else _panelsToShow.Add(panel);
    }

    private void DockBack(DockablePanel panel)
    {
        if (panel.Window is not { } window) return;

        window.CaptureBounds(panel.Bounds);

        foreach (var part in panel.Parts) window.PanelHost.Children.Remove(part);
        foreach (var part in panel.Parts) panel.Container.Children.Add(part);

        panel.Window = null;

        // A window restored as popped out and docked again before the main
        // window opened was never shown, and closing one of those throws.
        if (!_panelsToShow.Remove(panel))
        {
            window.AllowClose = true;
            window.Close();
        }

        ApplySplits();
    }

    /// <summary>Sizes every pane against what is still docked in it.</summary>
    private void ApplySplits()
    {
        foreach (var split in _splits) ApplySplit(split);

        // The grids still measure the old arrangement until they are laid out
        // again; Loaded runs after that pass.
        _splitsStale = true;
        Dispatcher.UIThread.Post(() => _splitsStale = false, DispatcherPriority.Loaded);
    }

    /// <summary>Reads each splitter's position back off the grid. Only pairs
    /// with both sides docked have one: a collapsed slot measures zero, which
    /// is not a split anyone chose, and saving it would bring the panel back
    /// with no room.</summary>
    private void RefreshSplitStars()
    {
        foreach (var split in _splits)
        {
            if (!split.FirstFilled() || !split.SecondFilled()) continue;
            split.FirstStar = GetStar(split.First);
            split.SecondStar = GetStar(split.Second);
        }
    }

    private static void ApplySplit(PaneSplit split)
    {
        bool first = split.FirstFilled();
        bool second = split.SecondFilled();
        bool draggable = first && second;

        split.Splitter.IsVisible = draggable;
        SetPixels(split.Divider, draggable ? 5 : 0);

        ApplySlot(split.First, first, split.FirstStar, split.FirstMin, draggable);
        ApplySlot(split.Second, second, split.SecondStar, split.SecondMin, draggable);
    }

    private static void ApplySlot(DefinitionBase definition, bool filled, double star, double min, bool draggable)
    {
        // A collapsed pane has to give up its declared minimum as well, or the
        // minimum alone holds it open.
        SetMinimum(definition, draggable ? min : 0);

        // With the other side collapsed there is no proportion left to honour:
        // a lone star of any size fills the container.
        if (filled) SetStar(definition, draggable ? star : 1);
        else SetPixels(definition, 0);
    }

    /// <summary>Records where each splitter stands, and where each pop-out
    /// window sits, so both survive a restart.</summary>
    private void CapturePanels(AppSettings settings)
    {
        if (!_splitsStale) RefreshSplitStars();
        foreach (var split in _splits) split.Store(settings, split.FirstStar, split.SecondStar);

        foreach (var panel in _panels)
        {
            panel.Window?.CaptureBounds(panel.Bounds);
            panel.Bounds.PoppedOut = panel.IsPoppedOut;
            settings.PanelWindows[panel.Key] = panel.Bounds;
        }
    }
}
