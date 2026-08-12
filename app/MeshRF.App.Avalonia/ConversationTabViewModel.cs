// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// One direct-message conversation tab, keyed by peer node number, plus the
/// peer's recorded location and telemetry history that the history windows
/// render. Ported from MeshRF.App's ConversationViewModel.
/// </summary>
public partial class ConversationTabViewModel : ObservableObject, ITabItem
{
    private readonly NodeStore? _nodeStore;
    // Resolved at call time rather than captured: tabs restored during host
    // construction are built before the view model has wired its formatters up,
    // and history loads lazily, by which point they exist.
    private readonly Func<Func<float, string>?>? _temperatureFormatter;
    private readonly Func<Func<float, string>?>? _pressureFormatter;
    private bool _historyLoaded;

    public uint NodeNum { get; }

    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    [ObservableProperty]
    private string _peerName;

    public string TabHeader => PeerName;

    public string PeerId => $"!{NodeNum:x8}";

    public bool CanClose => true;

    [ObservableProperty]
    private bool _tabNeedsAttention;

    public ConversationTabViewModel(uint nodeNum, string peerName,
                                    NodeStore? nodeStore = null,
                                    Func<Func<float, string>?>? temperatureFormatter = null,
                                    Func<Func<float, string>?>? pressureFormatter = null)
    {
        NodeNum = nodeNum;
        _peerName = peerName;
        _nodeStore = nodeStore;
        _temperatureFormatter = temperatureFormatter;
        _pressureFormatter = pressureFormatter;
    }

    partial void OnPeerNameChanged(string value) => OnPropertyChanged(nameof(TabHeader));

    // ----- History -----

    /// <summary>Every recorded position, oldest first.</summary>
    public ObservableCollection<LocationHistoryPoint> LocationHistory { get; } = new();

    /// <summary>Every recorded telemetry snapshot, oldest first.</summary>
    public ObservableCollection<TelemetryHistoryPoint> TelemetryHistory { get; } = new();

    // Per-pane views. A snapshot carries only the metric groups its packet had,
    // so each pane lists just the points with data for it — otherwise every
    // grid would be padded with blank rows from the other groups.
    public ObservableCollection<TelemetryHistoryPoint> DeviceTelemetryHistory { get; } = new();
    public ObservableCollection<TelemetryHistoryPoint> EnvironmentalTelemetryHistory { get; } = new();
    public ObservableCollection<TelemetryHistoryPoint> AirQualityTelemetryHistory { get; } = new();
    public ObservableCollection<TelemetryHistoryPoint> PowerTelemetryHistory { get; } = new();

    public bool HasLocationHistory => LocationHistory.Count > 0;
    public bool HasTelemetryHistory => TelemetryHistory.Count > 0;

    /// <summary>Loads this peer's stored history the first time a history view
    /// asks for it. Deferred so restoring a dozen DM tabs at startup doesn't
    /// read every peer's full history up front.</summary>
    public void EnsureHistoryLoaded()
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        if (_nodeStore is null) return;

        foreach (var row in _nodeStore.LocationHistory(NodeNum))
            LocationHistory.Add(new LocationHistoryPoint(
                row.Latitude, row.Longitude, row.AltitudeM,
                row.AltitudeM is int alt ? $"{alt} m" : string.Empty,
                row.TimestampUtc)
            { Id = row.Id });

        foreach (var row in _nodeStore.TelemetryHistory(NodeNum))
            AddTelemetryPoint(TelemetryHistoryPointFactory.FromRecord(row, _temperatureFormatter?.Invoke(), _pressureFormatter?.Invoke()));

        RaiseHistoryFlags();
    }

    /// <summary>Appends a newly received telemetry row (already persisted).</summary>
    public void AppendTelemetryRecord(NodeTelemetryHistoryRecord record)
    {
        AddTelemetryPoint(TelemetryHistoryPointFactory.FromRecord(record, _temperatureFormatter?.Invoke(), _pressureFormatter?.Invoke()));
        RaiseHistoryFlags();
    }

    /// <summary>Appends a newly received position (already persisted).</summary>
    public void AppendLocationRecord(NodeLocationHistoryRecord record)
    {
        LocationHistory.Add(new LocationHistoryPoint(
            record.Latitude, record.Longitude, record.AltitudeM,
            record.AltitudeM is int alt ? $"{alt} m" : string.Empty,
            record.TimestampUtc)
        { Id = record.Id });
        RaiseHistoryFlags();
    }

    private void AddTelemetryPoint(TelemetryHistoryPoint point)
    {
        TelemetryHistory.Add(point);
        if (point.HasDeviceTelemetry) DeviceTelemetryHistory.Add(point);
        if (point.HasEnvironmentalTelemetry) EnvironmentalTelemetryHistory.Add(point);
        if (point.HasAirQualityTelemetry) AirQualityTelemetryHistory.Add(point);
        if (point.HasPowerTelemetry) PowerTelemetryHistory.Add(point);
    }

    private void RemoveTelemetryPoint(TelemetryHistoryPoint point)
    {
        TelemetryHistory.Remove(point);
        DeviceTelemetryHistory.Remove(point);
        EnvironmentalTelemetryHistory.Remove(point);
        AirQualityTelemetryHistory.Remove(point);
        PowerTelemetryHistory.Remove(point);
    }

    private void RaiseHistoryFlags()
    {
        OnPropertyChanged(nameof(HasLocationHistory));
        OnPropertyChanged(nameof(HasTelemetryHistory));
    }

    [RelayCommand]
    private void ClearLocationHistory()
    {
        _nodeStore?.ClearLocationHistory(NodeNum);
        LocationHistory.Clear();
        RaiseHistoryFlags();
    }

    [RelayCommand]
    private void ClearTelemetryHistory()
    {
        _nodeStore?.ClearTelemetryHistory(NodeNum);
        TelemetryHistory.Clear();
        DeviceTelemetryHistory.Clear();
        EnvironmentalTelemetryHistory.Clear();
        AirQualityTelemetryHistory.Clear();
        PowerTelemetryHistory.Clear();
        RaiseHistoryFlags();
    }

    [RelayCommand]
    private void DeleteLocationHistoryPoint(LocationHistoryPoint? point)
    {
        if (point is null) return;
        if (point.Id != 0) _nodeStore?.DeleteLocationHistory(point.Id);
        LocationHistory.Remove(point);
        RaiseHistoryFlags();
    }

    [RelayCommand]
    private void DeleteTelemetryHistoryPoint(TelemetryHistoryPoint? point)
    {
        if (point is null) return;
        if (point.Id != 0) _nodeStore?.DeleteTelemetryHistory(point.Id);
        RemoveTelemetryPoint(point);
        RaiseHistoryFlags();
    }
}
