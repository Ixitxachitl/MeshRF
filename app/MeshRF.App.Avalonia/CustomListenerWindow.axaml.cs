// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The editing state of one hand-made listener: the listener itself, plus the
/// two conveniences for filling it in — a preset to start from, and a region
/// and slot to work a frequency out of.
/// </summary>
/// <remarks>
/// The preset and the slot are ways of arriving at a value, not values in
/// their own right. Only the frequency and the modem settings are kept, so
/// there is never a stored slot that disagrees with a stored frequency about
/// where the listener is.
/// </remarks>
public sealed partial class CustomListenerEditor : ObservableObject
{
    public required CustomListenerEdit Draft { get; init; }

    /// <summary>Names already taken. A mesh is told from another by its name
    /// alone, so a repeat would be two meshes sharing one channel list.</summary>
    public required Func<string, bool> NameIsFree { get; init; }

    public IReadOnlyList<LoraPreset> Presets { get; } = Enum.GetValues<LoraPreset>();
    public IReadOnlyList<Region> Regions { get; } = Enum.GetValues<Region>();

    [ObservableProperty] private LoraPreset _selectedPreset = LoraPreset.LongFast;
    [ObservableProperty] private Region _selectedRegion = Region.US;

    /// <summary>One entry in the slot picker. A null <see cref="Slot"/> is
    /// Auto: the slot firmware would land on for a channel of this name at
    /// this bandwidth, which is where an unconfigured node on this mesh
    /// would be.</summary>
    public sealed record SlotChoice(int? Slot, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<SlotChoice> SlotOptions => _slotOptions ??= BuildSlotOptions();
    private IReadOnlyList<SlotChoice>? _slotOptions;

    [ObservableProperty] private SlotChoice? _selectedSlot;

    partial void OnSelectedSlotChanged(SlotChoice? value)
    {
        if (value is null || _fillingSlots) return;
        FreqMHz = (decimal)ChannelPlan.FrequencyMHz(
            SelectedRegion, Draft.BwKhz / 1000.0, value.Slot ?? AutoSlot());
    }

    partial void OnSelectedRegionChanged(Region value) => RebuildSlotOptions();

    /// <summary>True while the picker is being refilled, so replacing its
    /// items does not read as the operator choosing one.</summary>
    private bool _fillingSlots;

    /// <summary>What firmware would pick for a channel of this name: the
    /// hashed default slot on this region's grid at this bandwidth.</summary>
    private int AutoSlot() =>
        ChannelPlan.DefaultSlot(SelectedRegion, Draft.BwKhz / 1000.0,
                                Draft.Name.Trim(), Draft.Name.Trim());

    private IReadOnlyList<SlotChoice> BuildSlotOptions()
    {
        double bwMHz = Draft.BwKhz / 1000.0;
        var list = new List<SlotChoice> { new(null, AutoLabel()) };
        int count = ChannelPlan.SlotCount(SelectedRegion, bwMHz);
        for (int slot = 1; slot <= count; slot++)
            list.Add(new SlotChoice(slot,
                $"{slot} — {ChannelPlan.FrequencyMHz(SelectedRegion, bwMHz, slot):0.000} MHz"));
        return list;
    }

    /// <summary>The grid depends on the region and the bandwidth, so the list
    /// is rebuilt whenever either moves.</summary>
    public void RebuildSlotOptions()
    {
        _slotOptions = BuildSlotOptions();
        OnPropertyChanged(nameof(SlotOptions));
        SyncSelectedSlot();
    }

    /// <summary>
    /// Points the picker at whichever entry names the frequency as it stands.
    /// </summary>
    /// <remarks>
    /// The picker follows the frequency rather than setting it. Forcing it to
    /// Auto after every change said "Auto" over a frequency that was not the
    /// auto slot's — filling in from a preset puts the frequency on that
    /// preset's default slot, which is a different slot from the one this
    /// listener's name hashes to. Nothing matching leaves it blank, which is
    /// the honest answer for a frequency typed by hand.
    /// </remarks>
    private void SyncSelectedSlot()
    {
        _fillingSlots = true;
        try { SelectedSlot = MatchingSlot(); }
        finally { _fillingSlots = false; }
    }

    private SlotChoice? MatchingSlot()
    {
        if (_slotOptions is null) return null;
        double bwMHz = Draft.BwKhz / 1000.0;
        if (bwMHz <= 0) return null;
        int auto = AutoSlot();
        // Auto is first, so a frequency that is both the auto slot's and an
        // explicit slot's reads as Auto — which is what it is.
        foreach (var option in _slotOptions)
        {
            var slot = option.Slot ?? auto;
            if (Math.Abs(ChannelPlan.FrequencyMHz(SelectedRegion, bwMHz, slot) - Draft.FreqMHz) < 1e-6)
                return option;
        }
        return null;
    }

    // Surfaced separately so the numeric boxes can bind to decimal? without
    // the draft having to carry the widget's type.
    public decimal Sf
    {
        get => Draft.Sf;
        set { Draft.Sf = (byte)value; OnPropertyChanged(); }
    }

    public decimal BwKhz
    {
        get => (decimal)Draft.BwKhz;
        set
        {
            Draft.BwKhz = (double)value;
            OnPropertyChanged();
            // The slot grid is made of bandwidths, so a different one is a
            // different grid and the slots on offer are different slots.
            RebuildSlotOptions();
        }
    }

    public decimal Cr
    {
        get => Draft.Cr;
        set { Draft.Cr = (byte)value; OnPropertyChanged(); }
    }

    public decimal FreqMHz
    {
        get => (decimal)Draft.FreqMHz;
        set
        {
            Draft.FreqMHz = (double)value;
            OnPropertyChanged();
            SyncSelectedSlot();
        }
    }

    /// <summary>Why this name cannot be used, or empty when it can.</summary>
    public string NameProblem
    {
        get
        {
            var name = Draft.Name.Trim();
            if (name.Length == 0) return "A listener needs a name: it is what its channels and its nodes belong to.";
            return NameIsFree(name)
                ? string.Empty
                : $"\"{name}\" already names a mesh here. Two meshes of one name would share one channel list.";
        }
    }

    public bool HasNameProblem => NameProblem.Length > 0;

    /// <summary>Takes everything from a preset, ready to be overridden.</summary>
    public void FillFromPreset()
    {
        var filled = CustomListenerEdit.FromPreset(SelectedRegion, SelectedPreset);
        // The name is the operator's, not the preset's, once they have typed
        // one: a listener called after a preset is exactly the collision the
        // name check refuses.
        if (Draft.Name.Trim().Length == 0) Draft.Name = filled.Name;
        Sf = filled.Sf;
        BwKhz = (decimal)filled.BwKhz;
        Cr = filled.Cr;
        FreqMHz = (decimal)filled.FreqMHz;
        RaiseNameChecked();
        // Its own default slot is where the fill just put it, so the picker
        // should say so rather than still showing the last grid.
        RebuildSlotOptions();
    }

    public void RaiseNameChecked()
    {
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        // Auto is the slot firmware would pick for a channel of this name, so
        // it moves as the name is typed — but only the one entry changes, and
        // the rest of the grid is unchanged. Rebuilt only when it has actually
        // moved, since the list runs to hundreds of slots at a narrow
        // bandwidth and this runs on every keystroke.
        if (_slotOptions is { Count: > 0 } && _slotOptions[0].Label != AutoLabel())
            RebuildSlotOptions();
    }

    private string AutoLabel() => $"Auto (slot {AutoSlot()})";
}

/// <summary>Creates a hand-made listener, or edits one already there.</summary>
public partial class CustomListenerWindow : Window
{
    private CustomListenerEditor? _editor;
    private bool _saved;

