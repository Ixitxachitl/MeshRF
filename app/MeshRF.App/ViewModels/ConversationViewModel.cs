// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.App.Units;
using MeshRF.Mesh;
using MeshRF.Nodes;

namespace MeshRF.App.ViewModels;

/// <summary>A single label/value row of node telemetry shown in a DM tab.</summary>
public sealed record TelemetryItem(string Label, string Value);

/// <summary>A single historical position sample for a conversation peer.</summary>
public sealed record LocationHistoryPoint(double Latitude, double Longitude, int? AltitudeM, string AltitudeDisplay, DateTime TimestampUtc)
{
    private const string UiDateTimeFormat = "M/d/yyyy h:mm:ss tt";

    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    public string Display =>
    $"{TimestampLocal.ToString(UiDateTimeFormat, CultureInfo.CurrentCulture)}  {Latitude:0.#####}, {Longitude:0.#####}"
    + (string.IsNullOrWhiteSpace(AltitudeDisplay) ? string.Empty : $"  {AltitudeDisplay}");

    public long Id { get; init; }
}

/// <summary>A single telemetry snapshot for a conversation peer.</summary>
public sealed record TelemetryHistoryPoint(
    DateTime TimestampUtc,
    double? BatteryPct,
    double? VoltageV,
    double? ChannelUtilPct,
    double? AirUtilTxPct,
    double? UptimeSeconds,
    double? TemperatureC,
    double? RelativeHumidityPct,
    double? BarometricPressureHpa,
    double? GasResistanceMohm,
    double? IaqValue,
    double? Pm10Standard,
    double? Pm25Standard,
    double? Pm100Standard,
    double? Pm10Environmental,
    double? Pm25Environmental,
    double? Pm100Environmental,
    string Battery,
    string Voltage,
    string ChannelUtil,
    string AirUtilTx,
    string Uptime,
    string Temperature,
    string Humidity,
    string Pressure,
    string GasResistance,
    string AirQuality,
    string Pm10Std,
    string Pm25Std,
    string Pm100Std,
    string Pm10Env,
    string Pm25Env,
    string Pm100Env,
    string Signature)
{
    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();

    public long Id { get; init; }

    public bool HasDeviceTelemetry =>
        BatteryPct.HasValue || VoltageV.HasValue || ChannelUtilPct.HasValue ||
        AirUtilTxPct.HasValue || UptimeSeconds.HasValue;

    public bool HasEnvironmentalTelemetry =>
        TemperatureC.HasValue || RelativeHumidityPct.HasValue ||
        BarometricPressureHpa.HasValue || GasResistanceMohm.HasValue || IaqValue.HasValue;

    public bool HasAirQualityMetrics =>
        Pm10Standard.HasValue || Pm25Standard.HasValue || Pm100Standard.HasValue ||
        Pm10Environmental.HasValue || Pm25Environmental.HasValue || Pm100Environmental.HasValue;
}

/// <summary>
/// A direct-message conversation with a single peer node. Shown as a closable
/// tab alongside the channel tabs.
/// </summary>
public partial class ConversationViewModel : ObservableObject, ITabItem
{
    private readonly Action<ConversationViewModel, bool>? _onMuteRtttlChanged;
    private readonly Action<ConversationViewModel>? _onLocationHistoryChanged;
    private readonly Func<float, string>? _formatTemperature;
    private readonly Func<float, string>? _formatPressure;
    private readonly Func<int, string>? _formatAltitude;
    private readonly NodeStore? _nodeStore;
    private bool _syncingNodeMute;

