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

    // ----- Live peer snapshot -----

    /// <summary>Latest node record for this peer; drives the telemetry panel.</summary>
    [ObservableProperty]
    private NodeRecord? _node;

    /// <summary>Formatted current values for the peer, newest snapshot only.</summary>
    public ObservableCollection<TelemetryItem> Telemetry { get; } = new();

    public bool HasTelemetry => Telemetry.Count > 0;

    /// <summary>Suppress the incoming-message ringtone for this peer. Stored on
    /// the node record, so it is the same flag the node grid and the channel
    /// mute check.</summary>
    [ObservableProperty]
    private bool _muteRtttl;

    // Set while pushing the node's stored value into the property, so the
    // change handler doesn't write it straight back to the store.
    private bool _syncingMute;

    partial void OnNodeChanged(NodeRecord? value) => RefreshNodeSnapshot();

    partial void OnMuteRtttlChanged(bool value)
    {
        if (_syncingMute) return;
        _nodeStore?.SetMuteRtttl(NodeNum, value);
        if (Node is not null) Node.MuteRtttl = value;
    }

    /// <summary>Re-reads the bound node into the telemetry panel. Call directly
    /// when the record was updated in place, which doesn't raise the setter.</summary>
    public void RefreshNodeSnapshot()
    {
        _syncingMute = true;
        MuteRtttl = Node?.MuteRtttl == true;
        _syncingMute = false;
        RebuildTelemetry();
    }

    private void RebuildTelemetry()
    {
        Telemetry.Clear();
        var n = Node;
        if (n is not null)
        {
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) Telemetry.Add(new TelemetryItem(label, value!));
            }

            var temp = _temperatureFormatter?.Invoke();
            var pres = _pressureFormatter?.Invoke();

            Add("Long name", n.LongName);
            Add("Short name", n.ShortName);
            Add("Hardware", n.HwModel);
            Add("Role", n.Role);
            if (n.IsUnmessagable.HasValue) Add("Unmessagable", n.IsUnmessagable.Value ? "Yes" : "No");
            if (n.SeenViaMqtt == true) Add("Via MQTT", "Yes");
            if (n.BatteryPct is byte bat) Add("Battery", $"{bat}%");
            if (n.VoltageV is float volt) Add("Voltage", $"{volt:0.00} V");
            if (n.ChannelUtilPct is float chUtil) Add("Channel util", $"{chUtil:0.0}%");
            if (n.AirUtilTxPct is float airUtil) Add("Air util TX", $"{airUtil:0.0}%");
            if (n.UptimeSeconds is uint up) Add("Uptime", FormatUptime(up));
            if (n.TemperatureC is float t) Add("Temperature", temp?.Invoke(t) ?? $"{t:0.0} °C");
            if (n.RelativeHumidityPct is float hum) Add("Humidity", $"{hum:0.0}%");
            if (n.BarometricPressureHpa is float p) Add("Pressure", pres?.Invoke(p) ?? $"{p:0.0} hPa");
            if (n.GasResistanceMohm is float gas) Add("Gas resistance", $"{gas:0.0} MΩ");
            if (n.Iaq is int iaq) Add("Air quality (IAQ)", iaq.ToString());
            if (n.Pm25Standard is uint pm25s) Add("PM2.5 std", $"{pm25s} μg/m³");
            if (n.Pm100Standard is uint pm100s) Add("PM10 std", $"{pm100s} μg/m³");
            if (n.Pm10Standard is uint pm1s) Add("PM1.0 std", $"{pm1s} μg/m³");
            if (n.Pm25Environmental is uint pm25e) Add("PM2.5 env", $"{pm25e} μg/m³");
            if (n.Pm100Environmental is uint pm100e) Add("PM10 env", $"{pm100e} μg/m³");
            if (n.Pm10Environmental is uint pm1e) Add("PM1.0 env", $"{pm1e} μg/m³");
            if (n.Ch1VoltageV is float c1v) Add("CH1 voltage", $"{c1v:0.000} V");
            if (n.Ch1CurrentMa is float c1i) Add("CH1 current", $"{c1i:0.0} mA");
            if (n.Ch2VoltageV is float c2v) Add("CH2 voltage", $"{c2v:0.000} V");
            if (n.Ch2CurrentMa is float c2i) Add("CH2 current", $"{c2i:0.0} mA");
            if (n.Ch3VoltageV is float c3v) Add("CH3 voltage", $"{c3v:0.000} V");
            if (n.Ch3CurrentMa is float c3i) Add("CH3 current", $"{c3i:0.0} mA");
            if (n.SnrDb is float snr) Add("SNR", $"{snr:0.0} dB");
            if (n.RssiDbm is float rssi) Add("RSSI", $"{rssi:0} dBm");
            if (n.HopsAway is byte hops) Add("Hops away", hops.ToString());
            if (n.Latitude is double lat && n.Longitude is double lon)
                Add("Position", $"{lat:0.#####}, {lon:0.#####}");
            if (n.AltitudeM is int altM) Add("Altitude", $"{altM} m");
            if (n.LastHeardEpoch > 0)
                Add("Last heard", UiFormats.Stamp(
                    DateTimeOffset.FromUnixTimeSeconds(n.LastHeardEpoch).LocalDateTime));
        }
        OnPropertyChanged(nameof(HasTelemetry));
    }

    private static string FormatUptime(uint seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes}m";
        return $"{span.Seconds}s";
    }

    [RelayCommand]
    private void ClearMessages()
    {
        Messages.Clear();
    }

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
