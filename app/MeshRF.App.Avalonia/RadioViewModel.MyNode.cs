// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Location;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Nodes;
using MeshRF.Telemetry;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The rest of the "My Node — Identity &amp; Settings" panel: derived identity
/// fields, the USB serial GPS, the six auto-report schedules, and the
/// weather/air-quality sources that fill environment and AQ telemetry.
/// Completes the port of MeshRF.App's NodeIdentityWindow.
/// </summary>
public partial class RadioViewModel
{
    // ----- Derived identity -----

    /// <summary>MAC derived from the node number (<c>02:00:xx:xx:xx:xx</c>),
    /// matching the Meshtastic convention that the 32-bit node number is the
    /// low four bytes. Read-only, like the node id it comes from: the whole
    /// chain hangs off the key pair.</summary>
    public string MyMacAddress
    {
        get
        {
            uint n = _rxHost.MyNodeNum;
            return n == 0
                ? string.Empty
                : $"02:00:{(n >> 24) & 0xFF:x2}:{(n >> 16) & 0xFF:x2}:{(n >> 8) & 0xFF:x2}:{n & 0xFF:x2}";
        }
    }

    /// <summary>Persists our own node record so the configured name resolves
    /// wherever a node number is shown. Without this our node either has no
    /// row at all or a stale one, and our messages fall back to the raw ID.</summary>
    public void RefreshSelfNode()
    {
        if (!_settingsLoaded) return;
        _rxHost.UpsertSelf(
            MyLongName, MyShortName, MyHwModel, MyRole, MyNodeStatus,
            Convert.ToHexString(TryParseKeyBase64(MyPublicKey)));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrivateKeyHidden))]
    private bool _isPrivateKeyRevealed;

    public bool IsPrivateKeyHidden => !IsPrivateKeyRevealed;

    [RelayCommand]
    private void ToggleRevealPrivateKey() => IsPrivateKeyRevealed = !IsPrivateKeyRevealed;

    /// <summary>Our own node number, for the self-history buttons.</summary>
    public uint MyNodeNumber => _rxHost.MyNodeNum;

    /// <summary>How we're labelled in message and reaction attributions.</summary>
    public string MyDisplayName => _rxHost.NodeDisplayName(_rxHost.MyNodeNum);

    /// <summary>A conversation view model to render history against. Reuses the
    /// open DM tab when there is one so the window and the tab share state;
    /// otherwise builds a detached one, which is how history is shown for a
    /// node with no conversation — including our own.</summary>
    public ConversationTabViewModel HistoryConversationFor(uint nodeNum)
    {
        var existing = Tabs.OfType<ConversationTabViewModel>().FirstOrDefault(c => c.NodeNum == nodeNum);
        if (existing is not null) return existing;

        return new ConversationTabViewModel(
            nodeNum,
            nodeNum == _rxHost.MyNodeNum ? (MyLongName ?? "Me") : _rxHost.NodeDisplayName(nodeNum),
            _nodeStore,
            () => FormatTemperature,
            () => (Func<float, string>)(hpa => $"{hpa:0.0} hPa"));
    }

    // ----- USB serial GPS -----

    private readonly UsbSerialGpsService _gpsService = new();

    [ObservableProperty] private string _gpsStatus = "Manual location selected.";

    private void InitGps()
    {
        _gpsService.StatusChanged += status =>
            Dispatcher.UIThread.Post(() => { if (IsUsbSerialLocationSource) GpsStatus = status; });
        _gpsService.FixReceived += fix => Dispatcher.UIThread.Post(() => ApplyGpsFix(fix));
        ApplyLocationSource(startOrStop: true);
    }

    private GpsSerialOptions BuildGpsOptions() => new(
        string.IsNullOrWhiteSpace(GpsSerialPort) ? null : GpsSerialPort.Trim(),
        int.TryParse(GpsBaudRateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) && baud > 0
            ? baud : null);

    /// <summary>Starts or stops the reader to match the selected source. The
    /// lat/lon boxes stay the single source of truth for our position: a fix
    /// writes into them, which is also what the map marker reads.</summary>
    private void ApplyLocationSource(bool startOrStop)
    {
        var options = BuildGpsOptions();
        _gpsService.UpdateOptions(options);

        if (IsUsbSerialLocationSource)
        {
            if (startOrStop) _gpsService.Restart();
            GpsStatus = options.PortName is null && options.BaudRate is null
                ? "USB GPS: auto-detecting port and baud..."
                : $"USB GPS: waiting on {options.PortName ?? "auto"} @ {(options.BaudRate?.ToString(CultureInfo.InvariantCulture)) ?? "auto"}...";
        }
        else
        {
            if (startOrStop) _gpsService.Stop();
            GpsStatus = "Manual location selected.";
        }
    }

    private void ApplyGpsFix(GpsFix fix)
    {
        GpsStatus = $"USB GPS: {fix.PortName} @ {fix.BaudRate} baud  {fix.Latitude:F6}, {fix.Longitude:F6}" +
                    (fix.AltitudeM is int a ? $"  alt {a} m" : string.Empty);
        if (!IsUsbSerialLocationSource) return;

        HomeLatitudeText = fix.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        HomeLongitudeText = fix.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        if (fix.AltitudeM is int alt)
            HomeAltitudeText = alt.ToString(CultureInfo.InvariantCulture);
    }

    // ----- Weather / air quality sources -----

    private readonly OpenMeteoClient _openMeteo = new();

    [ObservableProperty] private string _weatherTelemetryStatus = "Weather telemetry: idle.";
    [ObservableProperty] private string _airQualityTelemetryStatus = "Air quality telemetry: idle.";

    /// <summary>Answers a directed request from a peer. Without this our
    /// NodeInfo only ever leaves on the auto-report schedule or a manual
    /// click, so peers that ask for our name get nothing back.</summary>
    private void HandleAutoReplyRequest(PortNum port, uint to, string? channelName)
    {
        if (!CanTransmit || to == 0 || to == 0xFFFFFFFFu) return;
        var channel = _rxHost.FindChannelByName(channelName) ?? PrimaryChannel();
        if (channel is null) return;

        try
        {
            switch (port)
            {
                case PortNum.NodeInfo:
                    var nodeInfo = MeshEncoder.EncodeNodeInfo(channel, _rxHost.MyNodeNum, NextPacketId(),
                        MyLongName, MyShortName,
                        hwModel: (uint)Math.Max(0, HardwareModels.Id(MyHwModel)), role: RoleEnumValue(MyRole),
                        publicKey: TryParseKeyBase64(MyPublicKey),
                        to: to, hopLimit: (byte)HopLimit, wantResponse: false, okToMqtt: OkToMqtt,
                        isLicensed: MyIsLicensed, isUnmessagable: MyIsUnmessagable);
                    TransmitBackground(nodeInfo);
                    break;

                case PortNum.Position:
                    if (channel.PositionPrecision == 0) return;
                    if (!TryGetHomeLocation(out double lat, out double lon)) return;
                    int? alt = int.TryParse(HomeAltitudeText, NumberStyles.Integer,
                                            CultureInfo.InvariantCulture, out var a) ? a : null;
                    var position = MeshEncoder.EncodePosition(channel, _rxHost.MyNodeNum, NextPacketId(), lat, lon,
                        altitudeM: alt, precisionBits: channel.PositionPrecision,
                        to: to, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt);
                    TransmitBackground(position);
                    break;

            }
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-reply failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Answers a directed telemetry request with the metric group that was
    /// asked for. Firmware has a module per group, each replying only to its
    /// own variant; here one handler covers them, and an unspecified request
    /// gets device metrics — the variant every node can answer.
    /// </summary>
    private async void HandleTelemetryReplyRequest(uint to, string? channelName, TelemetryVariants wanted)
    {
        if (!CanTransmit || to == 0 || to == 0xFFFFFFFFu) return;
        var channel = _rxHost.FindChannelByName(channelName) ?? PrimaryChannel();
        if (channel is null) return;

        try
        {
            if (wanted.HasFlag(TelemetryVariants.Environment) ||
                wanted.HasFlag(TelemetryVariants.AirQuality))
            {
                // Both are sourced from the home location's weather, so a
                // request we can't locate can't be answered.
                if (!TryGetHomeLocation(out double lat, out double lon)) return;

                if (wanted.HasFlag(TelemetryVariants.Environment) &&
                    await _openMeteo.GetWeatherAsync(lat, lon) is { } weather)
                {
                    TransmitBackground(MeshEncoder.EncodeTelemetryEnvironmentMetrics(
                        channel, _rxHost.MyNodeNum, NextPacketId(),
                        temperatureC: weather.TemperatureC,
                        relativeHumidityPct: weather.RelativeHumidityPct,
                        barometricPressureHpa: weather.BarometricPressureHpa,
                        to: to, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt));
                }

                if (wanted.HasFlag(TelemetryVariants.AirQuality) &&
                    await _openMeteo.GetAirQualityAsync(lat, lon) is { } aq)
                {
                    TransmitBackground(MeshEncoder.EncodeTelemetryAirQualityMetrics(
                        channel, _rxHost.MyNodeNum, NextPacketId(),
                        pm25Standard: aq.Pm25Standard, pm100Standard: aq.Pm100Standard,
                        to: to, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt));
                }
                return;
            }

            TransmitBackground(MeshEncoder.EncodeTelemetryDeviceMetrics(
                channel, _rxHost.MyNodeNum, NextPacketId(),
                batteryLevel: 101, // 101 = mains-powered, the sentinel this app reports.
                to: to, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt));
        }
        catch (Exception ex)
        {
            StatusText = $"Telemetry reply failed: {ex.Message}";
        }
    }

    private void InitTelemetrySources()
    {
        _openMeteo.WeatherStatusChanged += s => Dispatcher.UIThread.Post(() => WeatherTelemetryStatus = s);
        _openMeteo.AirQualityStatusChanged += s => Dispatcher.UIThread.Post(() => AirQualityTelemetryStatus = s);
    }

    // ----- Extra self-sends (the three the quick-send bar doesn't cover) -----

    [RelayCommand]
    private Task SendSelfNodeStatus() => SendNodeStatusOnChannelAsync(null, null);

    public async Task SendNodeStatusOnChannelAsync(ChannelConfig? channel, uint? to)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }
        if (string.IsNullOrWhiteSpace(MyNodeStatus)) { StatusText = "Set a status text first."; return; }
        channel ??= PrimaryChannel();
        if (channel is null) return;
        var frame = MeshEncoder.EncodeNodeStatus(channel, _rxHost.MyNodeNum, NextPacketId(),
            MyNodeStatus.Trim(), to: to ?? 0xFFFFFFFFu, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt,
            xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey);
        StatusText = await TransmitFrameAsync(frame) ? "Sent node status." : "Transmit failed.";
    }

    [RelayCommand]
    private Task SendSelfEnvironmentMetrics() => SendEnvironmentMetricsOnChannelAsync(null, null);

    public async Task SendEnvironmentMetricsOnChannelAsync(ChannelConfig? channel, uint? to)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }
        channel ??= PrimaryChannel();
        if (channel is null) return;
        if (!TryGetHomeLocation(out double lat, out double lon))
        {
            StatusText = "Set your home location before sending environment telemetry.";
            return;
        }

        var weather = await _openMeteo.GetWeatherAsync(lat, lon, forceRefresh: true);
        if (weather is null) { StatusText = "Environment telemetry: no weather data."; return; }

        var frame = MeshEncoder.EncodeTelemetryEnvironmentMetrics(channel, _rxHost.MyNodeNum, NextPacketId(),
            temperatureC: weather.TemperatureC,
            relativeHumidityPct: weather.RelativeHumidityPct,
            barometricPressureHpa: weather.BarometricPressureHpa,
            to: to ?? 0xFFFFFFFFu, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt,
            xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey);
        if (await TransmitFrameAsync(frame))
        {
            StatusText = "Sent environment metrics.";
            _rxHost.RecordSelfTelemetry(new MeshTelemetry
            {
                TemperatureC = weather.TemperatureC,
                RelativeHumidityPct = weather.RelativeHumidityPct,
                BarometricPressureHpa = weather.BarometricPressureHpa,
            });
        }
        else StatusText = "Transmit failed.";
    }

    [RelayCommand]
    private Task SendSelfAirQualityMetrics() => SendAirQualityMetricsOnChannelAsync(null, null);

    public async Task SendAirQualityMetricsOnChannelAsync(ChannelConfig? channel, uint? to)
    {
        if (!CanTransmit) { StatusText = "Set your node ID and a TX-capable device first."; return; }
        channel ??= PrimaryChannel();
        if (channel is null) return;
        if (!TryGetHomeLocation(out double lat, out double lon))
        {
            StatusText = "Set your home location before sending air quality telemetry.";
            return;
        }

        var aq = await _openMeteo.GetAirQualityAsync(lat, lon, forceRefresh: true);
        if (aq is null) { StatusText = "Air quality telemetry: no data."; return; }

        var frame = MeshEncoder.EncodeTelemetryAirQualityMetrics(channel, _rxHost.MyNodeNum, NextPacketId(),
            pm25Standard: aq.Pm25Standard, pm100Standard: aq.Pm100Standard,
            to: to ?? 0xFFFFFFFFu, hopLimit: (byte)HopLimit, okToMqtt: OkToMqtt,
            xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey);
        if (await TransmitFrameAsync(frame))
        {
            StatusText = "Sent air quality metrics.";
            _rxHost.RecordSelfTelemetry(new MeshTelemetry
            {
                Pm25Standard = aq.Pm25Standard,
                Pm100Standard = aq.Pm100Standard,
            });
        }
        else StatusText = "Transmit failed.";
    }

    // ----- Auto-report schedules -----

    [ObservableProperty] private bool _autoReportNodeInfoEnabled;
    [ObservableProperty] private int _autoReportNodeInfoSeconds = 3600;
    [ObservableProperty] private bool _autoReportPositionEnabled;
    [ObservableProperty] private int _autoReportPositionSeconds = 3600;
    [ObservableProperty] private bool _autoReportDeviceMetricsEnabled;
    [ObservableProperty] private int _autoReportDeviceMetricsSeconds = 3600;
    [ObservableProperty] private bool _autoReportEnvironmentMetricsEnabled;
    [ObservableProperty] private int _autoReportEnvironmentMetricsSeconds = 3600;
    [ObservableProperty] private bool _autoReportAirQualityMetricsEnabled;
    [ObservableProperty] private int _autoReportAirQualityMetricsSeconds = 3600;
    [ObservableProperty] private bool _autoReportNodeStatusEnabled;
    [ObservableProperty] private int _autoReportNodeStatusSeconds = 3600;

    [ObservableProperty]
    private string _autoReportLastSentSummary =
        "Auto last: NI never | POS never | MET never | ENV never | AQ never | ST never";

    /// <summary>Minimum interval. Guards against a typo'd 0 turning an auto
    /// report into a transmit-as-fast-as-possible loop.</summary>
    private const int MinAutoReportSeconds = 5;

    private DateTime _nextNodeInfoUtc = DateTime.MaxValue;
    private DateTime _nextPositionUtc = DateTime.MaxValue;
    private DateTime _nextDeviceMetricsUtc = DateTime.MaxValue;
    private DateTime _nextEnvironmentMetricsUtc = DateTime.MaxValue;
    private DateTime _nextAirQualityMetricsUtc = DateTime.MaxValue;
    private DateTime _nextNodeStatusUtc = DateTime.MaxValue;

    private DateTime? _lastNodeInfoUtc, _lastPositionUtc, _lastDeviceMetricsUtc;
    private DateTime? _lastEnvironmentMetricsUtc, _lastAirQualityMetricsUtc, _lastNodeStatusUtc;

    private int _autoReportTickInFlight;

    private static int Clamp(int seconds) => Math.Max(MinAutoReportSeconds, seconds);
    private static DateTime Next(bool enabled, int seconds) =>
        enabled ? DateTime.UtcNow.AddSeconds(Clamp(seconds)) : DateTime.MaxValue;

    partial void OnAutoReportNodeInfoEnabledChanged(bool value) { _nextNodeInfoUtc = Next(value, AutoReportNodeInfoSeconds); SaveSettings(); }
    partial void OnAutoReportPositionEnabledChanged(bool value) { _nextPositionUtc = Next(value, AutoReportPositionSeconds); SaveSettings(); }
    partial void OnAutoReportDeviceMetricsEnabledChanged(bool value) { _nextDeviceMetricsUtc = Next(value, AutoReportDeviceMetricsSeconds); SaveSettings(); }
    partial void OnAutoReportEnvironmentMetricsEnabledChanged(bool value) { _nextEnvironmentMetricsUtc = Next(value, AutoReportEnvironmentMetricsSeconds); SaveSettings(); }
    partial void OnAutoReportAirQualityMetricsEnabledChanged(bool value) { _nextAirQualityMetricsUtc = Next(value, AutoReportAirQualityMetricsSeconds); SaveSettings(); }
    partial void OnAutoReportNodeStatusEnabledChanged(bool value) { _nextNodeStatusUtc = Next(value, AutoReportNodeStatusSeconds); SaveSettings(); }

    partial void OnAutoReportNodeInfoSecondsChanged(int value) { _nextNodeInfoUtc = Next(AutoReportNodeInfoEnabled, value); SaveSettings(); }
    partial void OnAutoReportPositionSecondsChanged(int value) { _nextPositionUtc = Next(AutoReportPositionEnabled, value); SaveSettings(); }
    partial void OnAutoReportDeviceMetricsSecondsChanged(int value) { _nextDeviceMetricsUtc = Next(AutoReportDeviceMetricsEnabled, value); SaveSettings(); }
    partial void OnAutoReportEnvironmentMetricsSecondsChanged(int value) { _nextEnvironmentMetricsUtc = Next(AutoReportEnvironmentMetricsEnabled, value); SaveSettings(); }
    partial void OnAutoReportAirQualityMetricsSecondsChanged(int value) { _nextAirQualityMetricsUtc = Next(AutoReportAirQualityMetricsEnabled, value); SaveSettings(); }
    partial void OnAutoReportNodeStatusSecondsChanged(int value) { _nextNodeStatusUtc = Next(AutoReportNodeStatusEnabled, value); SaveSettings(); }

    private void LoadAutoReportSettings()
    {
        AutoReportNodeInfoEnabled = _settings.AutoReportNodeInfoEnabled;
        AutoReportNodeInfoSeconds = Clamp(_settings.AutoReportNodeInfoSeconds);
        AutoReportPositionEnabled = _settings.AutoReportPositionEnabled;
        AutoReportPositionSeconds = Clamp(_settings.AutoReportPositionSeconds);
        AutoReportDeviceMetricsEnabled = _settings.AutoReportDeviceMetricsEnabled;
        AutoReportDeviceMetricsSeconds = Clamp(_settings.AutoReportDeviceMetricsSeconds);
        AutoReportEnvironmentMetricsEnabled = _settings.AutoReportEnvironmentMetricsEnabled;
        AutoReportEnvironmentMetricsSeconds = Clamp(_settings.AutoReportEnvironmentMetricsSeconds);
        AutoReportAirQualityMetricsEnabled = _settings.AutoReportAirQualityMetricsEnabled;
        AutoReportAirQualityMetricsSeconds = Clamp(_settings.AutoReportAirQualityMetricsSeconds);
        AutoReportNodeStatusEnabled = _settings.AutoReportNodeStatusEnabled;
        AutoReportNodeStatusSeconds = Clamp(_settings.AutoReportNodeStatusSeconds);
    }

    /// <summary>
    /// Writes the schedules onto <paramref name="s"/>, which is not always the
    /// <c>_settings</c> field: SaveSettings persists a copy freshly loaded from
    /// the file, so anything written to the field instead of the target reaches
    /// memory and never the disk. Takes the target explicitly for that reason,
    /// like StoreNodeFilterSettings and SaveMqttSettings beside it.
    /// </summary>
    private void StoreAutoReportSettings(AppSettings s)
    {
        s.AutoReportNodeInfoEnabled = AutoReportNodeInfoEnabled;
        s.AutoReportNodeInfoSeconds = Clamp(AutoReportNodeInfoSeconds);
        s.AutoReportPositionEnabled = AutoReportPositionEnabled;
        s.AutoReportPositionSeconds = Clamp(AutoReportPositionSeconds);
        s.AutoReportDeviceMetricsEnabled = AutoReportDeviceMetricsEnabled;
        s.AutoReportDeviceMetricsSeconds = Clamp(AutoReportDeviceMetricsSeconds);
        s.AutoReportEnvironmentMetricsEnabled = AutoReportEnvironmentMetricsEnabled;
        s.AutoReportEnvironmentMetricsSeconds = Clamp(AutoReportEnvironmentMetricsSeconds);
        s.AutoReportAirQualityMetricsEnabled = AutoReportAirQualityMetricsEnabled;
        s.AutoReportAirQualityMetricsSeconds = Clamp(AutoReportAirQualityMetricsSeconds);
        s.AutoReportNodeStatusEnabled = AutoReportNodeStatusEnabled;
        s.AutoReportNodeStatusSeconds = Clamp(AutoReportNodeStatusSeconds);
    }

    private void UpdateAutoReportSummary()
    {
        static string T(DateTime? utc) => utc is DateTime d ? UiFormats.Time(d.ToLocalTime()) : "never";
        AutoReportLastSentSummary =
            $"Auto last: NI {T(_lastNodeInfoUtc)} | POS {T(_lastPositionUtc)} | MET {T(_lastDeviceMetricsUtc)} " +
            $"| ENV {T(_lastEnvironmentMetricsUtc)} | AQ {T(_lastAirQualityMetricsUtc)} | ST {T(_lastNodeStatusUtc)}";
    }

    /// <summary>Called from the poll loop. Runs at most one tick at a time —
    /// the sends await a transmit, which is far slower than the poll
    /// interval.</summary>
    private void KickAutoReportTick()
    {
        if (!CanTransmit) return;
        if (Interlocked.Exchange(ref _autoReportTickInFlight, 1) != 0) return;
        _ = RunAutoReportTickAsync();
    }

    private async Task RunAutoReportTickAsync()
    {
        try
        {
            // Each due check re-reads the clock because the send before it may
            // have taken a while (a weather fetch plus an over-the-air frame).
            if (AutoReportNodeInfoEnabled && DateTime.UtcNow >= _nextNodeInfoUtc)
            {
                _nextNodeInfoUtc = DateTime.UtcNow.AddSeconds(Clamp(AutoReportNodeInfoSeconds));
                await SendSelfNodeInfoCommand.ExecuteAsync(null);
                if (StatusText.StartsWith("Sent NodeInfo", StringComparison.OrdinalIgnoreCase))
                { _lastNodeInfoUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (AutoReportPositionEnabled && DateTime.UtcNow >= _nextPositionUtc)
            {
                _nextPositionUtc = DateTime.UtcNow.AddSeconds(Clamp(AutoReportPositionSeconds));
                await SendSelfPositionCommand.ExecuteAsync(null);
                if (StatusText.StartsWith("Sent position", StringComparison.OrdinalIgnoreCase))
                { _lastPositionUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (AutoReportDeviceMetricsEnabled && DateTime.UtcNow >= _nextDeviceMetricsUtc)
            {
                _nextDeviceMetricsUtc = DateTime.UtcNow.AddSeconds(Clamp(AutoReportDeviceMetricsSeconds));
                await SendSelfDeviceMetricsCommand.ExecuteAsync(null);
                if (StatusText.StartsWith("Sent device metrics", StringComparison.OrdinalIgnoreCase))
                { _lastDeviceMetricsUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (AutoReportEnvironmentMetricsEnabled && DateTime.UtcNow >= _nextEnvironmentMetricsUtc)
            {
                _nextEnvironmentMetricsUtc = DateTime.UtcNow.AddSeconds(Clamp(AutoReportEnvironmentMetricsSeconds));
                await SendSelfEnvironmentMetricsCommand.ExecuteAsync(null);
                if (StatusText.StartsWith("Sent environment metrics", StringComparison.OrdinalIgnoreCase))
                { _lastEnvironmentMetricsUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (AutoReportAirQualityMetricsEnabled && DateTime.UtcNow >= _nextAirQualityMetricsUtc)
            {
                _nextAirQualityMetricsUtc = DateTime.UtcNow.AddSeconds(Clamp(AutoReportAirQualityMetricsSeconds));
                await SendSelfAirQualityMetricsCommand.ExecuteAsync(null);
                if (StatusText.StartsWith("Sent air quality metrics", StringComparison.OrdinalIgnoreCase))
                { _lastAirQualityMetricsUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (AutoReportNodeStatusEnabled && DateTime.UtcNow >= _nextNodeStatusUtc)
            {
                _nextNodeStatusUtc = DateTime.UtcNow.AddSeconds(Clamp(AutoReportNodeStatusSeconds));
                await SendSelfNodeStatusCommand.ExecuteAsync(null);
                if (StatusText.StartsWith("Sent node status", StringComparison.OrdinalIgnoreCase))
                { _lastNodeStatusUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-report error: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _autoReportTickInFlight, 0);
        }
    }

    private void DisposeMyNode()
    {
        _gpsService.Dispose();
        _openMeteo.Dispose();
    }
}