    public CustomListenerWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The editor for a listener, or for a new one when there is none to edit.
    /// </summary>
    /// <remarks>
    /// Separate from showing the window: everything that decides what the
    /// operator sees happens here, and a dialog is not something a test can
    /// wait on. Edits a copy, so cancelling leaves the original untouched.
    /// </remarks>
    public static CustomListenerEditor CreateEditor(RadioViewModel vm, CustomListenerEdit? editing)
    {
        var draft = editing?.Clone() ?? new CustomListenerEdit
        {
            FreqMHz = ChannelPlan.FrequencyMHz(vm.SelectedRegion, vm.SelectedPreset,
                                               ChannelPlan.DefaultSlot(vm.SelectedRegion, vm.SelectedPreset)),
        };
        var editor = new CustomListenerEditor
        {
            Draft = draft,
            NameIsFree = name => vm.IsMeshNameFree(name, editing),
            SelectedRegion = vm.SelectedRegion,
            SelectedPreset = vm.SelectedPreset,
        };
        draft.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CustomListenerEdit.Name)) editor.RaiseNameChecked();
        };
        editor.RebuildSlotOptions();
        return editor;
    }

    /// <summary>Shows the editor and returns the listener to file, or null if
    /// it was cancelled.</summary>
    public static async Task<CustomListenerEdit?> ShowAsync(
        Window owner, RadioViewModel vm, CustomListenerEdit? editing)
    {
        var editor = CreateEditor(vm, editing);
        var w = new CustomListenerWindow { DataContext = editor, _editor = editor };
        await w.ShowDialog(owner);
        return w._saved ? editor.Draft : null;
    }

    private void OnUsePreset(object? sender, RoutedEventArgs e) => _editor?.FillFromPreset();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_editor is null || _editor.HasNameProblem) return;
        _saved = true;
        Close();
    }
}
