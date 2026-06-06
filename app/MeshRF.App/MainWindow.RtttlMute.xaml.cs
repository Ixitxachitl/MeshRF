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
        var selected = SelectedNodes();
        ToggleNodeIgnoredMenuItem.IsEnabled = selected.Length > 0;
        bool allIgnored = selected.Length > 0 && selected.All(n => n.Ignored);
        ToggleNodeIgnoredMenuItem.Header = allIgnored ? "Unignore node" : "Ignore node";
    }

    private void OnToggleNodeIgnored(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var selected = SelectedNodes();
        if (selected.Length == 0) return;

        bool ignore = selected.Any(n => !n.Ignored);
        vm.SetNodesIgnored(selected, ignore);
    }
}