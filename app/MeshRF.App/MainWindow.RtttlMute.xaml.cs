// SPDX-License-Identifier: GPL-3.0-or-later
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeshRF.App.ViewModels;

namespace MeshRF.App;

public partial class MainWindow
{
    private MeshRF.Nodes.NodeRecord[] SelectedNodes() =>
        NodesGrid.SelectedItems
            .OfType<MeshRF.Nodes.NodeRecord>()
            .ToArray();

    private void OnNodesContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SetNodeReloadSuspended(true);

        var selected = SelectedNodes();
        CopyNodesMenuItem.IsEnabled = selected.Length > 0;
        ToggleNodeIgnoredMenuItem.IsEnabled = selected.Length > 0;
        ToggleNodeFavoriteMenuItem.IsEnabled = selected.Length > 0;
        bool allIgnored = selected.Length > 0 && selected.All(n => n.Ignored);
        ToggleNodeIgnoredMenuItem.Header = allIgnored ? "Unignore node" : "Ignore node";
        bool allFavorite = selected.Length > 0 && selected.All(n => n.Favorite);
        ToggleNodeFavoriteMenuItem.Header = allFavorite ? "Remove from favorites" : "Add to favorites";

        // Show on map only enabled when a single node with a known location is selected
        var first = selected.FirstOrDefault();
        ShowOnMapMenuItem.IsEnabled = first?.Latitude is not null && first?.Longitude is not null;
    }

    private void OnNodesContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SetNodeReloadSuspended(false);
    }

    private void OnToggleNodeIgnored(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = SelectedNodes();
        if (selected.Length == 0) return;

        bool ignore = selected.Any(n => !n.Ignored);
        vm.SetNodesIgnored(selected, ignore);
    }

    private void OnToggleNodeFavorite(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = SelectedNodes();
        if (selected.Length == 0) return;

        bool favorite = selected.Any(n => !n.Favorite);
        vm.SetNodesFavorite(selected, favorite);
    }
}