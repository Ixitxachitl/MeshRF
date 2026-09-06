// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The row of mesh tabs above the channel strip. It is built from an
/// ItemsControl and plain bordered panels rather than a tab control, so the
/// only proof it draws a tab per mesh — and marks the one on show — is the
/// visual tree it produces.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class MeshTabRowTests(HeadlessAvalonia avalonia)
{
    private static IEnumerable<Border> MeshTabs(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("meshTab"));

    [Fact]
    public void OneMeshMeansNoRowAtAll() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var vm = (RadioViewModel)window.DataContext!;
        vm.MultiPresetEnabled = false;
        vm.RefreshMonitors();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        Assert.False(vm.HasSeveralMeshes);
        Assert.Empty(MeshTabs(window).Where(b => b.IsEffectivelyVisible));

        window.Close();
    }));

    [Fact]
    public void ATabPerMeshWithTheOneOnShowMarked() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var tabs = MeshTabs(window).ToList();
        Assert.Equal(vm.TabGroupOptions.Count, tabs.Count);
        Assert.True(tabs.Count > 1, "listening on several presets should offer several meshes");

        // The one on show is the one carrying the selected class, which is
        // what paints it as continuous with the channel tabs below.
        var marked = Assert.Single(tabs, b => b.Classes.Contains("selected"));
        Assert.Same(vm.TabGroupOptions.Single(o => o.IsSelected), marked.DataContext);

        // And each tab names its mesh.
        foreach (var option in vm.TabGroupOptions)
            Assert.Contains(tabs.SelectMany(t => t.GetVisualDescendants().OfType<TextBlock>()),
                            t => t.Text == option.Label);

        window.Close();
    }));

    [Fact]
    public void PickingAnotherMeshMovesTheMark() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var other = vm.TabGroupOptions.First(o => o.Group.Length > 0);
        vm.SelectedTabGroupOption = other;
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var marked = Assert.Single(MeshTabs(window), b => b.Classes.Contains("selected"));
        Assert.Same(other, marked.DataContext);

        window.Close();
    }));
}
