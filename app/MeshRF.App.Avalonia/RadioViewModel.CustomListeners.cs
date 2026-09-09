// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// One hand-made listener as the Listeners window edits it: a mesh described
/// by its modem settings and a frequency rather than picked off the preset
/// list.
/// </summary>
/// <remarks>
/// Named, and the name is not decoration. A mesh is known by its name
/// everywhere else — it decides which channel list the traffic belongs to,
/// which tab it lands on, and what a node records as where it was heard — so
/// two unnamed hand-made listeners would be one mesh sharing one channel list.
/// Keeping the name stable is also what lets its channels carry over when its
/// settings are edited.
/// </remarks>
public sealed partial class CustomListenerEdit : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private byte _sf = 9;
    [ObservableProperty] private double _bwKhz = 250;
    [ObservableProperty] private byte _cr = 5;
    [ObservableProperty] private double _freqMHz;
    [ObservableProperty] private bool _enabled = true;

    public uint BwHz => (uint)Math.Round(BwKhz * 1000.0);

    /// <summary>What the receiver is asked to listen for.</summary>
    public MonitorPlan.CustomListener ToPlan() =>
        new(Name.Trim(), Sf, BwHz, Cr, FreqMHz, Enabled);

    public CustomListenerSettings ToSettings() => new()
    {
        Name = Name.Trim(), Sf = Sf, BwHz = BwHz, Cr = Cr, FreqMHz = FreqMHz, Enabled = Enabled,
    };

    public static CustomListenerEdit From(CustomListenerSettings s) => new()
    {
        Name = s.Name, Sf = s.Sf, BwKhz = s.BwHz / 1000.0, Cr = s.Cr,
        FreqMHz = s.FreqMHz, Enabled = s.Enabled,
    };

    public CustomListenerEdit Clone() => From(ToSettings());

    /// <summary>Prefilled from a preset, which is where most of these start:
    /// its modem settings and its default slot in the region, ready to be
    /// overridden.</summary>
    public static CustomListenerEdit FromPreset(Region region, LoraPreset preset)
    {
        var p = LoraParamsHelper.FromPreset(preset, ChannelPlan.IsWideLora(region));
        return new CustomListenerEdit
        {
            Name = preset.ToString(),
            Sf = p.Sf,
            BwKhz = p.BwKhz,
            Cr = p.Cr,
            FreqMHz = ChannelPlan.FrequencyMHz(region, preset, ChannelPlan.DefaultSlot(region, preset)),
        };
    }
}

public partial class RadioViewModel
{
    /// <summary>The hand-made listeners, in the order they were added.</summary>
    public ObservableCollection<CustomListenerEdit> CustomListeners { get; } = new();

    /// <summary>What the primary's mesh is called when its settings amount to
    /// no preset. Empty leaves it named after the preset the toolbar is set
    /// to, which is what a station that has never named it has always had.
    /// </summary>
    [ObservableProperty] private string _customPrimaryName = string.Empty;

    /// <summary>Why the name typed for the primary cannot be used, or empty
    /// when it can. A mesh is told from another by its name alone, so one
    /// already spoken for would put two meshes on one channel list.</summary>
    public string PrimaryNameProblem =>
        CustomPrimaryName.Trim().Length == 0 || IsMeshNameFree(CustomPrimaryName)
            ? string.Empty
            : $"\"{CustomPrimaryName.Trim()}\" already names a mesh here.";

    public bool HasPrimaryNameProblem => PrimaryNameProblem.Length > 0;

    partial void OnCustomPrimaryNameChanged(string value)
    {
        if (!_settingsLoaded) return;
        // Naming this mesh renames it, so its channels, its nodes and its
        // history come with it. Distinct from the preset picker below, which
        // moves the station to a different mesh and must leave them behind.
        OnPropertyChanged(nameof(PrimaryNameProblem));
        OnPropertyChanged(nameof(HasPrimaryNameProblem));
        SaveSettings();
        // A name already spoken for is not applied: pointing the primary at
        // another mesh's list would put two meshes on one.
        if (HasPrimaryNameProblem) return;

        var from = _rxHost.PrimaryListName;
        var to = PrimaryMeshTarget();
        if (!_rxHost.RenameMesh(from, to)) _rxHost.SetPrimaryList(to);
        AfterPrimaryListNameChanged();
    }