    public ConversationViewModel(uint nodeNum, string? peerName = null,
                                 Action<ConversationViewModel, bool>? onMuteRtttlChanged = null,
                                 Action<ConversationViewModel>? onLocationHistoryChanged = null,
                                 Func<float, string>? formatTemperature = null,
                                 Func<float, string>? formatPressure = null,
                                 Func<int, string>? formatAltitude = null,
                                 NodeStore? nodeStore = null)
    {
        NodeNum = nodeNum;
        _peerName = string.IsNullOrWhiteSpace(peerName) ? string.Empty : peerName!;
        _onMuteRtttlChanged = onMuteRtttlChanged;
        _onLocationHistoryChanged = onLocationHistoryChanged;
        _formatTemperature = formatTemperature;
        _formatPressure = formatPressure;
        _formatAltitude = formatAltitude;
        _nodeStore = nodeStore;
    }

    /// <summary>32-bit node number of the conversation peer.</summary>
    public uint NodeNum { get; }

    /// <summary>Meshtastic-style id, e.g. <c>!a1b2c3d4</c>.</summary>
    public string PeerId => $"!{NodeNum:x8}";

    [ObservableProperty]
    private string _peerName;

    /// <summary>Latest known node record for this peer (drives the telemetry
    /// panel). Set on open and refreshed whenever the node table reloads.</summary>
    [ObservableProperty]
    private NodeRecord? _node;

    /// <summary>Formatted telemetry rows for the peer node; empty when unknown.</summary>
    public ObservableCollection<TelemetryItem> Telemetry { get; } = new();

    /// <summary>Recent historical peer positions collected while this tab is open.</summary>
    public ObservableCollection<LocationHistoryPoint> LocationHistory { get; } = new();

    /// <summary>Recent telemetry snapshots collected while this tab is open.</summary>
    public ObservableCollection<TelemetryHistoryPoint> TelemetryHistory { get; } = new();

    /// <summary>Telemetry history rows that contain device metrics.</summary>
    public ObservableCollection<TelemetryHistoryPoint> DeviceTelemetryHistory { get; } = new();

    /// <summary>Telemetry history rows that contain environmental metrics.</summary>
    public ObservableCollection<TelemetryHistoryPoint> EnvironmentalTelemetryHistory { get; } = new();

    /// <summary>Telemetry history rows that contain air quality metrics.</summary>
    public ObservableCollection<TelemetryHistoryPoint> AirQualityTelemetryHistory { get; } = new();

    /// <summary>True when at least one telemetry value is available.</summary>
    public bool HasTelemetry => Telemetry.Count > 0;

    /// <summary>True when at least one location sample exists.</summary>
    public bool HasLocationHistory => LocationHistory.Count > 0;

    /// <summary>True when at least one telemetry snapshot exists.</summary>
    public bool HasTelemetryHistory => TelemetryHistory.Count > 0;

    /// <summary>Direct messages exchanged with this peer, newest last.</summary>
    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    /// <summary>Text typed into this conversation's compose box.</summary>
    [ObservableProperty]
    private string _composeText = string.Empty;

    /// <summary>Suppress the incoming-text RTTTL ringtone for this peer.</summary>
    [ObservableProperty]
    private bool _muteRtttl;

    /// <summary>When true, keep this conversation tailed to the newest message.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>True when this tab has unseen incoming activity.</summary>
    [ObservableProperty]
    private bool _tabNeedsAttention;

    public string TabHeader =>
        string.IsNullOrEmpty(PeerName) ? PeerId : PeerName;

    public bool CanClose => true;

    partial void OnPeerNameChanged(string value) => OnPropertyChanged(nameof(TabHeader));

    partial void OnNodeChanged(NodeRecord? value)
    {
        _syncingNodeMute = true;
        MuteRtttl = value?.MuteRtttl == true;
        _syncingNodeMute = false;
        RebuildTelemetry();
        AppendLocationHistory(value);
    }

    partial void OnMuteRtttlChanged(bool value)
    {
        if (!_syncingNodeMute)
            _onMuteRtttlChanged?.Invoke(this, value);
    }

