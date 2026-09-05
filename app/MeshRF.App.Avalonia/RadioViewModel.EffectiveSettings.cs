// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Mesh;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// What our broadcast settings actually resolve to, once the role's coercions
/// and the various minimums are applied on top of what the user configured.
/// </summary>
/// <remarks>
/// The settings the user types are never overwritten. Firmware rewrites its
/// config when a role is installed, which means picking ROUTER for an afternoon
/// silently destroys the intervals you had and leaves you to remember them; here
/// the role is an overlay, so changing back restores what you set without you
/// having to know what it was.
///
/// The cost of that is a settings box that no longer says what goes on the air,
/// so each overridden control carries a note beside it saying what actually
/// applies and why. Every transmit path reads the Effective* values — a caller
/// that reads the raw property is reporting the user's preference, not our
/// behaviour.
/// </remarks>
public partial class RadioViewModel
{
    /// <summary>The coercions the current role applies.</summary>
    private RoleDefaults RoleCoercions => RoleDefaults.For(MyRole);

    /// <summary>The resolution itself is pure and lives in Core — see
    /// <see cref="SettingOverlay"/>. These wrap it with our role name.</summary>
    private ResolvedSetting<int> ResolveInterval(int userSeconds, int? roleSeconds, int floorSeconds, string floorReason) =>
        SettingOverlay.Interval(Clamp(userSeconds), roleSeconds, MyRole, floorSeconds, floorReason);

    private ResolvedSetting<bool> ResolveFlag(bool userValue, bool? roleValue) =>
        SettingOverlay.Flag(userValue, roleValue, MyRole);

    /// <summary>Firmware's unconditional hourly floor on NodeInfo.</summary>
    private const string NodeInfoFloorReason = "min 1 h";
    private const string DefaultChannelReason = "default channel";

    // ---- Resolved settings -------------------------------------------------

    private ResolvedSetting<bool> NodeInfoEnabled => ResolveFlag(AutoReportNodeInfoEnabled, RoleCoercions.NodeInfoEnabled);
    private ResolvedSetting<int> NodeInfoSeconds => ResolveInterval(
        AutoReportNodeInfoSeconds, RoleCoercions.NodeInfoSeconds,
        BroadcastIntervals.MinNodeInfoSeconds, NodeInfoFloorReason);

    private ResolvedSetting<bool> PositionEnabled => ResolveFlag(AutoReportPositionEnabled, RoleCoercions.PositionEnabled);
    private ResolvedSetting<int> PositionSeconds => ResolveInterval(
        AutoReportPositionSeconds, RoleCoercions.PositionSeconds, PositionFloorSeconds, DefaultChannelReason);

    private ResolvedSetting<bool> PositionSmartEnabled =>
        ResolveFlag(AutoReportPositionSmartEnabled, RoleCoercions.PositionSmartEnabled);

    // Not ClampInterval'd: the smart gap is a threshold, and zero is a legal
    // "no minimum gap" the auto-report floor must not raise to five seconds.
    private ResolvedSetting<int> PositionSmartMinSeconds => SettingOverlay.Interval(
        Math.Max(0, AutoReportPositionSmartMinSeconds), RoleCoercions.PositionSmartMinSeconds,
        MyRole, SmartPositionFloorSeconds, DefaultChannelReason);

    private ResolvedSetting<uint> PositionSmartMinMoveMeters => SettingOverlay.Distance(
        AutoReportPositionSmartMinMoveMeters, RoleCoercions.PositionSmartMinMoveMeters, MyRole);

    private ResolvedSetting<bool> PositionAltitudeMsl =>
        ResolveFlag(AutoReportPositionAltitudeMsl, RoleCoercions.PositionAltitudeMsl);

    private ResolvedSetting<bool> DeviceMetricsEnabled =>
        ResolveFlag(AutoReportDeviceMetricsEnabled, RoleCoercions.DeviceMetricsEnabled);
    private ResolvedSetting<int> DeviceMetricsSeconds => ResolveInterval(
        AutoReportDeviceMetricsSeconds, RoleCoercions.DeviceMetricsSeconds,
        TelemetryFloorSeconds, DefaultChannelReason);

