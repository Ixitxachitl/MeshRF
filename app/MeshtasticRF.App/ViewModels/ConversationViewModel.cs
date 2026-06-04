// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Linq;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshtasticRF.Nodes;

namespace MeshtasticRF.App.ViewModels;

/// <summary>A single label/value row of node telemetry shown in a DM tab.</summary>
public sealed record TelemetryItem(string Label, string Value);

/// <summary>
/// A direct-message conversation with a single peer node. Shown as a closable
/// tab alongside the channel tabs.
/// </summary>
public partial class ConversationViewModel : ObservableObject, ITabItem
{
    public ConversationViewModel(uint nodeNum, string? peerName = null)
    {
        NodeNum = nodeNum;
        _peerName = string.IsNullOrWhiteSpace(peerName) ? string.Empty : peerName!;
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

    /// <summary>True when at least one telemetry value is available.</summary>
    public bool HasTelemetry => Telemetry.Count > 0;

    /// <summary>Direct messages exchanged with this peer, newest last.</summary>
    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    public string TabHeader =>
        string.IsNullOrEmpty(PeerName) ? PeerId : PeerName;

    public bool CanClose => true;

    partial void OnPeerNameChanged(string value) => OnPropertyChanged(nameof(TabHeader));

    partial void OnNodeChanged(NodeRecord? value) => RebuildTelemetry();

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
            Add("Hardware", n.HwModel);
            Add("Role", n.Role);
            if (n.BatteryPct is byte bat) Add("Battery", $"{bat}%");
            if (n.VoltageV is float volt) Add("Voltage", $"{volt:0.00} V");
            if (n.ChannelUtilPct is float chUtil) Add("Channel util", $"{chUtil:0.0}%");
            if (n.AirUtilTxPct is float airUtil) Add("Air util TX", $"{airUtil:0.0}%");
            if (n.UptimeSeconds is uint up) Add("Uptime", FormatUptime(up));
            if (n.TemperatureC is float temp) Add("Temperature", $"{temp:0.0} \u00B0C");
            if (n.RelativeHumidityPct is float hum) Add("Humidity", $"{hum:0.0}%");
            if (n.BarometricPressureHpa is float pres) Add("Pressure", $"{pres:0.0} hPa");
            if (n.GasResistanceMohm is float gas) Add("Gas resistance", $"{gas:0.0} M\u03A9");
            if (n.Iaq is int iaq) Add("Air quality (IAQ)", iaq.ToString());
            if (n.SnrDb is float snr) Add("SNR", $"{snr:0.0} dB");
            if (n.RssiDbm is float rssi) Add("RSSI", $"{rssi:0} dBm");
            if (n.HopsAway is byte hops) Add("Hops away", hops.ToString());
            if (n.Latitude is double lat && n.Longitude is double lon)
                Add("Position", $"{lat:0.#####}, {lon:0.#####}");
            if (n.AltitudeM is int alt) Add("Altitude", $"{alt} m");
            if (n.LastHeardEpoch != 0) Add("Last heard", n.LastHeard.ToString("g"));
        }

        OnPropertyChanged(nameof(HasTelemetry));
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
    private void CopyMessages()
    {
        if (Messages.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Messages.Select(m => m.Display))); }
        catch { }
    }
}
