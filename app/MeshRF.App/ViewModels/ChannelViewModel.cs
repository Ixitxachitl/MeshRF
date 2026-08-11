// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;

namespace MeshRF.App.ViewModels;

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
    private readonly Action<ChannelViewModel, bool>? _onMuteRtttlChanged;

    public ChannelViewModel(ChannelConfig cfg, Action<ChannelConfig>? onSave = null,
                            bool muteRtttl = false,
                            Action<ChannelViewModel, bool>? onMuteRtttlChanged = null,
                            UnitSystem unitSystem = UnitSystem.Metric)
    {
        Config = cfg;
        _onSave = onSave;
        _onMuteRtttlChanged = onMuteRtttlChanged;

        _editName = cfg.Name;
        _editRole = cfg.Role;
        _editPsk = (byte[])cfg.Psk.Clone();
        _editPositionPrecision = cfg.PositionPrecision;
        _editUplinkEnabled = cfg.UplinkEnabled;
        _editDownlinkEnabled = cfg.DownlinkEnabled;
        _muteRtttl = muteRtttl;
        UpdatePositionPrecisionOptions(unitSystem);
        SnakeScores.CollectionChanged += (_, _) => HasSnakeScores = SnakeScores.Count > 0;
        TetrisScores.CollectionChanged += (_, _) => HasTetrisScores = TetrisScores.Count > 0;
        BreakoutScores.CollectionChanged += (_, _) => HasBreakoutScores = BreakoutScores.Count > 0;
        ChirpyRunnerScores.CollectionChanged += (_, _) => HasChirpyRunnerScores = ChirpyRunnerScores.Count > 0;
    }

    /// <summary>Decoded text messages, newest last.</summary>
    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    /// <summary>Snake high-score table entries as received from the mesh.</summary>
    public ObservableCollection<SnakeHighScoreEntry> SnakeScores { get; } = new();

    private bool _hasSnakeScores;
    /// <summary>True when at least one snake high score has been received.</summary>
    public bool HasSnakeScores
    {
        get => _hasSnakeScores;
        private set { if (_hasSnakeScores != value) { _hasSnakeScores = value; OnPropertyChanged(); } }
    }

    /// <summary>Tetris high-score table entries as received from the mesh.</summary>
    public ObservableCollection<TetrisHighScoreEntry> TetrisScores { get; } = new();

    private bool _hasTetrisScores;
    /// <summary>True when at least one Tetris high score has been received.</summary>
    public bool HasTetrisScores
    {
        get => _hasTetrisScores;
        private set { if (_hasTetrisScores != value) { _hasTetrisScores = value; OnPropertyChanged(); } }
    }

    /// <summary>Breakout high-score table entries as received from the mesh.</summary>
    public ObservableCollection<BreakoutHighScoreEntry> BreakoutScores { get; } = new();

    private bool _hasBreakoutScores;
    /// <summary>True when at least one Breakout high score has been received.</summary>
    public bool HasBreakoutScores
    {
        get => _hasBreakoutScores;
        private set { if (_hasBreakoutScores != value) { _hasBreakoutScores = value; OnPropertyChanged(); } }
    }

    /// <summary>Chirpy Runner high-score table entries as received from the mesh.</summary>
    public ObservableCollection<ChirpyRunnerHighScoreEntry> ChirpyRunnerScores { get; } = new();

    private bool _hasChirpyRunnerScores;
    /// <summary>True when at least one Chirpy Runner high score has been received.</summary>
    public bool HasChirpyRunnerScores
    {
        get => _hasChirpyRunnerScores;
        private set { if (_hasChirpyRunnerScores != value) { _hasChirpyRunnerScores = value; OnPropertyChanged(); } }
    }

    [ObservableProperty]
    private int _packetCount;

    /// <summary>Suppress the incoming-text RTTTL ringtone for this channel.</summary>
    [ObservableProperty]
    private bool _muteRtttl;

    /// <summary>When true, keep this chat tailed to the newest message.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>True when this tab has unseen incoming activity.</summary>
    [ObservableProperty]
    private bool _tabNeedsAttention;

    partial void OnMuteRtttlChanged(bool value) => _onMuteRtttlChanged?.Invoke(this, value);

    // -- Editable fields (two-way bound to the settings panel) ---------------

    [ObservableProperty]
    private string _editName;

    [ObservableProperty]
    private ChannelRole _editRole;

    [ObservableProperty]
    private byte[] _editPsk;

    [ObservableProperty]
    private byte _editPositionPrecision;

    /// <summary>Uplink this channel's traffic to the MQTT bridge, if enabled
    /// and connected (firmware <c>Channel.settings.uplink_enabled</c>).</summary>
    [ObservableProperty]
    private bool _editUplinkEnabled;

    /// <summary>Accept downlinked traffic for this channel from the MQTT
    /// bridge and inject it into the local mesh (firmware
    /// <c>Channel.settings.downlink_enabled</c>).</summary>
    [ObservableProperty]
    private bool _editDownlinkEnabled;

    /// <summary>
    /// Discrete location-sharing precisions offered per channel, matching the
    /// official Meshtastic clients: 0 disables sharing, 32 sends the exact
    /// location, and 10–19 fuzz it to the listed radius (each step roughly
    /// halves the uncertainty). Only these <c>position_precision</c> values are
    /// considered valid on the mesh.
    /// </summary>
    private IReadOnlyList<PositionPrecisionOption> _positionPrecisionOptions = Array.Empty<PositionPrecisionOption>();
    public IReadOnlyList<PositionPrecisionOption> PositionPrecisionOptions => _positionPrecisionOptions;

    public IReadOnlyList<ChannelRole> RoleOptions { get; } = new[]
    {
        ChannelRole.Primary,
        ChannelRole.Secondary,
    };

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

    private void Save()
    {
        Config.Name              = (EditName ?? string.Empty).Trim();
        Config.Role              = EditRole;
        Config.Psk               = EditPsk ?? ChannelConfig.DefaultPsk;
        Config.PositionPrecision = EditPositionPrecision;
        Config.UplinkEnabled     = EditUplinkEnabled;
        Config.DownlinkEnabled   = EditDownlinkEnabled;
        _onSave?.Invoke(Config);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(PskHex));
        OnPropertyChanged(nameof(IsPrimary));
    }

    // Guards against RenameTo's programmatic EditName update re-entering
    // Save() -> _onSave -> ReloadChannels -> SyncPrimaryChannelName ->
    // RenameTo, which is exactly how RenameTo itself gets called.
    private bool _suppressAutoSave;

    partial void OnEditNameChanged(string value) { if (!_suppressAutoSave) Save(); }
    partial void OnEditRoleChanged(ChannelRole value) => Save();
    partial void OnEditPskChanged(byte[] value) => Save();
    partial void OnEditPositionPrecisionChanged(byte value) => Save();
    partial void OnEditUplinkEnabledChanged(bool value) => Save();
    partial void OnEditDownlinkEnabledChanged(bool value) => Save();

    /// <summary>
    /// Rename the channel in place (used to keep the default Primary channel's
    /// name in sync with the active modem preset) and refresh the tab header.
    /// </summary>
    public void RenameTo(string name)
    {
        Config.Name = name ?? string.Empty;
        _suppressAutoSave = true;
        EditName = Config.Name;
        _suppressAutoSave = false;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(PskHex));
    }

    public void UpdatePositionPrecisionOptions(UnitSystem unitSystem)
    {
        _positionPrecisionOptions = DisplayUnits.BuildPositionPrecisionOptions(unitSystem);
        OnPropertyChanged(nameof(PositionPrecisionOptions));
    }

    [RelayCommand]
    private void UseDefaultKey() => EditPsk = new byte[] { 0x01 };

    [RelayCommand]
    private void GenerateRandomKey() => EditPsk = ChannelConfig.NewRandomPsk(32);

    [RelayCommand]
    private void GenerateRandomKey128() => EditPsk = ChannelConfig.NewRandomPsk(16);

    [RelayCommand]
    private void CopyMessages()
    {
        if (Messages.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Messages.Select(m => m.Display))); }
        catch { }
    }

    [RelayCommand]
    private void ClearSnakeScores() => SnakeScores.Clear();

    [RelayCommand]
    private void ClearTetrisScores() => TetrisScores.Clear();

    [RelayCommand]
    private void ClearBreakoutScores() => BreakoutScores.Clear();

    [RelayCommand]
    private void ClearChirpyRunnerScores() => ChirpyRunnerScores.Clear();
}
