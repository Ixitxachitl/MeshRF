// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshtasticRF.Channels;

namespace MeshtasticRF.App.ViewModels;

/// <summary>
/// One row in the channel TabControl. Holds the persisted <see cref="ChannelConfig"/>
/// plus in-memory <see cref="Messages"/> and <see cref="Log"/> buffers, and
/// exposes editable copies of the channel fields with a Save command, matching
/// the firmware's "Channel Settings" pane.
/// </summary>
public partial class ChannelViewModel : ObservableObject, ITabItem
{
    public ChannelConfig Config { get; }

    /// <summary>Channels are permanent tabs and cannot be closed by the user.</summary>
    public bool CanClose => false;
    private readonly Action<ChannelConfig>? _onSave;

    public ChannelViewModel(ChannelConfig cfg, Action<ChannelConfig>? onSave = null)
    {
        Config = cfg;
        _onSave = onSave;

        _editName = cfg.Name;
        _editRole = cfg.Role;
        _editPsk = (byte[])cfg.Psk.Clone();
        _editUplink = cfg.UplinkEnabled;
        _editDownlink = cfg.DownlinkEnabled;
        _editPositionPrecision = cfg.PositionPrecision;
    }

    /// <summary>System / preamble / packet events for this channel.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Decoded text messages, newest last.</summary>
    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    [ObservableProperty]
    private int _packetCount;

    // -- Editable fields (two-way bound to the settings panel) ---------------

    [ObservableProperty]
    private string _editName;

    [ObservableProperty]
    private ChannelRole _editRole;

    [ObservableProperty]
    private byte[] _editPsk;

    [ObservableProperty]
    private bool _editUplink;

    [ObservableProperty]
    private bool _editDownlink;

    [ObservableProperty]
    private byte _editPositionPrecision;

    public IReadOnlyList<ChannelRole> RoleOptions { get; } = Enum.GetValues<ChannelRole>();

    public string DisplayName =>
        string.IsNullOrEmpty(Config.Name) ? $"Channel {Config.Index}" : Config.Name;

    public string TabHeader =>
        Config.Role == ChannelRole.Primary ? $"{DisplayName} \u2605" : DisplayName;

    public byte Hash => Config.Hash;

    public string PskHex =>
        Config.UsesDefaultKey
            ? "(default key)"
            : Convert.ToHexString(Config.Psk);

    public bool IsPrimary => Config.Role == ChannelRole.Primary;

    [RelayCommand]
    private void Save()
    {
        Config.Name              = (EditName ?? string.Empty).Trim();
        Config.Role              = EditRole;
        Config.Psk               = EditPsk ?? ChannelConfig.DefaultPsk;
        Config.UplinkEnabled     = EditUplink;
        Config.DownlinkEnabled   = EditDownlink;
        Config.PositionPrecision = EditPositionPrecision;
        _onSave?.Invoke(Config);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(PskHex));
        OnPropertyChanged(nameof(IsPrimary));
    }

    [RelayCommand]
    private void Revert()
    {
        EditName              = Config.Name;
        EditRole              = Config.Role;
        EditPsk               = (byte[])Config.Psk.Clone();
        EditUplink            = Config.UplinkEnabled;
        EditDownlink          = Config.DownlinkEnabled;
        EditPositionPrecision = Config.PositionPrecision;
    }

    [RelayCommand]
    private void UseDefaultKey() => EditPsk = new byte[] { 0x01 };

    [RelayCommand]
    private void GenerateRandomKey() => EditPsk = ChannelConfig.NewRandomPsk(32);

    [RelayCommand]
    private void GenerateRandomKey128() => EditPsk = ChannelConfig.NewRandomPsk(16);

    public void AddLog(string line)
    {
        Log.Add(line);
        if (Log.Count > 500) Log.RemoveAt(0);
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (Log.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Log)); }
        catch { /* clipboard contention; ignore */ }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private void CopyMessages()
    {
        if (Messages.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Messages.Select(m => m.Display))); }
        catch { }
    }
}

public sealed class ChannelMessage
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string FromId  { get; init; } = string.Empty;
    public string Text    { get; init; } = string.Empty;
    public float? RssiDbm { get; init; }
    public float? SnrDb   { get; init; }

    public string Display =>
        $"[{Timestamp:HH:mm:ss}] {FromId,-12}  {Text}";
}
