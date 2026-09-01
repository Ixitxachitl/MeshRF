// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Nodes;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Map-panel state: the marker projection the view renders, the "new waypoint"
/// composer bound to the map's Waypoint expander, and the map-side actions
/// (set home, send waypoint, pick a bounding box). Ported from MeshRF.App's
/// MainViewModel map region.
/// </summary>
public partial class RadioViewModel
{
    /// <summary>One drawable point on the map. Home, node and waypoint markers
    /// all flatten into this so the view has a single list to project.</summary>
    public sealed record MapMarker(
        double Lat, double Lon, string Label, string Title,
        bool IsHome, bool IsWaypoint = false, bool IsExpired = false,
        uint? NodeNum = null, long? WaypointRowId = null,
        uint GeofenceRadiusM = 0,
        double? BboxWest = null, double? BboxSouth = null,
        double? BboxEast = null, double? BboxNorth = null);

    /// <summary>Raised when anything the map draws changes (node positions,
    /// waypoints, home, the pending bounding box), so the view can re-render.</summary>
    public event EventHandler? MapDataChanged;

    public void RaiseMapDataChanged() => MapDataChanged?.Invoke(this, EventArgs.Empty);

    // ----- Marker projection -----

    /// <summary>Home (if set) plus every filtered node with a position plus
    /// every known waypoint, narrowed by <see cref="MapMarkerFilter"/>. Node
    /// markers honour the same filter as the node grid, so hiding a node there
    /// hides its marker too. Home is always drawn: it is the map's reference
    /// point, not one of the two marker kinds being filtered.</summary>
    public IReadOnlyList<MapMarker> GetMapMarkers()
    {
        var list = new List<MapMarker>();

        if (TryGetHomeLocation(out double hlat, out double hlon))
            list.Add(new MapMarker(hlat, hlon, GetLocationMarkerLabel(), "Location", IsHome: true));

        IEnumerable<NodeRecord> nodes = ShowNodesOnMap ? FilteredNodes : [];
        foreach (var n in nodes)
        {
            if (n.Latitude is not double lat || n.Longitude is not double lon) continue;
            list.Add(new MapMarker(lat, lon, GetMapNodeLabel(n), BuildNodeTooltip(n),
                                   IsHome: false, NodeNum: n.NodeNum));
        }

        IEnumerable<WaypointRecord> waypoints = ShowWaypointsOnMap ? Waypoints : [];
        foreach (var wp in waypoints)
        {
            list.Add(new MapMarker(
                wp.Latitude,
                wp.Longitude,
                string.IsNullOrWhiteSpace(wp.Name)
                    ? (string.IsNullOrWhiteSpace(wp.IconText) ? "Waypoint" : wp.IconText)
                    : $"{wp.IconText} {wp.Name}".Trim(),
                BuildWaypointTooltip(wp),
                IsHome: false,
                IsWaypoint: true,
                IsExpired: wp.IsExpired,
                WaypointRowId: wp.Id,
                GeofenceRadiusM: wp.GeofenceRadius,
                BboxWest: wp.BboxWest, BboxSouth: wp.BboxSouth,
                BboxEast: wp.BboxEast, BboxNorth: wp.BboxNorth));
        }

        return list;
    }

    private string GetLocationMarkerLabel() =>
        string.IsNullOrWhiteSpace(MyShortName)
            ? (string.IsNullOrWhiteSpace(MyLongName) ? "Location" : MyLongName)
            : MyShortName;

    // ----- Marker filter -----

    public IReadOnlyList<string> MapMarkerFilterOptions { get; } =
        ["Nodes and waypoints", "Nodes only", "Waypoints only"];

    public const string DefaultMapMarkerFilter = "Nodes and waypoints";

    [ObservableProperty]
    private string _mapMarkerFilter = DefaultMapMarkerFilter;

