// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// Listeners the operator makes, and the naming that makes each one a mesh.
///
/// The windows are opened for real rather than exercised through the view
/// model alone: a mistyped binding path or an x:DataType that does not match
/// compiles perfectly well and only comes apart when the window is shown.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class CustomListenerUiTests(HeadlessAvalonia avalonia)
{
    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    private static RadioViewModel Station()
    {
        var vm = new RadioViewModel
        {
            SelectedDevice = RadioDeviceKind.HackRf,
            SelectedRegion = Region.US,
            SelectedPreset = LoraPreset.MediumFast,
        };
        vm.SelectedRxSampleRate = vm.SampleRateOptions.Single(o => o.Hz == 10_000_000u);
        vm.MultiPresetEnabled = true;
        vm.RefreshMonitors();
        return vm;
    }

    private static CustomListenerEdit Backyard() => new()
    {
        Name = "Backyard net", Sf = 12, BwKhz = 250, Cr = 6, FreqMHz = 913.875, Enabled = true,
    };

    /// <summary>The editor opens and its bindings resolve. Everything else
    /// here would pass with a window that cannot be shown.</summary>
    [Fact]
    public void TheEditorOpens() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var editor = new CustomListenerEditor
        {
            Draft = Backyard(),
            NameIsFree = n => vm.IsMeshNameFree(n),
        };
        var w = new CustomListenerWindow { DataContext = editor };
        w.Show();
        Settle();

        // The name really reached the box, which is the proof the DataContext
        // and the binding path agree.
        Assert.Contains(w.GetVisualDescendants().OfType<TextBox>(),
                        t => t.Text == "Backyard net");
        Assert.False(editor.HasNameProblem);

        w.Close();
    }));

    /// <summary>A name is what tells one mesh from another, so the editor
    /// refuses the ones that would collide.</summary>
    [Fact]
    public void TheEditorRefusesANameThatIsAlreadyAMesh() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var draft = Backyard();
        var editor = new CustomListenerEditor { Draft = draft, NameIsFree = n => vm.IsMeshNameFree(n) };

        draft.Name = string.Empty;
        editor.RaiseNameChecked();
        Assert.True(editor.HasNameProblem);

        // A preset already answers to this one.
        draft.Name = "LongFast";
        editor.RaiseNameChecked();
        Assert.True(editor.HasNameProblem);

        draft.Name = "Backyard net";
        editor.RaiseNameChecked();
        Assert.False(editor.HasNameProblem);
    }));

    /// <summary>Filling in from a preset takes its settings and its slot, and
    /// leaves them editable, which is the point of starting from one.</summary>
    [Fact]
    public void FillingInFromAPresetTakesItsSettings() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var draft = new CustomListenerEdit { Name = "Club net" };
        var editor = new CustomListenerEditor
        {
            Draft = draft,
            NameIsFree = n => vm.IsMeshNameFree(n),
            SelectedRegion = Region.US,
            SelectedPreset = LoraPreset.LongFast,
        };

        editor.FillFromPreset();

        Assert.Equal(11, draft.Sf);
        Assert.Equal(250, draft.BwKhz, 3);
        Assert.Equal(906.875, draft.FreqMHz, 3);
        // The name the operator typed is theirs and survives the fill.
        Assert.Equal("Club net", draft.Name);

        // And picking a slot writes the frequency it names, on the grid that
        // bandwidth makes.
        editor.BwKhz = 250;
        editor.SelectedSlot = editor.SlotOptions.Single(o => o.Slot == 45);
        Assert.Equal(913.125, draft.FreqMHz, 3);
    }));

    /// <summary>A listener the operator makes is a mesh, so it gets a channel
    /// list of its own: the point of choosing it is to set its channels up.
    /// </summary>
    [Fact]
    public void AddingOneGivesItAChannelListOfItsOwn() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        Assert.DoesNotContain(vm.Tabs.OfType<ChannelTabViewModel>(), t => t.Config.Preset == "Backyard net");

        vm.SaveCustomListener(Backyard(), null);
        Settle();

        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(), t => t.Config.Preset == "Backyard net");
        Assert.Contains(vm.MonitorPresets, r => r.Name == "Backyard net" && r.IsCustomListener);
    }));

    /// <summary>
    /// The band drawn over the waterfall is the listener's own width, and
    /// carries its name.
    /// </summary>
    /// <remarks>
    /// Its width used to be asked of the toolbar, which answers for the
    /// primary however many listeners are up — so a 500 kHz listener beside a
    /// 250 kHz primary was drawn 250 wide, and tested for overlap at 250. The
    /// width now travels on the source, which is the only thing that knows it.
    /// </remarks>
    [Fact]
    public void ItsBandIsItsOwnWidthAndItsOwnName() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var wide = new CustomListenerEdit
        {
            Name = "Wide net", Sf = 11, BwKhz = 500, Cr = 8, FreqMHz = 911.500, Enabled = true,
        };
        vm.SaveCustomListener(wide, null);
        Settle();

        var band = Assert.Single(vm.ChannelBands, b => b.Label == "Wide net");
        Assert.Equal(500_000, band.BandwidthHz, 0);
        // Not the primary's 250, which is what it used to be drawn at.
        Assert.NotEqual(250_000, band.BandwidthHz, 0);
    }));

    /// <summary>
    /// Editing one opens on what was saved, not on a fresh listener. This is
    /// the whole of what Edit is for.
    /// </summary>
    [Fact]
    public void EditingOneOpensOnItsSavedSettings() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var saved = new CustomListenerEdit
        {
            Name = "Club net", Sf = 11, BwKhz = 500, Cr = 8, FreqMHz = 911.500, Enabled = true,
        };
        vm.SaveCustomListener(saved, null);
        Settle();

        // Found the way the window finds it: from the row, by name.
        var row = Assert.Single(vm.MonitorPresets, r => r.Name == "Club net");
        Assert.True(row.IsCustomListener);
        var found = vm.CustomListenerNamed(row.Name);
        Assert.NotNull(found);

        var editor = CustomListenerWindow.CreateEditor(vm, found);

        Assert.Equal("Club net", editor.Draft.Name);
        Assert.Equal(11, editor.Sf);
        Assert.Equal(500, editor.BwKhz);
        Assert.Equal(8, editor.Cr);
        Assert.Equal(911.500m, editor.FreqMHz);
        // Its own name is not a collision with itself.
        Assert.False(editor.HasNameProblem);
    }));

    /// <summary>
    /// And the window opened on it really shows them. The editor holding the
    /// right values proves nothing about what reaches the controls.
    /// </summary>
    [Fact]
    public void TheEditorWindowShowsTheSavedSettings() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var saved = new CustomListenerEdit
        {
            Name = "Club net", Sf = 11, BwKhz = 500, Cr = 8, FreqMHz = 911.500, Enabled = true,
        };
        vm.SaveCustomListener(saved, null);
        Settle();

        var editor = CustomListenerWindow.CreateEditor(vm, vm.CustomListenerNamed("Club net"));
        var w = new CustomListenerWindow { DataContext = editor };
        w.Show();
        Settle();

        Assert.Contains(w.GetVisualDescendants().OfType<TextBox>(), t => t.Text == "Club net");

        var values = w.GetVisualDescendants().OfType<NumericUpDown>()
                      .Select(n => n.Value)
                      .ToList();
        Assert.Contains(11m, values);
        Assert.Contains(500m, values);
        Assert.Contains(8m, values);
        Assert.Contains(911.5m, values);

        w.Close();
    }));

    /// <summary>
    /// The slot picker says where the frequency is, rather than claiming Auto
    /// over a frequency that is not the auto slot's.
    /// </summary>
    [Fact]
    public void TheSlotPickerFollowsTheFrequency() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var editor = CustomListenerWindow.CreateEditor(vm, null);
        editor.Draft.Name = "Club net";
        editor.RaiseNameChecked();

        // Filling in from a preset puts the frequency on that preset's default
        // slot, which is not the slot this listener's name hashes to.
        editor.SelectedPreset = LoraPreset.LongFast;
        editor.FillFromPreset();

        Assert.Equal(906.875m, editor.FreqMHz);

        // Auto is the first entry and carries no slot number of its own.
        Assert.Null(editor.SlotOptions[0].Slot);
        Assert.StartsWith("Auto", editor.SlotOptions[0].Label);

        // Whatever the picker settled on, it names the frequency that is set —
        // it reports where the listener is rather than deciding it.
        var picked = editor.SelectedSlot;
        Assert.NotNull(picked);
        int slot = picked!.Slot ?? ChannelPlan.DefaultSlot(
            editor.SelectedRegion, editor.Draft.BwKhz / 1000.0, "Club net", "Club net");
        Assert.Equal(906.875,
                     ChannelPlan.FrequencyMHz(editor.SelectedRegion, editor.Draft.BwKhz / 1000.0, slot), 3);
    }));

    /// <summary>And a frequency that matches nothing on the grid leaves it
    /// blank rather than claiming a slot it is not on.</summary>
    [Fact]
    public void AHandTypedFrequencyClaimsNoSlot() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var editor = CustomListenerWindow.CreateEditor(vm, null);
        editor.BwKhz = 250;
        editor.FreqMHz = 913.0777m;

        Assert.Null(editor.SelectedSlot);
    }));

    /// <summary>And taking it away stops it being listened for.</summary>
    [Fact]
    public void RemovingOneStopsItBeingListenedFor() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var mine = Backyard();
        vm.SaveCustomListener(mine, null);
        Settle();
        Assert.Contains(vm.MonitorPresets, r => r.Name == "Backyard net");

        vm.RemoveCustomListenerCommand.Execute(mine);
        Settle();

        Assert.DoesNotContain(vm.MonitorPresets, r => r.Name == "Backyard net");
        Assert.Empty(vm.CustomListeners);
    }));

    /// <summary>The Listeners window shows it, and offers the two things only
    /// a hand-made listener has: an edit and a way to take it away.</summary>
    [Fact]
    public void TheListenersWindowOffersEditAndRemoveOnItAlone() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.SaveCustomListener(Backyard(), null);

        var w = new MonitorsWindow { DataContext = vm };
        w.Show();
        Settle();

        // Asked of the buttons rather than of the rows: a row's data context
        // reaches every control drawn inside it, nested grids included, so
        // "the grids standing for a row" is several grids per row.
        // Effectively visible, not merely present: a control hidden by
        // IsVisible stays in the visual tree, so an unfiltered search finds
        // the buttons on every row whether or not they are offered.
        var offered = w.GetVisualDescendants().OfType<Button>()
                       .Where(b => b.IsEffectivelyVisible && (b.Content as string) == "Edit")
                       .Select(b => b.DataContext)
                       .OfType<MonitorPresetRow>()
                       .ToList();

        Assert.Contains(offered, r => r.Name == "Backyard net");
        // A preset's row is the region's, not the operator's, so it offers none.
        Assert.All(offered, r => Assert.True(r.IsCustomListener));
        // And the presets are on show beside it, so "only custom rows offer
        // Edit" is a statement about a window with both kinds in it.
        Assert.Contains(vm.MonitorPresets, r => !r.IsCustomListener);

        w.Close();
    }));

    /// <summary>
    /// Naming this station's own mesh is what lets its channels carry over:
    /// they belong to the name. Available whatever the settings amount to — a
    /// mesh is a place, and what the operator calls their own place is theirs
    /// to say.
    /// </summary>
    [Fact]
    public void NamingThePrimaryMovesItsChannelsOntoTheName() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();

        vm.CustomPrimaryName = "Home net";
        Settle();

        Assert.Equal("Home net", vm.PrimaryListName);
        Assert.Contains(vm.Tabs.OfType<ChannelTabViewModel>(), t => t.Config.Preset == "Home net");

        // Cleared, it goes back to being named after the preset the toolbar is
        // set to, and the channels set up under the name stay for its return.
        vm.CustomPrimaryName = string.Empty;
        Settle();
        Assert.Equal(nameof(LoraPreset.MediumFast), vm.PrimaryListName);
    }));

    /// <summary>The Listeners window offers the name, whatever preset the
    /// station is on. It is the only place to change it, so it has to be
    /// there to be found.</summary>
    [Fact]
    public void TheListenersWindowOffersTheMeshName() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        vm.CustomPrimaryName = "Home net";
        Settle();

        var w = new MonitorsWindow { DataContext = vm };
        w.Show();
        Settle();

        // Visible, on a station whose settings amount to a preset — which is
        // every ordinary station, and where this used to be hidden.
        Assert.Contains(w.GetVisualDescendants().OfType<TextBox>(),
                        t => t.IsEffectivelyVisible && t.Text == "Home net");
        Assert.Contains(w.GetVisualDescendants().OfType<TextBlock>(),
                        t => t.IsEffectivelyVisible && t.Text == "Mesh name");

        w.Close();
    }));

    /// <summary>A name another mesh already answers to is refused rather than
    /// applied: pointing the primary at another mesh's list would put two
    /// meshes on one.</summary>
    [Fact]
    public void ANameAnotherMeshAnswersToIsRefused() =>
        avalonia.Run(() => TempDataDirectory.With(() =>
    {
        using var vm = Station();
        var was = vm.PrimaryListName;

        // LongFast is a listener in its own right here.
        vm.CustomPrimaryName = nameof(LoraPreset.LongFast);
        Settle();

        Assert.True(vm.HasPrimaryNameProblem);
        Assert.Equal(was, vm.PrimaryListName);
    }));
}
