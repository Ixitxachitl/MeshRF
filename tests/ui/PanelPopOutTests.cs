// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Popping a panel out moves its controls into another window rather than
/// building a second copy of them. Two things break quietly when that is done
/// wrong, and neither throws: the pane the panel left keeps its share of the
/// space, leaving a gap; and bindings that reached the view model by the main
/// window's name find nothing in the new window, so menus open with no command
/// behind them and toggles stop tracking.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class PanelPopOutTests
{
    private readonly HeadlessAvalonia _avalonia;

    public PanelPopOutTests(HeadlessAvalonia avalonia) => _avalonia = avalonia;

    /// <summary>Each panel: the grid it lives in, the row that has to collapse
    /// behind it, and the splitter that has to go with it.</summary>
    public static TheoryData<string, string, int, string> Panels => new()
    {
        { "Map", "LeftPaneGrid", 0, "LeftSplitter" },
        { "Nodes", "NodesWaypointsGrid", 1, "NodesWaypointsSplitter" },
        { "Waypoints", "NodesWaypointsGrid", 3, "NodesWaypointsSplitter" },
        { "Spectrum", "RightPaneGrid", 0, "RightSplitter" },
        { "Chat", "MessagesLayoutGrid", 1, "MessagesSplitter" },
        { "Log", "MessagesLayoutGrid", 3, "MessagesSplitter" },
    };

    [Theory]
    [MemberData(nameof(Panels))]
    public void PoppingAPanelOutCollapsesThePaneItLeaves(
        string key, string gridName, int index, string splitterName)
    {
        WithTempSettings(() => _avalonia.Run(() =>
        {
            using var app = new MainWindowUnderTest();

            var grid = app.Find<Grid>(gridName);
            var splitter = app.Find<GridSplitter>(splitterName);
            var definition = grid.RowDefinitions[index];
            var parts = PartsOf(app.Window, key);

            Assert.All(parts, part => Assert.Contains(part, grid.Children));
            Assert.True(splitter.IsVisible);

            Click(PopOutButton(app.Window, key));
            app.Pump();

            var panelWindow = Assert.IsType<PanelWindow>(TopLevel.GetTopLevel(parts[0]));
            Assert.Equal(key, panelWindow.PanelKey);
            Assert.All(parts, part => Assert.DoesNotContain(part, grid.Children));

            // Collapsed to nothing, minimum included: a MinHeight left in place
            // would hold the empty row open on its own.
            Assert.True(definition.Height.IsAbsolute, $"{key} left a {definition.Height} row behind");
            Assert.Equal(0, definition.Height.Value);
            Assert.Equal(0, definition.MinHeight);
            Assert.False(splitter.IsVisible, $"{splitterName} still divides a pane with one side gone");

            // Closing the pop-out window is the dock-back gesture.
            panelWindow.Close();
            app.Pump();

            Assert.All(parts, part => Assert.Contains(part, grid.Children));
            Assert.True(definition.Height.IsStar, $"{key} came back as {definition.Height}");
            Assert.True(definition.Height.Value > 0);
            Assert.True(splitter.IsVisible);
        }));
    }

    /// <summary>The chat's message list binds its auto-scroll through the main
    /// window's name. Realized inside a pop-out window it has to find the same
    /// view model there, or the list silently stops following new traffic.
    /// </summary>
    [Fact]
    public void ChatInItsOwnWindowStillTracksTheViewModel()
    {
        WithTempSettings(() => _avalonia.Run(() =>
        {
            using var app = new MainWindowUnderTest();

            Click(PopOutButton(app.Window, "Chat"));
            app.Pump();

            var tabs = app.Find<TabControl>("MainTabs");
            Assert.IsType<PanelWindow>(TopLevel.GetTopLevel(tabs));
            Assert.NotEmpty(app.ViewModel.Tabs);

            tabs.SelectedIndex = 0;
            app.Pump();

            var list = tabs.GetVisualDescendants().OfType<ListBox>()
                           .FirstOrDefault(l => l.Classes.Contains("chat"));
            Assert.NotNull(list);

            // The default is on, so only the change proves the binding is live
            // rather than merely left at its default.
            app.ViewModel.AutoScroll = false;
            app.Pump();
            Assert.False(AutoScrollBehavior.GetIsEnabled(list!));

            app.ViewModel.AutoScroll = true;
            app.Pump();
            Assert.True(AutoScrollBehavior.GetIsEnabled(list!));
        }));
    }

    /// <summary>Half the node menu runs commands off the view model, reached
    /// the same way. A menu that opens with every entry dead is the failure
    /// this guards against.</summary>
    [Theory]
    [InlineData("Message")]
    [InlineData("Request node info")]
    [InlineData("Traceroute")]
    [InlineData("Toggle favorite")]
    public void NodeMenuInItsOwnWindowStillHasItsCommands(string header)
    {
        WithTempSettings(() => _avalonia.Run(() =>
        {
            using var app = new MainWindowUnderTest();

            Click(PopOutButton(app.Window, "Nodes"));
            app.Pump();

            var nodes = app.Find<DataGrid>("NodesGridProxy");
            Assert.IsType<PanelWindow>(TopLevel.GetTopLevel(nodes));

            var menu = Assert.IsType<ContextMenu>(nodes.ContextMenu);
            var item = menu.Items.OfType<MenuItem>()
                           .FirstOrDefault(m => Equals(m.Header, header));
            Assert.NotNull(item);
            Assert.NotNull(item!.Command);
        }));
    }

    /// <summary>With every panel out there is nothing left between the
    /// toolbars and the status bar, so the collapse has to carry all the way
    /// up: an empty pane must not leave its column standing.</summary>
    [Fact]
    public void WithEveryPanelOutTheMainWindowIsJustItsToolbars()
    {
        WithTempSettings(() => _avalonia.Run(() =>
        {
            using var app = new MainWindowUnderTest();

            foreach (var key in new[] { "Map", "Nodes", "Waypoints", "Spectrum", "Chat", "Log" })
            {
                Click(PopOutButton(app.Window, key));
                app.Pump();
            }

            var columns = app.Find<Grid>("MainLayoutGrid").ColumnDefinitions;
            // A definition collapsed to zero still measures one device pixel,
            // which is what a Grid reports for a column it gave nothing to.
            Assert.True(columns[0].ActualWidth <= 1, $"left column is {columns[0].ActualWidth} px wide");
            Assert.True(columns[2].ActualWidth <= 1, $"right column is {columns[2].ActualWidth} px wide");

            foreach (var name in new[] { "MainSplitter", "LeftSplitter", "RightSplitter",
                                         "NodesWaypointsSplitter", "MessagesSplitter" })
                Assert.False(app.Find<GridSplitter>(name).IsVisible, $"{name} still shows with nothing to divide");
        }));
    }

    /// <summary>A panel that goes out and comes back leaves the layout exactly
    /// as it found it, on screen and in the settings file. Collapsing the pane
    /// behind a departing panel rewrites the very row sizes a splitter position
    /// is read back from, so a save landing in that moment records the
    /// collapsed arrangement as the split the user chose — and the panel comes
    /// back, now or next launch, to a pane squeezed flat.</summary>
    [Fact]
    public void PoppingOutAndBackLeavesTheSplittersWhereTheyWere()
    {
        WithTempSettings(() => _avalonia.Run(() =>
        {
            double mapShare, listsShare;

            using (var app = new MainWindowUnderTest())
            {
                var left = app.Find<Grid>("LeftPaneGrid");
                mapShare = left.RowDefinitions[0].ActualHeight;
                listsShare = left.RowDefinitions[2].ActualHeight;
                Assert.True(mapShare > 0 && listsShare > 0);

                foreach (var key in new[] { "Map", "Nodes", "Waypoints" })
                {
                    Click(PopOutButton(app.Window, key));
                    app.Pump();
                }

                // Map first, while the pane below it still holds nothing but
                // the bars: that is the order that leaves the left column
                // rearranged twice, and it was what exposed this.
                foreach (var key in new[] { "Map", "Nodes", "Waypoints" })
                {
                    ((Window)TopLevel.GetTopLevel(PartsOf(app.Window, key)[0])!).Close();
                    app.Pump();
                }

                AssertPaneHeight(left.RowDefinitions[0], mapShare, "map");
                AssertPaneHeight(left.RowDefinitions[2], listsShare, "node and waypoint lists");
            }

            // And what was written down while all that was going on has to
            // describe the same layout, not one of the arrangements passed
            // through on the way.
            using var reopened = new MainWindowUnderTest();
            var restored = reopened.Find<Grid>("LeftPaneGrid");
            AssertPaneHeight(restored.RowDefinitions[0], mapShare, "reopened map");
            AssertPaneHeight(restored.RowDefinitions[2], listsShare, "reopened node and waypoint lists");
        }));
    }

    /// <summary>What was popped out when the app closed is popped out again
    /// next time, in the window it was left in.</summary>
    [Fact]
    public void APoppedOutPanelComesBackOutOnTheNextRun()
    {
        WithTempSettings(() => _avalonia.Run(() =>
        {
            using (var first = new MainWindowUnderTest())
            {
                Click(PopOutButton(first.Window, "Log"));
                first.Pump();

                var panelWindow = Assert.IsType<PanelWindow>(
                    TopLevel.GetTopLevel(PartsOf(first.Window, "Log")[0]));
                panelWindow.Width = 640;
                panelWindow.Height = 400;
                first.Pump();
            }

            using var second = new MainWindowUnderTest();
            var log = PartsOf(second.Window, "Log")[0];
            var restored = Assert.IsType<PanelWindow>(TopLevel.GetTopLevel(log));
            Assert.Equal("Log", restored.PanelKey);
            Assert.Equal(640, restored.Width);
            Assert.Equal(400, restored.Height);
        }));
    }

    // ----- helpers ---------------------------------------------------------

    private static void AssertPaneHeight(RowDefinition row, double expected, string what) =>
        Assert.True(Math.Abs(row.ActualHeight - expected) <= 1,
                    $"the {what} pane came back {row.ActualHeight:0.#} px tall, was {expected:0.#}");


    /// <summary>A main window shown on the headless surface, closed and drained
    /// on the way out so the next test starts from a written settings file
    /// rather than a queued one.</summary>
    private sealed class MainWindowUnderTest : IDisposable
    {
        public MainWindowUnderTest()
        {
            Window = new MainWindow { Width = 1280, Height = 900 };
            Window.Show();
            Pump();
        }

        public MainWindow Window { get; }

        public RadioViewModel ViewModel => (RadioViewModel)Window.DataContext!;

        public T Find<T>(string name) where T : Control
        {
            var found = Window.FindControl<T>(name);
            Assert.True(found is not null, $"no {typeof(T).Name} named {name}");
            return found!;
        }

        public void Pump()
        {
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            Pump();
        }
    }

    /// <summary>The controls a panel is made of, named as the markup names
    /// them. Held by reference so they can be followed across the move.
    /// </summary>
    private static Control[] PartsOf(MainWindow window, string key)
    {
        string[] names = key switch
        {
            "Map" => ["MapPanelHost"],
            "Nodes" => ["NodesHeaderPanel", "NodesGridProxy"],
            "Waypoints" => ["WaypointsPanelHost"],
            "Spectrum" => ["SpectrumPanelHost"],
            "Chat" => ["ChatHeaderPanel", "ChatBodyGrid"],
            "Log" => ["LogPanelHost"],
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "unknown panel"),
        };

        return [.. names.Select(name =>
        {
            var found = window.FindControl<Control>(name);
            Assert.True(found is not null, $"no control named {name}");
            return found!;
        })];
    }

    /// <summary>The panel's own pop-out button, wherever the panel currently
    /// is. The map's is part of the map overlay and carries no tag; the rest
    /// name their panel in theirs.</summary>
    private static Button PopOutButton(MainWindow window, string key)
    {
        var part = PartsOf(window, key)[0];
        Visual root = TopLevel.GetTopLevel(part) ?? (Visual)part;

        var button = root.GetVisualDescendants().OfType<Button>()
                         .FirstOrDefault(b => b.Classes.Contains("popout")
                                              && (Equals(b.Tag, key) || (key == "Map" && b.Name == "MapPopOutButton")));
        Assert.True(button is not null, $"no pop-out button for {key}");
        return button!;
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Runs the body against a settings file of its own. The window
    /// writes its layout on close and on a timer, and these tests would
    /// otherwise rewrite the layout of the app the developer is running.
    /// </summary>
    private static void WithTempSettings(Action body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MeshRF-panel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        string? previous = AppSettings.PathOverride;
        AppSettings.PathOverride = Path.Combine(dir, "settings.json");
        try
        {
            body();
        }
        finally
        {
            AppSettings.PathOverride = previous;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
