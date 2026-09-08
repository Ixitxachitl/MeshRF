// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Unseen activity is said the same way at both levels of the tab strip.
///
/// A mesh used to grow a red dot instead of pulsing, and the dot went out the
/// moment that mesh was brought on show — which is not the same as reading
/// what arrived on it. The tab holding the activity was still sitting there
/// unopened, and the only thing pointing at it had stopped pointing.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class MeshTabAttentionTests(HeadlessAvalonia avalonia)
{
    /// <summary>The mesh row's own boxes, told from the channel tabs below by
    /// what each one stands for.</summary>
    private static IEnumerable<Border> MeshTabs(Window window) =>
        window.GetVisualDescendants().OfType<Border>()
              .Where(b => b.Classes.Contains("meshTab") && b.DataContext is TabGroupOption);

    private static Border MeshTabFor(Window window, TabGroupOption option) =>
        MeshTabs(window).Single(b => ReferenceEquals(b.DataContext, option));

    private static bool IsPulsing(Border meshTab) =>
        meshTab.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("attention"));

    /// <summary>A station listening on several presets, so the mesh row is
    /// there to be looked at.</summary>
    private static (MainWindow Window, RadioViewModel Vm) Station()
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        Settle();

        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        vm.SelectedRegion = Region.US;
        vm.SelectedPreset = LoraPreset.MediumFast;
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        Settle();
        return (window, vm);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// A mesh with something unread pulses its own name, the way the channel
    /// and conversation tabs below it do — and carries no dot, which was the
    /// thing that made it read as a different kind of mark.
    /// </summary>
    [Fact]
    public void AMeshWithSomethingUnreadPulsesInsteadOfShowingADot() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (window, vm) = Station();

        var shown = vm.TabGroupOptions.Single(o => o.IsSelected);
        var hidden = vm.TabGroupOptions.First(o => !o.IsSelected);
        var hiddenTab = vm.Tabs.First(t => t.TabGroup == hidden.Group);

        Assert.False(IsPulsing(MeshTabFor(window, hidden)));

        hiddenTab.TabNeedsAttention = true;
        vm.RefreshTabGroupOptions();
        Settle();

        Assert.True(hidden.NeedsAttention);
        Assert.True(IsPulsing(MeshTabFor(window, hidden)));
        Assert.False(IsPulsing(MeshTabFor(window, shown)));

        // The class is only half the claim — the style behind it has to be
        // reaching this element. Its weight is the part that does not move,
        // so it is the part that can be asserted; the opacity is mid-animation
        // at any instant a test could look.
        var label = MeshTabFor(window, hidden).GetVisualDescendants().OfType<TextBlock>()
                                              .Single(t => t.Classes.Contains("attention"));
        Assert.Equal(FontWeight.SemiBold, label.FontWeight);

        // The dot is gone for good: a mesh says this the one way now.
        Assert.Empty(MeshTabs(window).SelectMany(b => b.GetVisualDescendants().OfType<Ellipse>()));

        window.Close();
    }));

    /// <summary>
    /// The crux: a mesh keeps pulsing while a tab on it is still unread, even
    /// though that mesh is the one on show. Showing a mesh is not reading it.
    /// </summary>
    [Fact]
    public void TheMeshOnShowKeepsPulsingUntilTheTabItselfIsOpened() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (window, vm) = Station();

        // A second channel on the mesh being shown, so there is a tab on it
        // that is not the one selected. Adding one selects it, which is what
        // leaves the original free to go unread.
        var shown = vm.TabGroupOptions.Single(o => o.IsSelected);
        var firstTab = vm.Tabs.First(t => t.TabGroup == shown.Group);
        vm.AddChannelCommand.Execute(null);
        Settle();
        Assert.NotSame(firstTab, vm.SelectedTab);

        firstTab.TabNeedsAttention = true;
        vm.RefreshTabGroupOptions();
        Settle();

        // On show, and still pulsing: the tab that has the activity has not
        // been opened.
        Assert.True(shown.IsSelected);
        Assert.True(shown.NeedsAttention);
        Assert.True(IsPulsing(MeshTabFor(window, shown)));

        // Opening it is what puts both marks out.
        vm.SelectedTab = firstTab;
        Settle();

        Assert.False(firstTab.TabNeedsAttention);
        Assert.False(shown.NeedsAttention);
        Assert.False(IsPulsing(MeshTabFor(window, shown)));

        window.Close();
    }));

    /// <summary>And a mesh with two unread tabs stops only once both have been
    /// looked at — the mark stands for whatever is still unread under it.
    /// </summary>
    [Fact]
    public void AMeshStopsOnlyWhenNothingUnderItIsUnread() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (window, vm) = Station();

        var shown = vm.TabGroupOptions.Single(o => o.IsSelected);
        var first = vm.Tabs.First(t => t.TabGroup == shown.Group);
        vm.AddChannelCommand.Execute(null);
        Settle();
        var second = vm.SelectedTab!;
        vm.AddChannelCommand.Execute(null);
        Settle();

        first.TabNeedsAttention = true;
        second.TabNeedsAttention = true;
        vm.RefreshTabGroupOptions();
        Settle();
        Assert.True(shown.NeedsAttention);

        vm.SelectedTab = first;
        Settle();
        Assert.True(shown.NeedsAttention);

        vm.SelectedTab = second;
        Settle();
        Assert.False(shown.NeedsAttention);

        window.Close();
    }));
}