    private void RebuildTelemetry()
    {
        Telemetry.Clear();
        var n = Node;
        if (n is not null)
        {
            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    Telemetry.Add(new TelemetryItem(label, value!));
            }

            Add("Long name", n.LongName);
            Add("Short name", n.ShortName);
            Add("Hardware", HardwareModels.Display(n.HwModel));
            Add("Role", n.Role);
            if (n.SeenViaMqtt) Add("Via MQTT", "Yes");
            if (n.BatteryPct is byte bat) Add("Battery", $"{bat}%");
            if (n.VoltageV is float volt) Add("Voltage", $"{volt:0.00} V");
            if (n.ChannelUtilPct is float chUtil) Add("Channel util", $"{chUtil:0.0}%");
            if (n.AirUtilTxPct is float airUtil) Add("Air util TX", $"{airUtil:0.0}%");
            if (n.UptimeSeconds is uint up) Add("Uptime", FormatUptime(up));
            if (n.TemperatureC is float temp)
                Add("Temperature", _formatTemperature?.Invoke(temp) ?? $"{temp:0.0} \u00B0C");
            if (n.RelativeHumidityPct is float hum) Add("Humidity", $"{hum:0.0}%");
            if (n.BarometricPressureHpa is float pres) Add("Pressure", _formatPressure?.Invoke(pres) ?? $"{pres:0.0} hPa");
            if (n.GasResistanceMohm is float gas) Add("Gas resistance", $"{gas:0.0} M\u03A9");
            if (n.Iaq is int iaq) Add("Air quality (IAQ)", iaq.ToString());
            if (n.Pm25Standard is uint pm25s) Add("PM2.5 std", $"{pm25s} \u03BCg/m\u00B3");
            if (n.Pm100Standard is uint pm100s) Add("PM10 std", $"{pm100s} \u03BCg/m\u00B3");
            if (n.Pm10Standard is uint pm1s) Add("PM1.0 std", $"{pm1s} \u03BCg/m\u00B3");
            if (n.Pm25Environmental is uint pm25e) Add("PM2.5 env", $"{pm25e} \u03BCg/m\u00B3");
            if (n.Pm100Environmental is uint pm100e) Add("PM10 env", $"{pm100e} \u03BCg/m\u00B3");
            if (n.Pm10Environmental is uint pm1e) Add("PM1.0 env", $"{pm1e} \u03BCg/m\u00B3");
            if (n.SnrDb is float snr) Add("SNR", $"{snr:0.0} dB");
            if (n.RssiDbm is float rssi) Add("RSSI", $"{rssi:0} dBm");
            if (n.HopsAway is byte hops) Add("Hops away", hops.ToString());
            if (n.Latitude is double lat && n.Longitude is double lon)
                Add("Position", $"{lat:0.#####}, {lon:0.#####}");
            if (n.AltitudeM is int alt) Add("Altitude", _formatAltitude?.Invoke(alt) ?? $"{alt} m");
            if (n.LastHeardEpoch != 0)
                Add("Last heard", n.LastHeard.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.CurrentCulture));
        }

        OnPropertyChanged(nameof(HasTelemetry));
    }

    /// <summary>Rebuild telemetry labels/values after a formatting preference change.</summary>
    public void RefreshTelemetryFormatting()
    {
        RebuildTelemetry();
        LoadNodeHistories();
    }

    public void LoadNodeHistories()
    {
        if (_nodeStore is null) return;

        LocationHistory.Clear();
        foreach (var point in _nodeStore.LocationHistory(NodeNum))
        {
            LocationHistory.Add(new LocationHistoryPoint(
                point.Latitude,
                point.Longitude,
                point.AltitudeM,
                point.AltitudeM is int altitude ? (_formatAltitude?.Invoke(altitude) ?? $"{altitude} m") : string.Empty,
                point.TimestampUtc)
            { Id = point.Id });
        }

        TelemetryHistory.Clear();
        DeviceTelemetryHistory.Clear();
        EnvironmentalTelemetryHistory.Clear();
        AirQualityTelemetryHistory.Clear();
        foreach (var point in _nodeStore.TelemetryHistory(NodeNum))
            AddTelemetryHistoryPoint(BuildTelemetryHistoryPoint(point));

        OnPropertyChanged(nameof(HasLocationHistory));
        OnPropertyChanged(nameof(HasTelemetryHistory));
        _onLocationHistoryChanged?.Invoke(this);
    }

    private static string FormatUptime(uint seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{(int)ts.TotalMinutes}m";
    }

    public void Add(ChannelMessage message)
    {
        Messages.Add(message);
        if (Messages.Count > 1000) Messages.RemoveAt(0);
    }

    [RelayCommand]
    private void ClearLocationHistory()
    {
        if (LocationHistory.Count == 0) return;
        _nodeStore?.ClearLocationHistory(NodeNum);
        _nodeStore?.ClearNodeLocation(NodeNum);
        if (Node is not null)
        {
            Node.Latitude  = null;
            Node.Longitude = null;
            Node.AltitudeM = null;
        }
        LocationHistory.Clear();
        OnPropertyChanged(nameof(HasLocationHistory));
        _onLocationHistoryChanged?.Invoke(this);
    }

    [RelayCommand]
    private void ClearTelemetryHistory()
    {
        if (TelemetryHistory.Count == 0) return;
        _nodeStore?.ClearTelemetryHistory(NodeNum);
        _nodeStore?.ClearNodeTelemetry(NodeNum);
        if (Node is not null)
        {
            Node.BatteryPct             = null;
            Node.VoltageV               = null;
            Node.ChannelUtilPct         = null;
            Node.AirUtilTxPct           = null;
            Node.UptimeSeconds          = null;
            Node.TemperatureC           = null;
            Node.RelativeHumidityPct    = null;
            Node.BarometricPressureHpa  = null;
            Node.GasResistanceMohm      = null;
            Node.Iaq                    = null;
            Node.Pm10Standard           = null;
            Node.Pm25Standard           = null;
            Node.Pm100Standard          = null;
            Node.Pm10Environmental      = null;
            Node.Pm25Environmental      = null;
            Node.Pm100Environmental     = null;
        }
        TelemetryHistory.Clear();
        DeviceTelemetryHistory.Clear();
        EnvironmentalTelemetryHistory.Clear();
        AirQualityTelemetryHistory.Clear();
        RebuildTelemetry();
        OnPropertyChanged(nameof(HasTelemetryHistory));
    }

    [RelayCommand]
    private void DeleteLocationHistoryPoint(LocationHistoryPoint? point)
    {
        if (point is null) return;
        if (!LocationHistory.Remove(point)) return;
        if (point.Id != 0)
            _nodeStore?.DeleteLocationHistory(point.Id);
        OnPropertyChanged(nameof(HasLocationHistory));
        _onLocationHistoryChanged?.Invoke(this);
    }

    [RelayCommand]
    private void DeleteTelemetryHistoryPoint(TelemetryHistoryPoint? point)
    {
        if (point is null) return;
        if (!TelemetryHistory.Remove(point)) return;
        DeviceTelemetryHistory.Remove(point);
        EnvironmentalTelemetryHistory.Remove(point);
        AirQualityTelemetryHistory.Remove(point);
        if (point.Id != 0)
            _nodeStore?.DeleteTelemetryHistory(point.Id);
        OnPropertyChanged(nameof(HasTelemetryHistory));
    }

    [RelayCommand]
    private void CopyMessages()
    {
        if (Messages.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Messages.Select(m => m.Display))); }
        catch { }
    }

    private void AppendLocationHistory(NodeRecord? node)
    {
        if (node?.Latitude is not double lat || node.Longitude is not double lon)
            return;

        var sampleTimeUtc = node.LastHeardEpoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(node.LastHeardEpoch).UtcDateTime
            : DateTime.UtcNow;

        var last = LocationHistory.LastOrDefault();
        if (last is not null)
        {
            bool sameCoord = Math.Abs(last.Latitude - lat) < 1e-7
                && Math.Abs(last.Longitude - lon) < 1e-7
                && last.AltitudeM == node.AltitudeM;
            if (sameCoord)
                return;
        }

        var point = new LocationHistoryPoint(
            lat,
            lon,
            node.AltitudeM,
            node.AltitudeM is int altitude ? (_formatAltitude?.Invoke(altitude) ?? $"{altitude} m") : string.Empty,
            sampleTimeUtc);
        LocationHistory.Add(point);
        if (LocationHistory.Count > 500)
            LocationHistory.RemoveAt(0);

        OnPropertyChanged(nameof(HasLocationHistory));
        _onLocationHistoryChanged?.Invoke(this);
    }

    private void AppendTelemetryHistory(NodeRecord? node)
    {
        if (node is null || !HasTelemetrySnapshot(node))
            return;

        var signature = BuildTelemetrySignature(node);
        var last = TelemetryHistory.LastOrDefault();
        if (last?.Signature == signature)
            return;

        var sampleTimeUtc = node.LastHeardEpoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds(node.LastHeardEpoch).UtcDateTime
            : DateTime.UtcNow;

        var record = new NodeTelemetryHistoryRecord(
            0,
            NodeNum,
            sampleTimeUtc,
            node.BatteryPct,
            node.VoltageV,
            node.ChannelUtilPct,
            node.AirUtilTxPct,
            node.UptimeSeconds,
            node.TemperatureC,
            node.RelativeHumidityPct,
            node.BarometricPressureHpa,
            node.GasResistanceMohm,
            node.Iaq,
            (double?)node.Pm10Standard,
            (double?)node.Pm25Standard,
            (double?)node.Pm100Standard,
            (double?)node.Pm10Environmental,
            (double?)node.Pm25Environmental,
            (double?)node.Pm100Environmental,
            signature);

        var point = BuildTelemetryHistoryPoint(record);
        if (_nodeStore is not null)
        {
            var id = _nodeStore.AddTelemetryHistory(record);
            point = point with { Id = id };
        }

        AddTelemetryHistoryPoint(point);

        if (TelemetryHistory.Count > 500)
            RemoveTelemetryHistoryPoint(TelemetryHistory[0]);

        OnPropertyChanged(nameof(HasTelemetryHistory));
    }

    private void AddTelemetryHistoryPoint(TelemetryHistoryPoint point)
    {
        TelemetryHistory.Add(point);
        if (point.HasDeviceTelemetry)
            DeviceTelemetryHistory.Add(point);
        if (point.HasEnvironmentalTelemetry)
            EnvironmentalTelemetryHistory.Add(point);
        if (point.HasAirQualityMetrics)
            AirQualityTelemetryHistory.Add(point);
    }

    public void AppendTelemetryHistoryRecord(NodeTelemetryHistoryRecord record)
    {
        // Guard against duplicates that arise when a LoadNodeHistories reload
        // completes after the background DB write, and then the write's async
        // UI dispatch also fires and tries to add the same record again.
        if (record.Id > 0 && TelemetryHistory.Any(p => p.Id == record.Id))
            return;

        var point = BuildTelemetryHistoryPoint(record);
        AddTelemetryHistoryPoint(point);

        if (TelemetryHistory.Count > 500)
            RemoveTelemetryHistoryPoint(TelemetryHistory[0]);

        OnPropertyChanged(nameof(HasTelemetryHistory));
    }

    private void RemoveTelemetryHistoryPoint(TelemetryHistoryPoint point)
    {
        TelemetryHistory.Remove(point);
        DeviceTelemetryHistory.Remove(point);
        EnvironmentalTelemetryHistory.Remove(point);
        AirQualityTelemetryHistory.Remove(point);
    }

    private TelemetryHistoryPoint BuildTelemetryHistoryPoint(NodeTelemetryHistoryRecord record) =>
        new(
            record.TimestampUtc,
            record.BatteryPct,
            record.VoltageV,
            record.ChannelUtilPct,
            record.AirUtilTxPct,
            record.UptimeSeconds,
            record.TemperatureC,
            record.RelativeHumidityPct,
            record.BarometricPressureHpa,
            record.GasResistanceMohm,
            record.IaqValue,
            record.Pm10Standard,
            record.Pm25Standard,
            record.Pm100Standard,
            record.Pm10Environmental,
            record.Pm25Environmental,
            record.Pm100Environmental,
            record.BatteryPct is double bat ? $"{bat:0}%" : string.Empty,
            record.VoltageV is double volt ? $"{volt:0.00} V" : string.Empty,
            record.ChannelUtilPct is double chUtil ? $"{chUtil:0.0}%" : string.Empty,
            record.AirUtilTxPct is double airUtil ? $"{airUtil:0.0}%" : string.Empty,
            record.UptimeSeconds is double up ? FormatUptime((uint)Math.Max(0, up)) : string.Empty,
            record.TemperatureC is double temp ? (_formatTemperature?.Invoke((float)temp) ?? $"{temp:0.0} \u00B0C") : string.Empty,
            record.RelativeHumidityPct is double hum ? $"{hum:0.0}%" : string.Empty,
            record.BarometricPressureHpa is double pres ? (_formatPressure?.Invoke((float)pres) ?? $"{pres:0.0} hPa") : string.Empty,
            record.GasResistanceMohm is double gas ? $"{gas:0.0} M\u03A9" : string.Empty,
            record.IaqValue is double iaq ? iaq.ToString("0", CultureInfo.InvariantCulture) : string.Empty,
            record.Pm10Standard      is double p1s   ? $"{p1s:0} \u03BCg/m\u00B3"   : string.Empty,
            record.Pm25Standard      is double p25s  ? $"{p25s:0} \u03BCg/m\u00B3"  : string.Empty,
            record.Pm100Standard     is double p100s ? $"{p100s:0} \u03BCg/m\u00B3" : string.Empty,
            record.Pm10Environmental  is double p1e   ? $"{p1e:0} \u03BCg/m\u00B3"   : string.Empty,
            record.Pm25Environmental  is double p25e  ? $"{p25e:0} \u03BCg/m\u00B3"  : string.Empty,
            record.Pm100Environmental is double p100e ? $"{p100e:0} \u03BCg/m\u00B3" : string.Empty,
            record.Signature)
        { Id = record.Id };

    private static bool HasTelemetrySnapshot(NodeRecord node) =>
        node.BatteryPct.HasValue
        || node.VoltageV.HasValue
        || node.ChannelUtilPct.HasValue
        || node.AirUtilTxPct.HasValue
        || node.TemperatureC.HasValue
        || node.RelativeHumidityPct.HasValue
        || node.BarometricPressureHpa.HasValue
        || node.GasResistanceMohm.HasValue
        || node.Iaq.HasValue
        || node.Pm10Standard.HasValue
        || node.Pm25Standard.HasValue
        || node.Pm100Standard.HasValue;

    private static string BuildTelemetrySignature(NodeRecord node) => string.Join("|",
        FormatNullable(node.BatteryPct),
        FormatNullable(node.VoltageV),
        FormatNullable(node.ChannelUtilPct),
        FormatNullable(node.AirUtilTxPct),
        FormatNullable(node.TemperatureC),
        FormatNullable(node.RelativeHumidityPct),
        FormatNullable(node.BarometricPressureHpa),
        FormatNullable(node.GasResistanceMohm),
        FormatNullable(node.Iaq),
        FormatNullable(node.Pm25Standard),
        FormatNullable(node.Pm100Standard));

    private static string FormatNullable<T>(T? value)
        where T : struct, IFormattable =>
        value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : string.Empty;
}