    private ResolvedSetting<bool> EnvironmentMetricsEnabled =>
        ResolveFlag(AutoReportEnvironmentMetricsEnabled, RoleCoercions.EnvironmentMetricsEnabled);
    private ResolvedSetting<int> EnvironmentMetricsSeconds => ResolveInterval(
        AutoReportEnvironmentMetricsSeconds, RoleCoercions.EnvironmentMetricsSeconds,
        TelemetryFloorSeconds, DefaultChannelReason);

    private ResolvedSetting<bool> AirQualityMetricsEnabled =>
        ResolveFlag(AutoReportAirQualityMetricsEnabled, RoleCoercions.AirQualityMetricsEnabled);
    private ResolvedSetting<int> AirQualityMetricsSeconds => ResolveInterval(
        AutoReportAirQualityMetricsSeconds, null, TelemetryFloorSeconds, DefaultChannelReason);

    private ResolvedSetting<bool> NodeStatusEnabled =>
        ResolveFlag(AutoReportNodeStatusEnabled, RoleCoercions.NodeStatusEnabled);

    private ResolvedSetting<bool> Unmessagable => ResolveFlag(MyIsUnmessagable, RoleCoercions.IsUnmessagable);

    /// <summary>
    /// The rebroadcast mode actually in force. Two layers: the role's own
    /// default (ROUTER installs CORE_PORTNUMS_ONLY), then firmware's runtime
    /// coercions on top — licensed operation forces LOCAL_ONLY, and NONE is not
    /// honoured for a router.
    /// </summary>
    private ResolvedSetting<string> RebroadcastModeResolved
    {
        get
        {
            string user = RebroadcastMode ?? "All";
            string roled = RoleCoercions.RebroadcastMode ?? user;
            string final = RelayPolicy.EffectiveRebroadcastMode(MyRole, roled, MyIsLicensed);

            // Compare in firmware spelling: the picker says "LocalOnly" where
            // firmware says "LOCAL_ONLY", and those are the same choice.
            if (final == RelayPolicy.EffectiveRebroadcastMode(MyRole, user, isLicensed: false) && !MyIsLicensed)
                return new ResolvedSetting<string>(final, null);

            return new ResolvedSetting<string>(final, MyIsLicensed ? "licensed" : $"role {MyRole}");
        }
    }

    // ---- What the rest of the app reads ------------------------------------

    public bool EffectiveNodeInfoEnabled => NodeInfoEnabled.Value;
    public int EffectiveNodeInfoSeconds => NodeInfoSeconds.Value;
    public bool EffectivePositionEnabled => PositionEnabled.Value;
    public int EffectivePositionSeconds => PositionSeconds.Value;
    public bool EffectivePositionSmartEnabled => PositionSmartEnabled.Value;
    public int EffectivePositionSmartMinSeconds => PositionSmartMinSeconds.Value;
    public uint EffectivePositionSmartMinMoveMeters => PositionSmartMinMoveMeters.Value;
    public bool EffectivePositionAltitudeMsl => PositionAltitudeMsl.Value;
    public bool EffectiveDeviceMetricsEnabled => DeviceMetricsEnabled.Value;
    public int EffectiveDeviceMetricsSeconds => DeviceMetricsSeconds.Value;
    public bool EffectiveEnvironmentMetricsEnabled => EnvironmentMetricsEnabled.Value;
    public int EffectiveEnvironmentMetricsSeconds => EnvironmentMetricsSeconds.Value;
    public bool EffectiveAirQualityMetricsEnabled => AirQualityMetricsEnabled.Value;
    public int EffectiveAirQualityMetricsSeconds => AirQualityMetricsSeconds.Value;
    public bool EffectiveNodeStatusEnabled => NodeStatusEnabled.Value;
    public bool EffectiveIsUnmessagable => Unmessagable.Value;

    /// <summary>The mode to hand the relay policy — role default applied, its
    /// own runtime coercions still to come.</summary>
    public string EffectiveRebroadcastMode => RoleCoercions.RebroadcastMode ?? RebroadcastMode ?? "All";

    // ---- The notes shown beside each control -------------------------------

    private static string Note<T>(ResolvedSetting<T> r, Func<T, string> format) =>
        r.IsOverridden ? $"actually {format(r.Value)} ({r.Reason})" : string.Empty;

    private static string OnOff(bool b) => b ? "on" : "off";