    private bool ShowNodesOnMap => MapMarkerFilter != "Waypoints only";
    private bool ShowWaypointsOnMap => MapMarkerFilter != "Nodes only";

    partial void OnMapMarkerFilterChanged(string value)
    {
        SaveSettings();
        RaiseMapDataChanged();
    }

    // ----- Map label mode -----

    public IReadOnlyList<string> MapNodeLabelModeOptions { get; } =
    [
        "Node Number", "Long Name", "Short Name",
        "Temperature", "Humidity", "Pressure", "Gas Resistance", "IAQ",
        "PM1.0 std", "PM2.5 std", "PM10 std",
        "PM1.0 env", "PM2.5 env", "PM10 env",
        "Ch1 Voltage", "Ch1 Current", "Ch2 Voltage", "Ch2 Current",
        "Ch3 Voltage", "Ch3 Current",
    ];

    /// <summary>Short name is the useful default — a node number label is just
    /// the ID already shown in the grid. Overridden from settings at startup.</summary>
    public const string DefaultMapNodeLabelMode = "Short Name";

    [ObservableProperty]
    private string _mapNodeLabelMode = DefaultMapNodeLabelMode;

    partial void OnMapNodeLabelModeChanged(string value)
    {
        SaveSettings();
        RaiseMapDataChanged();
    }

    private string GetMapNodeLabel(NodeRecord n) => MapNodeLabelMode switch
    {
        "Long Name" => !string.IsNullOrWhiteSpace(n.LongName) ? n.LongName : n.DisplayId,
        "Short Name" => !string.IsNullOrWhiteSpace(n.ShortName) ? n.ShortName : n.DisplayId,
        "Temperature" => n.TemperatureC is float t ? FormatTemperature(t) : n.DisplayId,
        "Humidity" => n.RelativeHumidityPct is float h ? $"{h:F0}%" : n.DisplayId,
        "Pressure" => n.BarometricPressureHpa is float p ? $"{p:0.0} hPa" : n.DisplayId,
        "Gas Resistance" => n.GasResistanceMohm is float g ? $"{g:0.0} MΩ" : n.DisplayId,
        "IAQ" => n.Iaq is int iaq ? $"IAQ {iaq}" : n.DisplayId,
        "PM1.0 std" => n.Pm10Standard is uint p10s ? $"{p10s} µg" : n.DisplayId,
        "PM2.5 std" => n.Pm25Standard is uint p25s ? $"{p25s} µg" : n.DisplayId,
        "PM10 std" => n.Pm100Standard is uint p100s ? $"{p100s} µg" : n.DisplayId,
        "PM1.0 env" => n.Pm10Environmental is uint p10e ? $"{p10e} µg" : n.DisplayId,
        "PM2.5 env" => n.Pm25Environmental is uint p25e ? $"{p25e} µg" : n.DisplayId,
        "PM10 env" => n.Pm100Environmental is uint p100e ? $"{p100e} µg" : n.DisplayId,
        "Ch1 Voltage" => n.Ch1VoltageV is float v1 ? $"{v1:0.00} V" : n.DisplayId,
        "Ch1 Current" => n.Ch1CurrentMa is float i1 ? $"{i1:0.0} mA" : n.DisplayId,
        "Ch2 Voltage" => n.Ch2VoltageV is float v2 ? $"{v2:0.00} V" : n.DisplayId,
        "Ch2 Current" => n.Ch2CurrentMa is float i2 ? $"{i2:0.0} mA" : n.DisplayId,
        "Ch3 Voltage" => n.Ch3VoltageV is float v3 ? $"{v3:0.00} V" : n.DisplayId,
        "Ch3 Current" => n.Ch3CurrentMa is float i3 ? $"{i3:0.0} mA" : n.DisplayId,
        _ => n.DisplayId,
    };

    internal string FormatTemperature(float celsius) =>
        CurrentUnitSystem == UnitSystem.Imperial
            ? $"{celsius * 9f / 5f + 32f:0.0}°F"
            : $"{celsius:0.0}°C";

