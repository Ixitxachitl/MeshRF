// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeshRF.AvaloniaApp;
using MeshRF.Nodes;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The heard-on filter offers the meshes the node list actually holds. It
/// used to offer every preset the enum knows — most of them meshes nothing
/// here had ever been heard on — and none of the other names a mesh goes by,
/// so a node heard through a listener you had named could not be filtered to.
/// </summary>
public class NodeHeardOnFilterOptionsTests(HeadlessAvalonia ui) : RenderTest(ui)
{
    private static NodeRecord Heard(uint nodeNum, string mesh) => new() { NodeNum = nodeNum, HeardOnPreset = mesh };

    [Fact]
    public void OnlyTheMeshesANodeWasHeardOnAreOffered() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = new RadioViewModel();

        vm.Nodes.Add(Heard(1, nameof(LoraPreset.MediumFast)));
        vm.Nodes.Add(Heard(2, nameof(LoraPreset.LongFast)));
        vm.Nodes.Add(Heard(3, nameof(LoraPreset.MediumFast)));
        // Not heard on anything yet: nothing to offer for it.
        vm.Nodes.Add(Heard(4, string.Empty));
        // A listener somebody made and named, which the preset list never held.
        vm.Nodes.Add(Heard(5, "Shed"));

        Assert.Equal(["Any", nameof(LoraPreset.LongFast), nameof(LoraPreset.MediumFast), "Shed"],
                     vm.NodeHeardOnFilterOptions);
    }));

    /// <summary>A node's record is replaced when it is heard somewhere new, and
    /// the options follow.</summary>
    [Fact]
    public void ANodeHeardSomewhereNewMovesTheOptions() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = new RadioViewModel();
        vm.Nodes.Add(Heard(1, nameof(LoraPreset.LongFast)));
        Assert.Contains(nameof(LoraPreset.LongFast), vm.NodeHeardOnFilterOptions);

        vm.Nodes[vm.Nodes.Count - 1] = Heard(1, nameof(LoraPreset.ShortFast));

        Assert.DoesNotContain(nameof(LoraPreset.LongFast), vm.NodeHeardOnFilterOptions);
        Assert.Contains(nameof(LoraPreset.ShortFast), vm.NodeHeardOnFilterOptions);
    }));

    /// <summary>
    /// The mesh the filter is set to stays offered after its last node goes.
    /// Dropping it would leave the combo blank over a filter still hiding every
    /// row, with nothing on screen to say why.
    /// </summary>
    [Fact]
    public void TheChosenMeshStaysOfferedWithNoNodeLeftOnIt() => Ui(() => TempDataDirectory.With(() =>
    {
        using var vm = new RadioViewModel();
        var node = Heard(1, nameof(LoraPreset.LongFast));
        vm.Nodes.Add(node);
        vm.NodeHeardOnFilter = nameof(LoraPreset.LongFast);

        vm.Nodes.Remove(node);

        Assert.Equal(nameof(LoraPreset.LongFast), vm.NodeHeardOnFilter);
        Assert.Contains(nameof(LoraPreset.LongFast), vm.NodeHeardOnFilterOptions);
        Assert.Empty(vm.FilteredNodes);
    }));

    /// <summary>A saved filter is restored before any node has been listed, so
    /// it cannot be checked against the nodes — and must not be thrown away for
    /// failing that check.</summary>
    [Fact]
    public void ASavedFilterIsRestoredBeforeTheNodesAre() => Ui(() => TempDataDirectory.With(() =>
    {
        using (var vm = new RadioViewModel())
        {
            vm.Nodes.Add(Heard(1, "Shed"));
            vm.NodeHeardOnFilter = "Shed";
        }
        AppSettings.FlushPendingWrites(TimeSpan.FromSeconds(5));

        using var reopened = new RadioViewModel();

        Assert.Equal("Shed", reopened.NodeHeardOnFilter);
        Assert.Contains("Shed", reopened.NodeHeardOnFilterOptions);
    }));

    /// <summary>
    /// The options change under a combo that is showing one of them. Cleared
    /// and refilled, the list would take the selection with it and the two-way
    /// binding would write that back as no filter; edited in place, the combo
    /// stays on what was chosen.
    /// </summary>
    [Fact]
    public void TheComboKeepsItsSelectionWhileTheOptionsChange() => Ui(() => TempDataDirectory.With(() =>
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
        var vm = (RadioViewModel)window.DataContext!;

        var combo = window.GetLogicalDescendants().OfType<ComboBox>()
                          .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, vm.NodeHeardOnFilterOptions));
        Assert.True(combo is not null, "no combo is bound to the heard-on options");

        vm.Nodes.Add(Heard(1, nameof(LoraPreset.MediumFast)));
        vm.NodeHeardOnFilter = nameof(LoraPreset.MediumFast);
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        // Sorts in ahead of the selection, and another mesh drops out behind it.
        var leaving = Heard(2, "Shed");
        vm.Nodes.Add(leaving);
        vm.Nodes.Add(Heard(3, nameof(LoraPreset.LongFast)));
        vm.Nodes.Remove(leaving);
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        var selected = combo!.SelectedItem;
        var filter = vm.NodeHeardOnFilter;
        window.Close();
        for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

        Assert.Equal(nameof(LoraPreset.MediumFast), selected);
        Assert.Equal(nameof(LoraPreset.MediumFast), filter);
    }));
}