    public string NodeInfoEnabledNote => Note(NodeInfoEnabled, OnOff);
    public string NodeInfoSecondsNote => Note(NodeInfoSeconds, SettingOverlay.Duration);
    public string PositionEnabledNote => Note(PositionEnabled, OnOff);
    public string PositionSecondsNote => Note(PositionSeconds, SettingOverlay.Duration);
    public string PositionSmartEnabledNote => Note(PositionSmartEnabled, OnOff);
    public string PositionSmartMinSecondsNote => Note(PositionSmartMinSeconds, SettingOverlay.Duration);
    public string PositionSmartMinMoveNote =>
        Note(PositionSmartMinMoveMeters, m => DisplayUnits.FormatShortDistance(m, CurrentUnitSystem));
    public string PositionAltitudeMslNote =>
        Note(PositionAltitudeMsl, msl => msl ? "above sea level" : "above ellipsoid (HAE)");
    public string DeviceMetricsEnabledNote => Note(DeviceMetricsEnabled, OnOff);
    public string DeviceMetricsSecondsNote => Note(DeviceMetricsSeconds, SettingOverlay.Duration);
    public string EnvironmentMetricsEnabledNote => Note(EnvironmentMetricsEnabled, OnOff);
    public string EnvironmentMetricsSecondsNote => Note(EnvironmentMetricsSeconds, SettingOverlay.Duration);
    public string AirQualityMetricsEnabledNote => Note(AirQualityMetricsEnabled, OnOff);
    public string AirQualityMetricsSecondsNote => Note(AirQualityMetricsSeconds, SettingOverlay.Duration);
    public string NodeStatusEnabledNote => Note(NodeStatusEnabled, OnOff);

    /// <summary>What the channel chosen for the position report will do
    /// with it, when that is not what choosing a channel implies: sharing
    /// turned off there means nothing goes out at all, and a channel anyone
    /// can decrypt caps how precise a position may be however the channel is
    /// configured. Silent when the position goes out as asked.</summary>
    public string PositionChannelNote
    {
        get
        {
            var channel = _rxHost.FindChannelByName(AutoReportPositionChannel) ?? PrimaryChannel();
            if (channel is null) return string.Empty;

            byte effective = channel.EffectivePositionPrecision;
            if (effective == 0)
                return $"actually nothing sent (location sharing is off on {channel.Name})";

            return effective < channel.PositionPrecision
                ? $"actually {PrecisionLabel(effective)} ({channel.Name} uses a public key)"
                : string.Empty;
        }
    }

    /// <summary>How a precision reads in the picker the channel dialog
    /// offers, so the note names it the way the setting does. Bits with no
    /// row of their own are spelled out rather than guessed at.</summary>
    private string PrecisionLabel(byte bits) =>
        DisplayUnits.BuildPositionPrecisionOptions(CurrentUnitSystem)
                    .FirstOrDefault(o => o.Bits == bits)?.Label.ToLowerInvariant()
            ?? $"{bits}-bit precision";
    public string UnmessagableNote => Note(Unmessagable, OnOff);
    /// <summary>Spelled as the picker beside it does — showing
    /// CORE_PORTNUMS_ONLY next to a box reading "All" reads as a different
    /// setting rather than the same one overruled.</summary>
    public string RebroadcastModeNote => Note(RebroadcastModeResolved, PickerSpelling);

    private string PickerSpelling(string firmwareMode)
    {
        string flat = firmwareMode.Replace("_", string.Empty);
        foreach (var option in RebroadcastModeOptions)
            if (string.Equals(option, flat, StringComparison.OrdinalIgnoreCase)) return option;
        return firmwareMode;
    }

