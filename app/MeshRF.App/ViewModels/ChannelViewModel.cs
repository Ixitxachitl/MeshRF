// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;

namespace MeshRF.App.ViewModels;

/// <summary>One selectable location-sharing precision (Meshtastic <c>position_precision</c>).</summary>
public sealed record PositionPrecisionOption(byte Bits, string Label);

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
        _editPositionPrecision = cfg.PositionPrecision;
    }

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
    private byte _editPositionPrecision;

    /// <summary>
    /// Discrete location-sharing precisions offered per channel, matching the
    /// official Meshtastic clients: 0 disables sharing, 32 sends the exact
    /// location, and 10–19 fuzz it to the listed radius (each step roughly
    /// halves the uncertainty). Only these <c>position_precision</c> values are
    /// considered valid on the mesh.
    /// </summary>
    public static IReadOnlyList<PositionPrecisionOption> PositionPrecisionOptions { get; } =
        new[]
        {
            new PositionPrecisionOption(0,  "Do not share location"),
            new PositionPrecisionOption(10, "Within 23 km"),
            new PositionPrecisionOption(11, "Within 12 km"),
            new PositionPrecisionOption(12, "Within 5.8 km"),
            new PositionPrecisionOption(13, "Within 2.9 km"),
            new PositionPrecisionOption(14, "Within 1.5 km"),
            new PositionPrecisionOption(15, "Within 700 m"),
            new PositionPrecisionOption(16, "Within 350 m"),
            new PositionPrecisionOption(17, "Within 200 m"),
            new PositionPrecisionOption(18, "Within 90 m"),
            new PositionPrecisionOption(19, "Within 50 m"),
            new PositionPrecisionOption(32, "Precise"),
        };

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
        Config.PositionPrecision = EditPositionPrecision;
        _onSave?.Invoke(Config);
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(PskHex));
        OnPropertyChanged(nameof(IsPrimary));
    }

    /// <summary>
    /// Rename the channel in place (used to keep the default Primary channel's
    /// name in sync with the active modem preset) and refresh the tab header.
    /// </summary>
    public void RenameTo(string name)
    {
        Config.Name = name ?? string.Empty;
        EditName = Config.Name;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabHeader));
        OnPropertyChanged(nameof(Hash));
        OnPropertyChanged(nameof(PskHex));
    }

    [RelayCommand]
    private void Revert()
    {        EditName              = Config.Name;
        EditRole              = Config.Role;
        EditPsk               = (byte[])Config.Psk.Clone();
        EditPositionPrecision = Config.PositionPrecision;
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
}

public partial class ChannelMessage : ObservableObject
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string FromId  { get; init; } = string.Empty;
    public string Text    { get; init; } = string.Empty;
    public float? RssiDbm { get; init; }
    public float? SnrDb   { get; init; }

    /// <summary>Packet id of this message (for matching ACKs). 0 = unknown.</summary>
    public uint PacketId { get; init; }

    /// <summary>True for messages we transmitted (so delivery status applies).</summary>
    public bool IsOutgoing { get; init; }

    /// <summary>Delivery state for outgoing messages, updated when an ACK/NAK
    /// arrives. Always <see cref="MessageDelivery.None"/> for received messages.</summary>
    [ObservableProperty]
    private MessageDelivery _delivery = MessageDelivery.None;

    partial void OnDeliveryChanged(MessageDelivery value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(TextWithStatus));
    }

    private string DeliverySuffix => Delivery switch
    {
        MessageDelivery.Sent      => "  \u00B7 sent",
        MessageDelivery.Delivered => "  \u00B7 delivered",
        MessageDelivery.Failed    => "  \u00B7 no ack",
        _ => string.Empty,
    };

    /// <summary>Timestamp column, e.g. <c>[14:03:22]</c> (fixed width).</summary>
    public string TimePrefix => $"[{Timestamp:HH:mm:ss}]";

    /// <summary>Message body plus the delivery-status suffix, for the text column.</summary>
    public string TextWithStatus => $"{Text}{DeliverySuffix}";

    /// <summary>Single-line rendering used for clipboard copy.</summary>
    public string Display =>
        $"[{Timestamp:HH:mm:ss}] {FromId,-12}  {Text}{DeliverySuffix}";
}

/// <summary>Delivery state of an outgoing message based on Meshtastic ACKs.</summary>
public enum MessageDelivery
{
    None,
    Sent,
    Delivered,
    Failed,
}
