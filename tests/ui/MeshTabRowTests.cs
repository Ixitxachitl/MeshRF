// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    /// <summary>Every tab box on screen. The class is shared with the
     /// channel strip below, which is the point of it.</summary>
    private static IEnumerable<Border> TabBoxes(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("meshTab"));

    /// <summary>Just the mesh row, told apart by what each tab stands for.</summary>
    private static IEnumerable<Border> MeshTabs(Window window) =>
        TabBoxes(window).Where(b => b.DataContext is TabGroupOption);

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

    /// <summary>
    /// The two rows behave as one strip as well as looking like one: they
    /// hover the same, sit flush the same, and neither explains itself with a
    /// tip the other does not have.
    /// </summary>
    /// <remarks>
    /// Hover is forced rather than pointed at. A headless pointer cannot be
    /// moved onto a control, and the fill a hovered tab takes is a style on
    /// the pseudo-class, so setting it is the only way to see what the style
    /// would do.
    /// </remarks>
    [Fact]
    public void TheTwoRowsHoverAndSitTheSame() => avalonia.Run(() => TempDataDirectory.With(() =>
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
        vm.AddChannelCommand.Execute(null);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var shown = TabBoxes(window).Where(b => b.IsEffectivelyVisible).ToList();
        var meshes = shown.Where(b => b.DataContext is TabGroupOption).ToList();
        var channels = shown.Except(meshes).ToList();

        static bool IsCurrent(Border b) =>
            b.Classes.Contains("selected")
            || b.GetVisualAncestors().OfType<TabItem>().FirstOrDefault()?.IsSelected == true;

        var mesh = meshes.First(b => !IsCurrent(b));
        var channel = channels.First(b => !IsCurrent(b));

        // Neither explains itself where the other does not.
        Assert.All(meshes, b => Assert.Null(ToolTip.GetTip(b)));
        Assert.All(meshes, b => Assert.Equal(channel.Cursor, b.Cursor));

        ((IPseudoClasses)mesh.Classes).Set(":pointerover", true);
        ((IPseudoClasses)channel.Classes).Set(":pointerover", true);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        Assert.Equal(channel.Background, mesh.Background);

        ((IPseudoClasses)mesh.Classes).Set(":pointerover", false);
        ((IPseudoClasses)channel.Classes).Set(":pointerover", false);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        // At rest they match too — which is why the line above proves nothing
        // on its own. What makes it mean something is that hover moves them,
        // and moves them to the same place.
        Assert.Equal(channel.Background, mesh.Background);
        var resting = mesh.Background;
        ((IPseudoClasses)mesh.Classes).Set(":pointerover", true);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
        Assert.NotEqual(resting, mesh.Background);
        ((IPseudoClasses)mesh.Classes).Set(":pointerover", false);

        // Flush in both rows: the boxes meet at their borders. Measured in the
        // window's own space — a Border's Bounds are its parent's coordinates,
        // and each of these is the root of its own item container, so every
        // one of them reports an X of zero.
        static IReadOnlyList<(double Left, double Right)> SpansIn(Window w, IEnumerable<Border> boxes) =>
            boxes.Select(b => (((Visual)b).TranslatePoint(default, w)!.Value.X, b.Bounds.Width))
                 .Select(p => (p.X, p.X + p.Width))
                 .OrderBy(p => p.X)
                 .ToList();

        var meshRow = SpansIn(window, meshes);
        Assert.True(meshRow.Count > 1, "the mesh row should offer a choice");
        for (int i = 1; i < meshRow.Count; i++)
            Assert.Equal(meshRow[i - 1].Right, meshRow[i].Left, 1);

        // The same as the strip below, which is what "consistent" means here.
        var channelRow = SpansIn(window, channels);
        for (int i = 1; i < channelRow.Count; i++)
            Assert.Equal(channelRow[i - 1].Right, channelRow[i].Left, 1);

        window.Close();
    }));

    /// <summary>
    /// The meshes and the channels below them are one tab strip at two
    /// levels, so they are drawn as the same box. They arrive at it by
    /// different routes — the mesh row from a bound class, a channel tab from
    /// the tab strip's own selection — and only the rendered border says the
    /// two routes agree.
    /// </summary>
    [Fact]
    public void ChannelTabsAreDrawnAsTheMeshTabsAre() => avalonia.Run(() => TempDataDirectory.With(() =>
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
        // A second channel on the shown mesh, so there is an unselected
        // channel tab to compare as well as the current one.
        vm.AddChannelCommand.Execute(null);
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

        var shown = TabBoxes(window).Where(b => b.IsEffectivelyVisible).ToList();
        var meshes = shown.Where(b => b.DataContext is TabGroupOption).ToList();
        var channels = shown.Except(meshes).ToList();
        Assert.True(meshes.Count > 1, "the mesh row should offer a choice");
        Assert.True(channels.Count > 1, "the strip should hold more than the current channel");

        static bool IsCurrent(Border b) =>
            b.Classes.Contains("selected")
            || b.GetVisualAncestors().OfType<TabItem>().FirstOrDefault()?.IsSelected == true;

        var meshNow = Assert.Single(meshes, IsCurrent);
        var channelNow = Assert.Single(channels, IsCurrent);
        var meshOther = meshes.First(b => !IsCurrent(b));
        var channelOther = channels.First(b => !IsCurrent(b));

        // The fill is what says which one is current, and it has to say it
        // the same way in both rows.
        Assert.Equal(meshNow.Background, channelNow.Background);
        Assert.Equal(meshOther.Background, channelOther.Background);
        Assert.NotEqual(meshNow.Background, meshOther.Background);

        // And the box itself is the same box, not merely the same colour.
        foreach (var b in shown)
        {
            Assert.Equal(meshNow.BorderBrush, b.BorderBrush);
            Assert.Equal(meshNow.BorderThickness, b.BorderThickness);
            Assert.Equal(meshNow.CornerRadius, b.CornerRadius);
            Assert.Equal(meshNow.Padding, b.Padding);
        }

        window.Close();
    }));
}