    // ----- Tooltips -----

    private string BuildNodeTooltip(NodeRecord n)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(n.LongName) ? n.DisplayId : n.LongName);
        if (!string.IsNullOrWhiteSpace(n.ShortName)) sb.Append(" (").Append(n.ShortName).Append(')');
        sb.Append('\n').Append(n.DisplayId);
        if (n.Latitude is double lat && n.Longitude is double lon)
            sb.Append('\n').Append(lat.ToString("F5", CultureInfo.InvariantCulture))
              .Append(", ").Append(lon.ToString("F5", CultureInfo.InvariantCulture));
        if (n.AltitudeM is int alt)
            sb.Append("  ").Append(DisplayUnits.FormatAltitude(alt, CurrentUnitSystem));
        if (n.SnrDb is float snr) sb.Append("\nSNR ").Append(snr.ToString("0.0", CultureInfo.InvariantCulture)).Append(" dB");
        if (n.BatteryPct is byte batt) sb.Append("\nBattery ").Append(batt).Append('%');
        if (n.HopsAway is byte hops) sb.Append("\nHops ").Append(hops);
        if (n.LastHeardEpoch > 0)
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(n.LastHeardEpoch);
            sb.Append("\nHeard ").Append(FormatAge(age)).Append(" ago");
        }
        return sb.ToString();
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{Math.Max(0, (int)age.TotalSeconds)}s";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private string BuildWaypointTooltip(WaypointRecord wp)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(string.IsNullOrWhiteSpace(wp.IconText) ? wp.DisplayName : $"{wp.IconText} {wp.DisplayName}")
          .Append("\nFrom ").Append(_rxHost.NodeDisplayName(wp.FromNode))
          .Append('\n').Append(wp.Latitude.ToString("F5", CultureInfo.InvariantCulture))
          .Append(", ").Append(wp.Longitude.ToString("F5", CultureInfo.InvariantCulture));
        if (wp.AltitudeM is int alt)
            sb.Append("  ").Append(DisplayUnits.FormatAltitude(alt, CurrentUnitSystem));
        if (!string.IsNullOrWhiteSpace(wp.Description)) sb.Append('\n').Append(wp.Description);
        if (wp.LockedTo != 0)
            sb.Append("\nLocked to !").Append(wp.LockedTo.ToString("x8", CultureInfo.InvariantCulture));
        if (wp.GeofenceRadius > 0)
            sb.Append("\nGeofence: ")
              .Append(DisplayUnits.FormatShortDistance(wp.GeofenceRadius, CurrentUnitSystem))
              .Append(" radius");
        if (wp.BboxWest is double bw && wp.BboxSouth is double bs &&
            wp.BboxEast is double be && wp.BboxNorth is double bn)
            sb.Append("\nGeofence box: ")
              .Append(bs.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(bw.ToString("F4", CultureInfo.InvariantCulture)).Append(" to ")
              .Append(bn.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(be.ToString("F4", CultureInfo.InvariantCulture));
        if (wp.NotifyOnEnter || wp.NotifyOnExit)
        {
            sb.Append("\nNotify: ");
            if (wp.NotifyOnEnter) sb.Append("enter");
            if (wp.NotifyOnEnter && wp.NotifyOnExit) sb.Append('/');
            if (wp.NotifyOnExit) sb.Append("exit");
            if (wp.NotifyFavoritesOnly) sb.Append(" (favorites only)");
        }
        sb.Append('\n').Append(wp.ExpiryStatus);
        return sb.ToString();
    }

    // ----- Home location -----

    /// <summary>Ctrl+right-click on the map drops our home location here.</summary>
    /// <remarks>
    /// Refused while the USB GPS is the location source: there the receiver
    /// owns the position boxes, and its next fix — a second away — would
    /// overwrite anything dropped by hand. The identity window disables the
    /// typed boxes for the same reason; this is the same gate for the gesture.
    /// </remarks>
    public void SetHomeLocation(double lat, double lon)
    {
        if (IsUsbSerialLocationSource)
        {
            StatusText = "Location comes from the USB GPS. Set the location source to Manual to place it yourself.";
            return;
        }

        HomeLatitudeText = lat.ToString("F6", CultureInfo.InvariantCulture);
        HomeLongitudeText = lon.ToString("F6", CultureInfo.InvariantCulture);
        StatusText = $"Location set to {HomeLatitudeText}, {HomeLongitudeText}";
        RaiseMapDataChanged();
    }

    // ----- New-waypoint composer -----

    [ObservableProperty] private string _selectedWaypointEmoji = "📍";

    /// <summary>What the composer's icon button shows. An empty selection sends
    /// no <c>Waypoint.icon</c> at all, so the button needs a placeholder to stay
    /// findable.</summary>
    public string WaypointIconGlyph =>
        string.IsNullOrEmpty(SelectedWaypointEmoji) ? "＋" : SelectedWaypointEmoji;

    partial void OnSelectedWaypointEmojiChanged(string value) =>
        OnPropertyChanged(nameof(WaypointIconGlyph));

    [RelayCommand]
    private void ClearWaypointIcon() => SelectedWaypointEmoji = string.Empty;

    [ObservableProperty] private string _waypointNameInput = string.Empty;
    [ObservableProperty] private string _waypointDescriptionInput = string.Empty;
    [ObservableProperty] private bool _waypointLockToMe;

    [ObservableProperty] private bool _useWaypointExpiry;
    [ObservableProperty] private DateTimeOffset? _waypointExpiryDate = DateTimeOffset.Now.AddDays(1);
    [ObservableProperty] private TimeSpan? _waypointExpiryTime = new TimeSpan(12, 0, 0);

    [ObservableProperty] private bool _useWaypointGeofence;
    /// <summary>In the display units, not necessarily metres — see
    /// <see cref="WaypointGeofenceRadiusLabel"/> for which.</summary>
    [ObservableProperty] private string _waypointGeofenceRadiusInput = "100";

    /// <summary>Names the unit the radius box is read in, so the field can be
    /// typed in feet without the protobuf's metres leaking into the UI.</summary>
    public string WaypointGeofenceRadiusLabel =>
        $"Radius ({DisplayUnits.ShortDistanceUnitShort(CurrentUnitSystem)})";
    [ObservableProperty] private bool _waypointNotifyOnEnter;
    [ObservableProperty] private bool _waypointNotifyOnExit;
    [ObservableProperty] private bool _waypointNotifyFavoritesOnly;

    [ObservableProperty] private bool _useWaypointBoundingBox;
    [ObservableProperty] private bool _isPickingWaypointBoundingBox;

    public double? WaypointBboxWest { get; private set; }
    public double? WaypointBboxSouth { get; private set; }
    public double? WaypointBboxEast { get; private set; }
    public double? WaypointBboxNorth { get; private set; }

    /// <summary>Human-readable summary of the picked box, shown under the
    /// "Pick corners on map" toggle.</summary>
    public string WaypointBoundingBoxSummary =>
        WaypointBboxWest is double w && WaypointBboxSouth is double s &&
        WaypointBboxEast is double e && WaypointBboxNorth is double n
            ? $"SW {s.ToString("F4", CultureInfo.InvariantCulture)}, {w.ToString("F4", CultureInfo.InvariantCulture)}\n" +
              $"NE {n.ToString("F4", CultureInfo.InvariantCulture)}, {e.ToString("F4", CultureInfo.InvariantCulture)}"
            : "No box picked.";

    /// <summary>True once the composer has a geofence of either shape. The
    /// notify flags belong to the waypoint's geofence rather than to one shape
    /// of it — Waypoint.notify_on_enter/exit read on "the circular radius
    /// and/or the bounding box" — so a box on its own arms them just as a
    /// radius does.</summary>
    public bool WaypointHasGeofence => UseWaypointGeofence || UseWaypointBoundingBox;

    partial void OnIsPickingWaypointBoundingBoxChanged(bool value) => RaiseMapDataChanged();

    partial void OnUseWaypointBoundingBoxChanged(bool value)
    {
        OnPropertyChanged(nameof(WaypointHasGeofence));
        RaiseMapDataChanged();
    }

    partial void OnUseWaypointGeofenceChanged(bool value)
    {
        OnPropertyChanged(nameof(WaypointHasGeofence));
        RaiseMapDataChanged();
    }

    /// <summary>Completes a two-corner pick into a normalised west/south/east/
    /// north box and leaves picking mode.</summary>
    public void SetWaypointBoundingBox(double latA, double lonA, double latB, double lonB)
    {
        WaypointBboxWest = Math.Min(lonA, lonB);
        WaypointBboxEast = Math.Max(lonA, lonB);
        WaypointBboxSouth = Math.Min(latA, latB);
        WaypointBboxNorth = Math.Max(latA, latB);
        IsPickingWaypointBoundingBox = false;
        OnPropertyChanged(nameof(WaypointBoundingBoxSummary));
        RaiseMapDataChanged();
    }

    [RelayCommand]
    private void ClearWaypointBoundingBox()
    {
        WaypointBboxWest = null;
        WaypointBboxSouth = null;
        WaypointBboxEast = null;
        WaypointBboxNorth = null;
        IsPickingWaypointBoundingBox = false;
        OnPropertyChanged(nameof(WaypointBoundingBoxSummary));
        RaiseMapDataChanged();
    }

    /// <summary>Expiry as a unix epoch. Never-expires sends Int32.MaxValue like
    /// the official app rather than 0, which firmware reads as already-expired.</summary>
    private uint BuildWaypointExpiryEpoch()
    {
        if (!UseWaypointExpiry || WaypointExpiryDate is not DateTimeOffset date)
            return WaypointRecord.NeverExpiresEpoch;

        var time = WaypointExpiryTime ?? TimeSpan.Zero;
        var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local).Add(time);
        long epoch = new DateTimeOffset(local).ToUnixTimeSeconds();
        if (epoch <= 0) return WaypointRecord.NeverExpiresEpoch;
        return epoch > uint.MaxValue ? WaypointRecord.NeverExpiresEpoch : (uint)epoch;
    }

    private uint BuildWaypointGeofenceRadius()
    {
        if (!UseWaypointGeofence) return 0;
        // Typed in the display units; the protobuf field is always metres.
        return DisplayUnits.ParseShortDistanceInput(WaypointGeofenceRadiusInput, CurrentUnitSystem) ?? 0u;
    }

    private static uint? EmojiToCodePoint(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        try
        {
            int cp = char.ConvertToUtf32(emoji.Trim(), 0);
            return cp > 0 ? (uint)cp : null;
        }
        catch { return null; }
    }

    // ----- Sending a waypoint from the map -----

    /// <summary>Ctrl+left-click on the map builds a waypoint from the composer
    /// above, transmits it, and caches it locally. <paramref name="to"/> makes
    /// it a DM rather than a broadcast (it still rides the channel's PSK).</summary>
    public async Task SendWaypointFromMapAsync(double lat, double lon,
                                               ChannelConfig? channel = null,
                                               uint? to = null)
    {
        if (!CanTransmit || _rxHost.MyNodeNum == 0)
        {
            StatusText = "Set your node ID and a TX-capable device before sending waypoints.";
            return;
        }

        var selectedChannel = channel
            ?? _rxHost.FindChannelByName((SelectedTab as ChannelTabViewModel)?.Config.Name);
        if (selectedChannel is null)
        {
            StatusText = "No enabled channel to send waypoint on.";
            return;
        }

        try
        {
            uint packetId = NextPacketId();
            uint waypointId = packetId;
            string name = string.IsNullOrWhiteSpace(WaypointNameInput)
                ? $"Waypoint {DateTime.Now:HHmmss}"
                : WaypointNameInput.Trim();
            string description = WaypointDescriptionInput?.Trim() ?? string.Empty;
            uint? icon = EmojiToCodePoint(SelectedWaypointEmoji);
            uint expireEpoch = BuildWaypointExpiryEpoch();
            uint lockedTo = WaypointLockToMe ? _rxHost.MyNodeNum : 0;
            uint geofenceRadius = BuildWaypointGeofenceRadius();

            double? bboxWest = null, bboxSouth = null, bboxEast = null, bboxNorth = null;
            if (UseWaypointBoundingBox &&
                WaypointBboxWest is double bw && WaypointBboxSouth is double bs &&
                WaypointBboxEast is double be && WaypointBboxNorth is double bn)
            {
                bboxWest = bw; bboxSouth = bs; bboxEast = be; bboxNorth = bn;
            }

            // The notify flags only mean something alongside a geofence of
            // either shape, and favorites-only only narrows an alert that is
            // being raised at all. The official Android client normalises them
            // the same way before sending.
            bool hasGeofence = geofenceRadius > 0 || bboxWest is not null;
            bool notifyOnEnter = hasGeofence && WaypointNotifyOnEnter;
            bool notifyOnExit = hasGeofence && WaypointNotifyOnExit;
            bool notifyFavoritesOnly = (notifyOnEnter || notifyOnExit) && WaypointNotifyFavoritesOnly;

            var frame = MeshEncoder.EncodeWaypoint(
                selectedChannel, _rxHost.MyNodeNum, packetId, waypointId, lat, lon,
                name: name, description: description,
                expireEpoch: expireEpoch, lockedTo: lockedTo, icon: icon,
                geofenceRadiusM: geofenceRadius,
                bboxWest: bboxWest, bboxSouth: bboxSouth, bboxEast: bboxEast, bboxNorth: bboxNorth,
                notifyOnEnter: notifyOnEnter, notifyOnExit: notifyOnExit,
                notifyFavoritesOnly: notifyFavoritesOnly,
                to: to ?? 0xFFFFFFFFu,
                hopLimit: (byte)HopLimit,
                okToMqtt: OkToMqtt,
                xeddsaPrivateKey: MyXeddsa.PrivateKey, xeddsaPublicKey: MyXeddsa.PublicKey);

            if (!await TransmitFrameAsync(frame))
            {
                StatusText = "Transmit failed (device cannot transmit).";
                return;
            }

            var record = new WaypointRecord
            {
                FromNode = _rxHost.MyNodeNum,
                WaypointId = waypointId,
                PacketId = packetId,
                Channel = selectedChannel.Name,
                Name = name,
                Description = description,
                Icon = icon,
                Latitude = lat,
                Longitude = lon,
                ExpireEpoch = expireEpoch,
                LockedTo = lockedTo,
                RxEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                GeofenceRadius = geofenceRadius,
                BboxWest = bboxWest,
                BboxSouth = bboxSouth,
                BboxEast = bboxEast,
                BboxNorth = bboxNorth,
                NotifyOnEnter = notifyOnEnter,
                NotifyOnExit = notifyOnExit,
                NotifyFavoritesOnly = notifyFavoritesOnly,
            };
            _waypointStore.Upsert(record);
            Waypoints.Add(record);

            var destName = to is uint dest ? _rxHost.NodeDisplayName(dest) : selectedChannel.Name;
            StatusText = $"Sent waypoint ({frame.Length} B) {(to is not null ? "to" : "on")} {destName}";
            RaiseMapDataChanged();
        }
        catch (Exception ex)
        {
            StatusText = $"Waypoint error: {ex.Message}";
        }
    }
}
