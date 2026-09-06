// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
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

    /// <summary>
    /// Detached history view models, one per node, kept only as long as
    /// something still holds them.
    /// </summary>
    /// <remarks>
    /// Weakly, so this does not accumulate a full loaded history for every node
    /// whose track was ever glanced at. The open window is the strong reference
    /// — it is the DataContext — so an entry lives exactly as long as the
    /// window that needs it, and reopening after a close simply builds a new
    /// one and reloads.
    /// </remarks>
    private readonly Dictionary<uint, WeakReference<ConversationTabViewModel>> _detachedHistory = new();

    /// <summary>A conversation view model to render history against. Reuses the
    /// open DM tab when there is one so the window and the tab share state;
    /// otherwise a detached one, which is how history is shown for a node with
    /// no conversation — including our own.</summary>
    /// <remarks>
    /// The detached one is reused rather than rebuilt per call, because live
    /// updates are routed to it by node number: a fresh instance per open would
    /// leave the window bound to a view model nothing is feeding.
    /// </remarks>
    public ConversationTabViewModel HistoryConversationFor(uint nodeNum)
    {
        // A conversation tab is preferred for a new window, so the window and
        // the tab share one set of history. Any detached view model already
        // handed out is deliberately left registered: a window opened before
        // the tab existed is still on screen and still has to fill in.
        var existing = Tabs.OfType<ConversationTabViewModel>().FirstOrDefault(c => c.NodeNum == nodeNum);
        if (existing is not null) return existing;

        if (_detachedHistory.TryGetValue(nodeNum, out var slot) && slot.TryGetTarget(out var cached))
            return cached;

        var convo = new ConversationTabViewModel(
            nodeNum,
            nodeNum == _rxHost.MyNodeNum ? (MyLongName ?? "Me") : _rxHost.NodeDisplayName(nodeNum),
            _nodeStore,
            () => FormatTemperature,
            () => (Func<float, string>)(hpa => $"{hpa:0.0} hPa"),
            () => (Func<int, string>)(m => DisplayUnits.FormatAltitude(m, CurrentUnitSystem)));

        _detachedHistory[nodeNum] = new WeakReference<ConversationTabViewModel>(convo);
        return convo;
    }

    /// <summary>
    /// Every view model showing this node's history: the conversation tab if
    /// one is open, and the detached one if a history window is up without a
    /// tab. Both can exist, and neither need.
    /// </summary>
    private IEnumerable<ConversationTabViewModel> HistoryViewsFor(uint nodeNum)
    {
        var tab = Tabs.OfType<ConversationTabViewModel>().FirstOrDefault(c => c.NodeNum == nodeNum);
        if (tab is not null) yield return tab;

        if (_detachedHistory.TryGetValue(nodeNum, out var slot))
        {
            if (slot.TryGetTarget(out var detached))
            {
                if (!ReferenceEquals(detached, tab)) yield return detached;
            }
            else
            {
                // The window that held it has closed.
                _detachedHistory.Remove(nodeNum);
            }
        }
    }

    /// <summary>
    /// Routes a newly stored history row to whatever is displaying it, so an
    /// open history window fills in as packets arrive instead of showing
    /// whatever was there when it opened.
    /// </summary>
    /// <remarks>
    /// Marshalled to the UI thread: these arrive on the decode path, and the
    /// collections on the other end are bound to a grid, a graph and a map.
    /// </remarks>
    private void OnLocationHistoryRecorded(uint nodeNum, NodeLocationHistoryRecord record) =>
        OnUiThread(() => { foreach (var view in HistoryViewsFor(nodeNum)) view.AppendLocationRecord(record); });

    private void OnTelemetryHistoryRecorded(uint nodeNum, NodeTelemetryHistoryRecord record) =>
        OnUiThread(() => { foreach (var view in HistoryViewsFor(nodeNum)) view.AppendTelemetryRecord(record); });

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ----- USB serial GPS -----

    private readonly UsbSerialGpsService _gpsService = new();
    private readonly SmartPositionFilter _smartPosition = new();

    /// <summary>Drops the reference fix, so the next one through is taken as
    /// it stands. Called whenever the thresholds or the source change.</summary>
    private void ResetSmartPosition() => _smartPosition.Reset();

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
        // The datum turns on where the altitude came from, and this is
        // where that changes.
        OnPropertyChanged(nameof(AltitudeDatumSummary));

        var options = BuildGpsOptions();
        _gpsService.UpdateOptions(options);
        ResetSmartPosition();

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

    /// <summary>
    /// Takes a fix from the receiver, subject to the smart-position filter.
    /// </summary>
    /// <remarks>
    /// The status line reports every fix, taken or held: it is what says the
    /// receiver is alive, and a held one still carries how far it has drifted,
    /// which is what the thresholds have to be set against. Only a taken fix
    /// reaches the position boxes — writing those persists the settings file
    /// and re-renders the map, and the receiver publishes every second.
    /// </remarks>
    private void ApplyGpsFix(GpsFix fix)
    {
        bool take = true;
        string holdNote = string.Empty;

        if (GpsSmartPosition && IsUsbSerialLocationSource)
        {
            take = _smartPosition.ShouldTake(
                fix.Latitude, fix.Longitude, DateTime.UtcNow,
                GpsSmartPositionMinMoveMeters,
                TimeSpan.FromSeconds(Math.Max(0, GpsSmartPositionMinSeconds)),
                out double movedMeters);

            if (!take)
                holdNote = "  (holding, moved " +
                           DisplayUnits.FormatShortDistance(movedMeters, CurrentUnitSystem) + ")";
        }

        // Two lines, always: the port on the first and the reading on the
        // second. One long line re-wraps as the numbers change width, which
        // shoves every row under it up and down once a second.
        GpsStatus = $"USB GPS: {fix.PortName} @ {fix.BaudRate} baud\n" +
                    $"{fix.Latitude:F6}, {fix.Longitude:F6}" +
                    (fix.AltitudeM is int a
                        ? $"  alt {DisplayUnits.FormatAltitude(a, CurrentUnitSystem)}" : string.Empty) +
                    holdNote;

        if (!take || !IsUsbSerialLocationSource) return;

        HomeLatitudeText = fix.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        HomeLongitudeText = fix.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        if (fix.AltitudeM is int alt)
            HomeAltitudeText = DisplayUnits.FormatAltitudeInput(alt, CurrentUnitSystem);

        // Kept beside the altitude it belongs to. It describes where this fix
        // was taken, so it travels with the fix rather than being looked up.
        _geoidSeparationM = fix.GeoidSeparationM;
        OnPropertyChanged(nameof(AltitudeDatumSummary));
    }

    /// <summary>The geoid separation from the last fix, which is only ours to
    /// use while the altitude beside it is the one that came with it. Typing a
    /// height into the box replaces the reading and not the separation, so the
    /// manual source has none.</summary>
    private int? _geoidSeparationM;

    private int? GeoidSeparationM => IsUsbSerialLocationSource ? _geoidSeparationM : null;

    /// <summary>Whether the role asks for height above the ellipsoid. Nobody
    /// sets this by hand: the datum is a property of what a receiver of ours
    /// expects, which is what choosing a TAK role says.</summary>
    private bool RoleWantsHae => RoleCoercions.PositionAltitudeMsl == false;

    /// <summary>The altitude to put on the air and the field it belongs in.
    /// </summary>
    private (int? AltitudeM, bool IsMsl) AltitudeToSend() =>
        AltitudeDatum.ForTransmit(HomeAltitudeMeters, GeoidSeparationM, RoleWantsHae);

    /// <summary>What the dialog says beside "Altitude datum", since there is no
    /// longer a box to set it in. A role that asks for HAE and a receiver that
    /// will not say where the ellipsoid is are two different states, and the
    /// second one is worth seeing.</summary>
    public string AltitudeDatumSummary =>
        !RoleWantsHae ? "above mean sea level"
        : GeoidSeparationM is int separation
            ? $"above the ellipsoid (HAE), {separation:+#;-#;0} m from the fix"
            : "above mean sea level — no geoid separation to convert with";

    // ----- Weather / air quality sources -----

    private readonly OpenMeteoClient _openMeteo = new();

    [ObservableProperty] private string _weatherTelemetryStatus = "Weather telemetry: idle.";
    [ObservableProperty] private string _airQualityTelemetryStatus = "Air quality telemetry: idle.";

    /// <summary>
    /// Firmware <c>MeshService::handleFromRadio</c>: introduce ourselves to a
    /// node we have just heard but hold no NodeInfo for, and ask for its own.
    /// </summary>
    /// <remarks>
    /// The gates are firmware's, in its order. The router roles stay quiet
    /// because a backbone node hears everything and would answer every stranger
    /// on the mesh; the channel-utilisation gate keeps a busy channel from
    /// filling with introductions; and a node more than two hops beyond our own
    /// limit is one whose reply could never reach us anyway.
    /// </remarks>
    private void HandleUnknownNodeHeard(uint from, string? channelName, byte hopsAway, RxSource source)
    {
        if (!CanTransmit || from == 0) return;
        if (RelayPolicy.IsRouterish(MyRole)) return;
        if (!ChannelUtilAllowsPoliteTx()) return;
        if (!DutyCycleAllows(polite: true, out _)) return;
        if (hopsAway > HopLimit + 2) return;

        // On the list of the listener that heard them, on that listener's
        // settings: that is the mesh they are on.
        var channel = _rxHost.ChannelFor(source, channelName);
        if (channel is null) return;

        // Built and queued here rather than through SendNodeInfoOnChannelAsync,
        // which reports through StatusText: this runs on the decode thread, and
        // an unprompted introduction is not what the status line is for.
        var frame = MeshEncoder.EncodeNodeInfo(channel, _rxHost.MyNodeNum, NextPacketId(),
            MyLongName, MyShortName,
            hwModel: (uint)Math.Max(0, HardwareModels.Id(MyHwModel)), role: RoleEnumValue(MyRole),
            publicKey: TryParseKeyBase64(MyPublicKey),
            to: from, hopLimit: (byte)HopLimit,
            wantResponse: RoleDefaults.AllowsRequestingReplies(MyRole), okToMqtt: OkToMqtt,
            xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey,
            isLicensed: MyIsLicensed, isUnmessagable: EffectiveIsUnmessagable);
        TransmitBackground(frame, TargetForSource(source));
        LogFromAnyThread($"  heard new node {_rxHost.NodeDisplayName(from)}, sending our NodeInfo");
    }

    /// <summary>Answers a directed request from a peer. Without this our
    /// NodeInfo only ever leaves on the auto-report schedule or a manual
    /// click, so peers that ask for our name get nothing back.</summary>
    /// <param name="hopLimit">How far the answer may travel — the hops the
    /// request took plus a margin, worked out by the host from the request's
    /// own header. A script asking for one of these has no request behind it
    /// and passes the configured limit.</param>
    private void HandleAutoReplyRequest(PortNum port, uint to, string? channelName, byte hopLimit, RxSource source)
    {
        if (!CanTransmit || to == 0 || to == 0xFFFFFFFFu) return;
        // On the list of the listener that heard them, on that listener's
        // settings: that is the mesh they are on.
        var channel = _rxHost.ChannelFor(source, channelName);
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
                        to: to, hopLimit: hopLimit, wantResponse: false, okToMqtt: OkToMqtt,
                        isLicensed: MyIsLicensed, isUnmessagable: EffectiveIsUnmessagable);
                    TransmitBackground(nodeInfo, TargetForSource(source));
                    break;

                case PortNum.Position:
                    if (channel.EffectivePositionPrecision == 0) return;
                    if (!TryGetHomeLocation(out double lat, out double lon)) return;
                    var (alt, altIsMsl) = AltitudeToSend();
                    var position = MeshEncoder.EncodePosition(channel, _rxHost.MyNodeNum, NextPacketId(), lat, lon,
                        altitudeM: alt, altitudeIsMsl: altIsMsl,
                        precisionBits: channel.EffectivePositionPrecision,
                        to: to, hopLimit: hopLimit, okToMqtt: OkToMqtt);
                    TransmitBackground(position, TargetForSource(source));
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
    private async void HandleTelemetryReplyRequest(
        uint to, string? channelName, TelemetryVariants wanted, byte hopLimit, RxSource source)
    {
        void Send(byte[] frame) => TransmitBackground(frame, TargetForSource(source));
        if (!CanTransmit || to == 0 || to == 0xFFFFFFFFu) return;
        // On the list of the listener that heard them, on that listener's
        // settings: that is the mesh they are on.
        var channel = _rxHost.ChannelFor(source, channelName);
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
                    Send(MeshEncoder.EncodeTelemetryEnvironmentMetrics(
                        channel, _rxHost.MyNodeNum, NextPacketId(),
                        temperatureC: weather.TemperatureC,
                        relativeHumidityPct: weather.RelativeHumidityPct,
                        barometricPressureHpa: weather.BarometricPressureHpa,
                        to: to, hopLimit: hopLimit, okToMqtt: OkToMqtt));
                }

                if (wanted.HasFlag(TelemetryVariants.AirQuality) &&
                    await _openMeteo.GetAirQualityAsync(lat, lon) is { } aq)
                {
                    Send(MeshEncoder.EncodeTelemetryAirQualityMetrics(
                        channel, _rxHost.MyNodeNum, NextPacketId(),
                        pm25Standard: aq.Pm25Standard, pm100Standard: aq.Pm100Standard,
                        to: to, hopLimit: hopLimit, okToMqtt: OkToMqtt));
                }
                return;
            }

            Send(MeshEncoder.EncodeTelemetryDeviceMetrics(
                channel, _rxHost.MyNodeNum, NextPacketId(),
                batteryLevel: 101, // 101 = mains-powered, the sentinel this app reports.
                to: to, hopLimit: hopLimit, okToMqtt: OkToMqtt));
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
        StatusText = await TransmitFrameAsync(frame, TargetForChannel(channel, to ?? 0xFFFFFFFFu))
            ? "Sent node status." : "Transmit failed.";
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
        if (await TransmitFrameAsync(frame, TargetForChannel(channel, to ?? 0xFFFFFFFFu)))
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
        if (await TransmitFrameAsync(frame, TargetForChannel(channel, to ?? 0xFFFFFFFFu)))
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

    /// <summary>Firmware's <c>position_broadcast_smart_enabled</c>: send a
    /// position early when we have moved, without disturbing the interval.</summary>
    [ObservableProperty] private bool _autoReportPositionSmartEnabled = true;

    /// <summary><c>broadcast_smart_minimum_distance</c>, in the display units.</summary>
    [ObservableProperty] private string _autoReportPositionSmartMinMoveInput = "100";

    /// <summary><c>broadcast_smart_minimum_interval_secs</c>.</summary>
    [ObservableProperty] private int _autoReportPositionSmartMinSeconds = 300;

    public string AutoReportPositionSmartMinMoveLabel =>
        $"Min move ({DisplayUnits.ShortDistanceUnitShort(CurrentUnitSystem)})";

    internal uint AutoReportPositionSmartMinMoveMeters =>
        DisplayUnits.ParseShortDistanceInput(AutoReportPositionSmartMinMoveInput, CurrentUnitSystem) ?? 0u;
    [ObservableProperty] private bool _autoReportDeviceMetricsEnabled;
    [ObservableProperty] private int _autoReportDeviceMetricsSeconds = 3600;
    [ObservableProperty] private bool _autoReportEnvironmentMetricsEnabled;
    [ObservableProperty] private int _autoReportEnvironmentMetricsSeconds = 3600;
    [ObservableProperty] private bool _autoReportAirQualityMetricsEnabled;
    [ObservableProperty] private int _autoReportAirQualityMetricsSeconds = 3600;
    [ObservableProperty] private bool _autoReportNodeStatusEnabled;
    [ObservableProperty] private int _autoReportNodeStatusSeconds = 3600;

    /// <summary>The channel each report is broadcast on, by name.</summary>
    /// <remarks>
    /// One channel per report rather than one for all of them: which mesh a
    /// report belongs on differs by what it says. A name that matches no
    /// channel - one renamed or deleted since - falls back to the primary at
    /// send time rather than the report going quiet, and
    /// <see cref="RefreshAutoReportChannelOptions"/> puts the picker back on
    /// something real.
    /// </remarks>
    [ObservableProperty] private string _autoReportNodeInfoChannel = string.Empty;
    [ObservableProperty] private string _autoReportPositionChannel = string.Empty;
    [ObservableProperty] private string _autoReportDeviceMetricsChannel = string.Empty;
    [ObservableProperty] private string _autoReportEnvironmentMetricsChannel = string.Empty;
    [ObservableProperty] private string _autoReportAirQualityMetricsChannel = string.Empty;
    [ObservableProperty] private string _autoReportNodeStatusChannel = string.Empty;

    /// <summary>Names the pickers offer: every channel that can carry a
    /// broadcast, in tab order.</summary>
    public ObservableCollection<string> AutoReportChannelOptions { get; } = new();

    /// <summary>Rebuilds the offered names and moves any picker whose channel
    /// has gone onto the primary, so what the dialog shows is where a report
    /// would actually go.</summary>
    public void RefreshAutoReportChannelOptions()
    {
        var names = Tabs.OfType<ChannelTabViewModel>()
                                .Where(t => !t.Config.IsDisabled)
                                .Select(t => t.Config.Name)
                                .ToList();

        // No channels yet (this can be asked before they are loaded) means
        // nothing to offer and nothing to coerce against — leaving the saved
        // names alone rather than resolving every one of them to "".
        if (names.Count == 0) return;

        if (!names.SequenceEqual(AutoReportChannelOptions))
        {
            AutoReportChannelOptions.Clear();
            foreach (var name in names) AutoReportChannelOptions.Add(name);
        }

        string fallback = PrimaryChannel()?.Name ?? names.FirstOrDefault() ?? string.Empty;
        string Offered(string chosen) => names.Contains(chosen) ? chosen : fallback;

        AutoReportNodeInfoChannel = Offered(AutoReportNodeInfoChannel);
        AutoReportPositionChannel = Offered(AutoReportPositionChannel);
        AutoReportDeviceMetricsChannel = Offered(AutoReportDeviceMetricsChannel);
        AutoReportEnvironmentMetricsChannel = Offered(AutoReportEnvironmentMetricsChannel);
        AutoReportAirQualityMetricsChannel = Offered(AutoReportAirQualityMetricsChannel);
        AutoReportNodeStatusChannel = Offered(AutoReportNodeStatusChannel);
    }

    /// <summary>The channel a report goes out on. An unknown name resolves to
    /// the primary, which is where every report went before they could be
    /// sent separately.</summary>
    private ChannelConfig? AutoReportChannel(string name) =>
        _rxHost.FindChannelByName(name) ?? PrimaryChannel();

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

    /// <summary>
    /// The configured interval after the default-channel floor and firmware's
    /// congestion scaling, in that order — firmware raises the configured value
    /// to the minimum first, then scales what it gets.
    /// </summary>
    /// <remarks>
    /// Applied when a report is rescheduled rather than when it is configured,
    /// so a mesh that grows during the day stretches our cadence, and leaving
    /// the default channel restores our own, without the user's setting ever
    /// being rewritten.
    /// </remarks>
    private int ScaledInterval(int effectiveSeconds) =>
        BroadcastIntervals.ScaledSeconds(
            Clamp(effectiveSeconds), MyRole, OnlineNodeCount,
            SelectedPreset, ChannelPlan.IsWideLora(SelectedRegion));

    /// <summary>
    /// Whether the radio is sitting on the frequency the region's default slot
    /// resolves to — firmware's <c>uses_default_frequency_slot</c> and
    /// <c>!override_frequency</c> together. A hand-tuned frequency reaches
    /// nobody else's default channel, whatever ours is called.
    /// </summary>
    private bool OnDefaultFrequencySlot
    {
        get
        {
            double defaultMHz = ChannelPlan.FrequencyMHz(
                SelectedRegion, SelectedPreset,
                ChannelPlan.DefaultSlot(SelectedRegion, SelectedPreset, PrimaryChannelName()));
            return Math.Abs(CenterFreqMHz - defaultMHz) < 0.0005;
        }
    }

    /// <summary>
    /// Whether a report addressed to <paramref name="channelName"/> would land
    /// on the channel the neighbourhood shares. Every default-channel floor
    /// turns on this one question.
    /// </summary>
    /// <remarks>
    /// Firmware asks it two ways, neither of which fits us. Its telemetry gate
    /// (<c>hasDefaultChannel</c>) is about the node: a default channel anywhere
    /// in the list quiets every report, wherever it goes. Its position gate
    /// tests one channel, but firmware picks that channel itself. MeshRF gives
    /// each auto report a picker, so the report says where it is going and we
    /// ask about that channel — a report on a private channel is nobody else's
    /// airtime, whatever else the list holds.
    ///
    /// The frequency slot is part of it, as it is in firmware's telemetry gate
    /// but not its position one: on a hand-tuned frequency no other radio is
    /// listening, so there is no shared channel to be quiet on.
    /// </remarks>
    private bool ReportsToDefaultChannel(string channelName) =>
        OnDefaultFrequencySlot
        && AutoReportChannel(channelName) is { } channel
        && DefaultChannelMinimums.IsDefaultChannel(channel, SelectedPreset, !IsCustomLoraParams);

    /// <summary>Firmware's telemetry floor, in force only for a report actually
    /// addressed to a default channel.</summary>
    private int TelemetryFloorSeconds(string channelName) =>
        ReportsToDefaultChannel(channelName) ? DefaultChannelMinimums.TelemetrySeconds(MyRole) : 0;

    /// <summary>Whether the auto position report is addressed to a default
    /// channel — the one thing both position floors turn on. Sharing switched
    /// off on that channel means no position leaves at all, so there is nothing
    /// to hold down.</summary>
    private bool PositionGoesOutOnDefaultChannel =>
        OnDefaultFrequencySlot
        && DefaultChannelMinimums.PositionUsesDefaultChannel(
               AutoReportChannel(AutoReportPositionChannel), SelectedPreset, !IsCustomLoraParams);

    /// <summary>Firmware's position floor, which follows the channel our
    /// positions would actually go out on rather than the channel list.</summary>
    private int PositionFloorSeconds =>
        PositionGoesOutOnDefaultChannel ? DefaultChannelMinimums.PositionSeconds(MyRole) : 0;

    /// <summary>The five-minute smart-broadcast gap a default channel imposes.
    /// This is what holds a TAK_TRACKER's 15-second role default in check when
    /// it is beaconing on the channel everyone shares.</summary>
    private int SmartPositionFloorSeconds =>
        PositionGoesOutOnDefaultChannel ? DefaultChannelMinimums.SmartPositionSeconds : 0;

    /// <summary>Nodes heard inside firmware's two-hour online window, which is
    /// what its congestion coefficient counts.</summary>
    private int OnlineNodeCount
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(BroadcastIntervals.OnlineWindow).ToUnixTimeSeconds();
            int n = 0;
            foreach (var node in _nodeStore.All())
                if (node.LastHeardEpoch >= cutoff) n++;
            return n;
        }
    }

    /// <summary>The scaled reschedule, and the note that explains a stretched
    /// interval before it looks like a bug.</summary>
    /// <summary>Reschedules a report, congestion-scaling the interval it is
    /// already resolved to. The role and the default-channel minimums were
    /// applied upstream — see RadioViewModel.EffectiveSettings.</summary>
    private DateTime NextScheduled(int effectiveSeconds, string label)
    {
        int scaled = ScaledInterval(effectiveSeconds);
        int configured = Clamp(effectiveSeconds);
        // Reached from a thread-pool continuation in the auto-report tick, and
        // Log mutates a collection bound to a ListBox.
        if (scaled > configured)
            LogFromAnyThread($"  {label} interval scaled {configured}s -> {scaled}s ({OnlineNodeCount} nodes online)");
        return DateTime.UtcNow.AddSeconds(scaled);
    }

    partial void OnAutoReportNodeInfoEnabledChanged(bool value) { _nextNodeInfoUtc = Next(EffectiveNodeInfoEnabled, EffectiveNodeInfoSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportPositionEnabledChanged(bool value) { _nextPositionUtc = Next(EffectivePositionEnabled, EffectivePositionSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportDeviceMetricsEnabledChanged(bool value) { _nextDeviceMetricsUtc = Next(EffectiveDeviceMetricsEnabled, EffectiveDeviceMetricsSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportEnvironmentMetricsEnabledChanged(bool value) { _nextEnvironmentMetricsUtc = Next(EffectiveEnvironmentMetricsEnabled, EffectiveEnvironmentMetricsSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportAirQualityMetricsEnabledChanged(bool value) { _nextAirQualityMetricsUtc = Next(EffectiveAirQualityMetricsEnabled, EffectiveAirQualityMetricsSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportNodeStatusEnabledChanged(bool value) { _nextNodeStatusUtc = Next(EffectiveNodeStatusEnabled, AutoReportNodeStatusSeconds); SaveSettings(); RefreshEffectiveSettings(); }

    partial void OnAutoReportNodeInfoSecondsChanged(int value) { _nextNodeInfoUtc = Next(EffectiveNodeInfoEnabled, EffectiveNodeInfoSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportPositionSecondsChanged(int value) { _nextPositionUtc = Next(EffectivePositionEnabled, EffectivePositionSeconds); SaveSettings(); RefreshEffectiveSettings(); }

    partial void OnAutoReportPositionSmartEnabledChanged(bool value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportPositionSmartMinMoveInputChanged(string value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportPositionSmartMinSecondsChanged(int value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportDeviceMetricsSecondsChanged(int value) { _nextDeviceMetricsUtc = Next(EffectiveDeviceMetricsEnabled, EffectiveDeviceMetricsSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportEnvironmentMetricsSecondsChanged(int value) { _nextEnvironmentMetricsUtc = Next(EffectiveEnvironmentMetricsEnabled, EffectiveEnvironmentMetricsSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportAirQualityMetricsSecondsChanged(int value) { _nextAirQualityMetricsUtc = Next(EffectiveAirQualityMetricsEnabled, EffectiveAirQualityMetricsSeconds); SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportNodeStatusSecondsChanged(int value) { _nextNodeStatusUtc = Next(EffectiveNodeStatusEnabled, AutoReportNodeStatusSeconds); SaveSettings(); RefreshEffectiveSettings(); }

    // Where the next report goes, not when, so these are only written down.
    partial void OnAutoReportNodeInfoChannelChanged(string value) => SaveSettings();
    // The one whose channel changes more than where it goes: what the
    // channel allows decides whether a position is sent at all, and how
    // precise it may be.
    partial void OnAutoReportPositionChannelChanged(string value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportDeviceMetricsChannelChanged(string value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportEnvironmentMetricsChannelChanged(string value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportAirQualityMetricsChannelChanged(string value) { SaveSettings(); RefreshEffectiveSettings(); }
    partial void OnAutoReportNodeStatusChannelChanged(string value) => SaveSettings();

    /// <summary>
    /// Re-arms every schedule against the intervals now in force. Called when
    /// the role changes: a pending timer armed for the old interval would
    /// otherwise fire once on the old cadence before settling to the new one,
    /// which for a Router picking up a 12-hour telemetry interval means one
    /// beacon it should not have sent.
    /// </summary>
    private void RearmAutoReportSchedules()
    {
        if (!_settingsLoaded) return;
        _nextNodeInfoUtc = Next(EffectiveNodeInfoEnabled, EffectiveNodeInfoSeconds);
        _nextPositionUtc = Next(EffectivePositionEnabled, EffectivePositionSeconds);
        _nextDeviceMetricsUtc = Next(EffectiveDeviceMetricsEnabled, EffectiveDeviceMetricsSeconds);
        _nextEnvironmentMetricsUtc = Next(EffectiveEnvironmentMetricsEnabled, EffectiveEnvironmentMetricsSeconds);
        _nextAirQualityMetricsUtc = Next(EffectiveAirQualityMetricsEnabled, EffectiveAirQualityMetricsSeconds);
        _nextNodeStatusUtc = Next(EffectiveNodeStatusEnabled, AutoReportNodeStatusSeconds);
    }

    private void LoadAutoReportSettings()
    {
        AutoReportNodeInfoEnabled = _settings.AutoReportNodeInfoEnabled;
        AutoReportNodeInfoSeconds = Clamp(_settings.AutoReportNodeInfoSeconds);
        AutoReportPositionEnabled = _settings.AutoReportPositionEnabled;
        AutoReportPositionSeconds = Clamp(_settings.AutoReportPositionSeconds);
        AutoReportPositionSmartEnabled = _settings.AutoReportPositionSmartEnabled;
        // Written in the units in force right now rather than in metres for
        // OnUnitSystemNameChanged to convert: this load runs after the saved
        // unit system has been applied, so there is no later conversion to
        // ride on and a stored 100 m would sit under an "(ft)" label.
        AutoReportPositionSmartMinMoveInput = DisplayUnits.FormatShortDistanceInput(
            _settings.AutoReportPositionSmartMinMoveMeters, CurrentUnitSystem);
        AutoReportPositionSmartMinSeconds = Math.Max(0, _settings.AutoReportPositionSmartMinSeconds);
        AutoReportDeviceMetricsEnabled = _settings.AutoReportDeviceMetricsEnabled;
        AutoReportDeviceMetricsSeconds = Clamp(_settings.AutoReportDeviceMetricsSeconds);
        AutoReportEnvironmentMetricsEnabled = _settings.AutoReportEnvironmentMetricsEnabled;
        AutoReportEnvironmentMetricsSeconds = Clamp(_settings.AutoReportEnvironmentMetricsSeconds);
        AutoReportAirQualityMetricsEnabled = _settings.AutoReportAirQualityMetricsEnabled;
        AutoReportAirQualityMetricsSeconds = Clamp(_settings.AutoReportAirQualityMetricsSeconds);
        AutoReportNodeStatusEnabled = _settings.AutoReportNodeStatusEnabled;
        AutoReportNodeStatusSeconds = Clamp(_settings.AutoReportNodeStatusSeconds);

        AutoReportNodeInfoChannel = _settings.AutoReportNodeInfoChannel;
        AutoReportPositionChannel = _settings.AutoReportPositionChannel;
        AutoReportDeviceMetricsChannel = _settings.AutoReportDeviceMetricsChannel;
        AutoReportEnvironmentMetricsChannel = _settings.AutoReportEnvironmentMetricsChannel;
        AutoReportAirQualityMetricsChannel = _settings.AutoReportAirQualityMetricsChannel;
        AutoReportNodeStatusChannel = _settings.AutoReportNodeStatusChannel;
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
        s.AutoReportPositionSmartEnabled = AutoReportPositionSmartEnabled;
        s.AutoReportPositionSmartMinMoveMeters = AutoReportPositionSmartMinMoveMeters;
        s.AutoReportPositionSmartMinSeconds = Math.Max(0, AutoReportPositionSmartMinSeconds);
        s.AutoReportDeviceMetricsEnabled = AutoReportDeviceMetricsEnabled;
        s.AutoReportDeviceMetricsSeconds = Clamp(AutoReportDeviceMetricsSeconds);
        s.AutoReportEnvironmentMetricsEnabled = AutoReportEnvironmentMetricsEnabled;
        s.AutoReportEnvironmentMetricsSeconds = Clamp(AutoReportEnvironmentMetricsSeconds);
        s.AutoReportAirQualityMetricsEnabled = AutoReportAirQualityMetricsEnabled;
        s.AutoReportAirQualityMetricsSeconds = Clamp(AutoReportAirQualityMetricsSeconds);
        s.AutoReportNodeStatusEnabled = AutoReportNodeStatusEnabled;
        s.AutoReportNodeStatusSeconds = Clamp(AutoReportNodeStatusSeconds);

        s.AutoReportNodeInfoChannel = AutoReportNodeInfoChannel;
        s.AutoReportPositionChannel = AutoReportPositionChannel;
        s.AutoReportDeviceMetricsChannel = AutoReportDeviceMetricsChannel;
        s.AutoReportEnvironmentMetricsChannel = AutoReportEnvironmentMetricsChannel;
        s.AutoReportAirQualityMetricsChannel = AutoReportAirQualityMetricsChannel;
        s.AutoReportNodeStatusChannel = AutoReportNodeStatusChannel;
    }

    /// <summary>Tracks the position we last put on the air, so movement can be
    /// judged against what the mesh actually believes rather than against the
    /// last fix the GPS happened to produce.</summary>
    private readonly SmartPositionFilter _positionBroadcast = new();

    /// <summary>Called after every position transmit, scheduled or not.</summary>
    private void MarkPositionBroadcast(double latitude, double longitude) =>
        _positionBroadcast.Mark(latitude, longitude, DateTime.UtcNow);

    /// <summary>
    /// Firmware's <c>position_broadcast_smart_enabled</c>: whether we have
    /// moved far enough, long enough after the last send, to put a position out
    /// ahead of the schedule.
    /// </summary>
    /// <remarks>
    /// Distance is measured between the coordinates as they would be
    /// transmitted, not as they were measured — a channel that fuzzes position
    /// to a few hundred metres puts every smaller move in the same cell, and
    /// re-sending an identical pair of numbers is airtime for nothing. That is
    /// what firmware compares too (computeImpreciseLatLon before the distance).
    ///
    /// With nothing sent yet there is no reference to have moved from, and the
    /// interval owns the first send.
    /// </remarks>
    private bool SmartPositionBroadcastDue()
    {
        if (!EffectivePositionSmartEnabled || !_positionBroadcast.HasReference) return false;
        if (!TryGetHomeLocation(out double lat, out double lon)) return false;

        // The channel the report is addressed to, not the primary: precision is
        // per channel, and measuring the move at another channel's precision
        // would answer for a report we are not about to send.
        var channel = AutoReportChannel(AutoReportPositionChannel);
        if (channel is null || channel.EffectivePositionPrecision == 0) return false;

        var (sendLat, sendLon) = MeshEncoder.ApplyPositionPrecision(lat, lon, channel.EffectivePositionPrecision);
        return _positionBroadcast.WouldTake(
            sendLat, sendLon, DateTime.UtcNow,
            EffectivePositionSmartMinMoveMeters,
            TimeSpan.FromSeconds(EffectivePositionSmartMinSeconds),
            out _);
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
        // Firmware's modules each check isTxAllowedAirUtil() before building a
        // beacon: scheduled reports are background traffic and get only half the
        // duty-cycle budget, so a user-initiated message still fits. The clocks
        // keep running, so a report skipped here goes out on the next tick that
        // has room rather than being lost.
        if (!DutyCycleAllows(polite: true, out _)) return;
        if (Interlocked.Exchange(ref _autoReportTickInFlight, 1) != 0) return;
        _ = RunAutoReportTickAsync();
    }

    private async Task RunAutoReportTickAsync()
    {
        try
        {
            // Each due check re-reads the clock because the send before it may
            // have taken a while (a weather fetch plus an over-the-air frame).
            if (EffectiveNodeInfoEnabled && DateTime.UtcNow >= _nextNodeInfoUtc)
            {
                _nextNodeInfoUtc = NextScheduled(EffectiveNodeInfoSeconds, "nodeinfo");
                await SendNodeInfoOnChannelAsync(AutoReportChannel(AutoReportNodeInfoChannel), null);
                if (StatusText.StartsWith("Sent NodeInfo", StringComparison.OrdinalIgnoreCase))
                { _lastNodeInfoUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            // Two ways a position goes out: the interval is up, or smart
            // broadcast says we have moved far enough since the last one to be
            // worth an early send. An early send restarts the interval, as it
            // does in firmware — the point is a fresh position on the mesh, and
            // one that just went out is fresh however it was triggered.
            if (EffectivePositionEnabled &&
                (DateTime.UtcNow >= _nextPositionUtc || SmartPositionBroadcastDue()))
            {
                _nextPositionUtc = NextScheduled(EffectivePositionSeconds, "position");
                await SendPositionOnChannelAsync(AutoReportChannel(AutoReportPositionChannel), null);
                if (StatusText.StartsWith("Sent position", StringComparison.OrdinalIgnoreCase))
                { _lastPositionUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (EffectiveDeviceMetricsEnabled && DateTime.UtcNow >= _nextDeviceMetricsUtc)
            {
                _nextDeviceMetricsUtc = NextScheduled(EffectiveDeviceMetricsSeconds, "device metrics");
                await SendDeviceMetricsOnChannelAsync(AutoReportChannel(AutoReportDeviceMetricsChannel), null);
                if (StatusText.StartsWith("Sent device metrics", StringComparison.OrdinalIgnoreCase))
                { _lastDeviceMetricsUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (EffectiveEnvironmentMetricsEnabled && DateTime.UtcNow >= _nextEnvironmentMetricsUtc)
            {
                _nextEnvironmentMetricsUtc = NextScheduled(EffectiveEnvironmentMetricsSeconds, "environment metrics");
                await SendEnvironmentMetricsOnChannelAsync(AutoReportChannel(AutoReportEnvironmentMetricsChannel), null);
                if (StatusText.StartsWith("Sent environment metrics", StringComparison.OrdinalIgnoreCase))
                { _lastEnvironmentMetricsUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (EffectiveAirQualityMetricsEnabled && DateTime.UtcNow >= _nextAirQualityMetricsUtc)
            {
                _nextAirQualityMetricsUtc = NextScheduled(EffectiveAirQualityMetricsSeconds, "air quality metrics");
                await SendAirQualityMetricsOnChannelAsync(AutoReportChannel(AutoReportAirQualityMetricsChannel), null);
                if (StatusText.StartsWith("Sent air quality metrics", StringComparison.OrdinalIgnoreCase))
                { _lastAirQualityMetricsUtc = DateTime.UtcNow; UpdateAutoReportSummary(); }
            }

            if (EffectiveNodeStatusEnabled && DateTime.UtcNow >= _nextNodeStatusUtc)
            {
                _nextNodeStatusUtc = NextScheduled(AutoReportNodeStatusSeconds, "node status");
                await SendNodeStatusOnChannelAsync(AutoReportChannel(AutoReportNodeStatusChannel), null);
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