    // Visibility companions: the note row only exists when there is something
    // to say. Bound rather than converted, matching the licensed-channel
    // warning beside them.
    public bool HasNodeInfoEnabledNote => NodeInfoEnabledNote.Length > 0;
    public bool HasNodeInfoSecondsNote => NodeInfoSecondsNote.Length > 0;
    public bool HasPositionEnabledNote => PositionEnabledNote.Length > 0;
    public bool HasPositionSecondsNote => PositionSecondsNote.Length > 0;
    public bool HasPositionSmartEnabledNote => PositionSmartEnabledNote.Length > 0;
    public bool HasPositionSmartMinSecondsNote => PositionSmartMinSecondsNote.Length > 0;
    public bool HasPositionSmartMinMoveNote => PositionSmartMinMoveNote.Length > 0;
    public bool HasPositionAltitudeMslNote => PositionAltitudeMslNote.Length > 0;
    public bool HasDeviceMetricsEnabledNote => DeviceMetricsEnabledNote.Length > 0;
    public bool HasDeviceMetricsSecondsNote => DeviceMetricsSecondsNote.Length > 0;
    public bool HasEnvironmentMetricsEnabledNote => EnvironmentMetricsEnabledNote.Length > 0;
    public bool HasEnvironmentMetricsSecondsNote => EnvironmentMetricsSecondsNote.Length > 0;
    public bool HasAirQualityMetricsEnabledNote => AirQualityMetricsEnabledNote.Length > 0;
    public bool HasAirQualityMetricsSecondsNote => AirQualityMetricsSecondsNote.Length > 0;
    public bool HasNodeStatusEnabledNote => NodeStatusEnabledNote.Length > 0;
    public bool HasPositionChannelNote => PositionChannelNote.Length > 0;
    public bool HasUnmessagableNote => UnmessagableNote.Length > 0;
    public bool HasRebroadcastModeNote => RebroadcastModeNote.Length > 0;

    private static readonly string[] EffectiveSettingProperties =
    {
        nameof(EffectiveNodeInfoEnabled), nameof(EffectiveNodeInfoSeconds),
        nameof(EffectivePositionEnabled), nameof(EffectivePositionSeconds),
        nameof(EffectivePositionSmartEnabled), nameof(EffectivePositionSmartMinSeconds),
        nameof(EffectivePositionSmartMinMoveMeters), nameof(EffectivePositionAltitudeMsl),
        nameof(EffectiveDeviceMetricsEnabled), nameof(EffectiveDeviceMetricsSeconds),
        nameof(EffectiveEnvironmentMetricsEnabled), nameof(EffectiveEnvironmentMetricsSeconds),
        nameof(EffectiveAirQualityMetricsEnabled), nameof(EffectiveAirQualityMetricsSeconds),
        nameof(EffectiveNodeStatusEnabled), nameof(EffectiveIsUnmessagable),
        nameof(EffectiveRebroadcastMode),
        nameof(NodeInfoEnabledNote), nameof(NodeInfoSecondsNote),
        nameof(PositionEnabledNote), nameof(PositionSecondsNote),
        nameof(PositionSmartEnabledNote), nameof(PositionSmartMinSecondsNote),
        nameof(PositionSmartMinMoveNote), nameof(PositionAltitudeMslNote),
        nameof(DeviceMetricsEnabledNote), nameof(DeviceMetricsSecondsNote),
        nameof(EnvironmentMetricsEnabledNote), nameof(EnvironmentMetricsSecondsNote),
        nameof(AirQualityMetricsEnabledNote), nameof(AirQualityMetricsSecondsNote),
        nameof(NodeStatusEnabledNote), nameof(UnmessagableNote), nameof(RebroadcastModeNote),
        nameof(PositionChannelNote), nameof(HasPositionChannelNote),
        nameof(HasNodeInfoEnabledNote),
        nameof(HasNodeInfoSecondsNote),
        nameof(HasPositionEnabledNote),
        nameof(HasPositionSecondsNote),
        nameof(HasPositionSmartEnabledNote),
        nameof(HasPositionSmartMinSecondsNote),
        nameof(HasPositionSmartMinMoveNote),
        nameof(HasPositionAltitudeMslNote),
        nameof(HasDeviceMetricsEnabledNote),
        nameof(HasDeviceMetricsSecondsNote),
        nameof(HasEnvironmentMetricsEnabledNote),
        nameof(HasEnvironmentMetricsSecondsNote),
        nameof(HasAirQualityMetricsEnabledNote),
        nameof(HasAirQualityMetricsSecondsNote),
        nameof(HasNodeStatusEnabledNote),
        nameof(HasUnmessagableNote),
        nameof(HasRebroadcastModeNote),
    };

    /// <summary>
    /// Re-reads every derived value. Called whenever anything they are computed
    /// from moves: the role, the licence, a setting, the channel list, or the
    /// radio's band and preset — the last three because the default-channel
    /// minimums depend on where and how we are transmitting.
    /// </summary>
    public void RefreshEffectiveSettings()
    {
        foreach (var name in EffectiveSettingProperties) OnPropertyChanged(name);
    }
}