    /// <summary>What the primary's channel list is called: its name when it
    /// has one, the chosen preset otherwise.</summary>
    private string PrimaryMeshTarget()
    {
        var name = PrimaryMeshName();
        return name.Length > 0 ? name : SelectedPreset.ToString();
    }

    /// <summary>
    /// Points the primary's channel list at whatever its mesh is now.
    /// </summary>
    /// <remarks>
    /// Nothing is moved. This runs when the preset or the modem settings
    /// change, and that is the station going to a different mesh rather than
    /// this mesh being called something else — its channels belong to the mesh
    /// they were made on and stay there.
    /// </remarks>
    public void ApplyPrimaryListName()
    {
        _rxHost.SetPrimaryList(PrimaryMeshTarget());
        AfterPrimaryListNameChanged();
    }

    private void AfterPrimaryListNameChanged()
    {
        RefreshTabGroupOptions();
        OnPropertyChanged(nameof(PrimaryListName));
        RefreshMonitors();
    }

    /// <summary>The hand-made listeners as the plan wants them. Ones with no
    /// name are dropped rather than listened for: a mesh with no name has
    /// nowhere to put its channels.</summary>
    private IReadOnlyList<MonitorPlan.CustomListener> CustomListenerPlan() =>
        CustomListeners.Where(c => c.Name.Trim().Length > 0)
                       .Select(c => c.ToPlan())
                       .ToList();

    /// <summary>True when a name is free to use: no preset, no other
    /// hand-made listener and not the primary already answer to it. Meshes are
    /// told apart by name alone, so two of one name is two meshes sharing a
    /// channel list.</summary>
    public bool IsMeshNameFree(string name, CustomListenerEdit? excluding = null)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        if (string.Equals(name, MeshRF.Mesh.HeardOn.Custom, StringComparison.OrdinalIgnoreCase)) return false;
        // A preset's name belongs to that preset's mesh — except the one the
        // toolbar is set to, which is the primary's own mesh and so is what it
        // is called already.
        if (Enum.GetNames<LoraPreset>().Contains(name, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(name, SelectedPreset.ToString(), StringComparison.OrdinalIgnoreCase))
            return false;
        return !CustomListeners.Any(c => !ReferenceEquals(c, excluding)
                                      && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Files a new listener, or the edits to one already there.</summary>
    public void SaveCustomListener(CustomListenerEdit edited, CustomListenerEdit? replacing)
    {
        if (replacing is not null && CustomListeners.Contains(replacing))
        {
            // Renamed rather than replaced: everything filed under the old name
            // belongs to this mesh and follows it.
            var was = replacing.Name.Trim();
            var now = edited.Name.Trim();
            if (!string.Equals(was, now, StringComparison.Ordinal)) _rxHost.RenameMesh(was, now);
            CustomListeners[CustomListeners.IndexOf(replacing)] = edited;
        }
        else
        {
            CustomListeners.Add(edited);
        }
        PersistCustomListeners();
    }

    [RelayCommand]
    private void RemoveCustomListener(CustomListenerEdit? listener)
    {
        if (listener is null || !CustomListeners.Remove(listener)) return;
        PersistCustomListeners();
    }

    /// <summary>Finds the listener a Monitors row stands for, so the window
    /// can edit or remove it. Rows carry a name rather than the object, since
    /// they are rebuilt from the plan each time it changes.</summary>
    public CustomListenerEdit? CustomListenerNamed(string name) =>
        CustomListeners.FirstOrDefault(c =>
            string.Equals(c.Name.Trim(), name.Trim(), StringComparison.Ordinal));

    private void PersistCustomListeners()
    {
        SaveSettings();
        // A hand-made listener is a mesh the operator has chosen, so its
        // channel list exists from the moment it does — the point of choosing
        // it is to set its channels up.
        RefreshMonitors();
    }
}
