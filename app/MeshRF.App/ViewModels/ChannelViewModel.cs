// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.App.Units;
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
        _muteRtttl = muteRtttl;
        UpdatePositionPrecisionOptions(unitSystem);
    }

    /// <summary>Decoded text messages, newest last.</summary>
    public ObservableCollection<ChannelMessage> Messages { get; } = new();

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

    /// <summary>
    /// Discrete location-sharing precisions offered per channel, matching the
    /// official Meshtastic clients: 0 disables sharing, 32 sends the exact
    /// location, and 10–19 fuzz it to the listed radius (each step roughly
    /// halves the uncertainty). Only these <c>position_precision</c> values are
    /// considered valid on the mesh.
    /// </summary>
    private IReadOnlyList<PositionPrecisionOption> _positionPrecisionOptions = Array.Empty<PositionPrecisionOption>();
    public IReadOnlyList<PositionPrecisionOption> PositionPrecisionOptions => _positionPrecisionOptions;

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

    public void UpdatePositionPrecisionOptions(UnitSystem unitSystem)
    {
        _positionPrecisionOptions = DisplayUnits.BuildPositionPrecisionOptions(unitSystem);
        OnPropertyChanged(nameof(PositionPrecisionOptions));
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
    private const string UiDateTimeFormat = "M/d/yyyy h:mm:ss tt";

    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string FromId  { get; init; } = string.Empty;
    public string Text    { get; init; } = string.Empty;
    public float? RssiDbm { get; init; }
    public float? SnrDb   { get; init; }

    /// <summary>Packet id of this message (for matching ACKs). 0 = unknown.</summary>
    public uint PacketId { get; init; }

    /// <summary>True for messages we transmitted (so delivery status applies).</summary>
    public bool IsOutgoing { get; init; }

    /// <summary>True when the sender is marked ignored in the node list.</summary>
    public bool IsIgnoredSender { get; init; }

    /// <summary>True when this message references an earlier packet via reply_id.</summary>
    public bool IsReplyLinked { get; init; }

    /// <summary>True when the referenced reply target existed in the local view.</summary>
    public bool ReplyTargetFound { get; init; }

    /// <summary>Packet id this message replies to (0 when not reply-linked).</summary>
    public uint ReplyToPacketId { get; init; }

    /// <summary>Aggregated reactions attached to this message.</summary>
    public ObservableCollection<MessageReaction> Reactions { get; } = new();

    private readonly Dictionary<string, MessageReaction> _reactionsByEmoji = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _reactorsByEmoji = new(StringComparer.Ordinal);

    public bool HasReactions => Reactions.Count > 0;

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

    /// <summary>Timestamp column rendered as local date + 12-hour time.</summary>
    public string TimePrefix => $"[{Timestamp.ToString(UiDateTimeFormat, CultureInfo.CurrentCulture)}]";

    /// <summary>Message body plus the delivery-status suffix, for the text column.</summary>
    public string TextWithStatus => $"{Text}{DeliverySuffix}";

    /// <summary>Single-line rendering used for clipboard copy.</summary>
    public string Display =>
        $"[{Timestamp.ToString(UiDateTimeFormat, CultureInfo.CurrentCulture)}] {FromId,-12}  {Text}{DeliverySuffix}";

    /// <summary>Add or update one reaction for this message. A sender only
    /// counts once per emoji.</summary>
    public void AddReaction(string emoji, string fromId)
    {
        var emojiKey = (emoji ?? string.Empty).Trim();
        if (emojiKey.Length == 0) return;

        var sender = string.IsNullOrWhiteSpace(fromId) ? "unknown" : fromId.Trim();
        if (!_reactorsByEmoji.TryGetValue(emojiKey, out var reactors))
        {
            reactors = new HashSet<string>(StringComparer.Ordinal);
            _reactorsByEmoji[emojiKey] = reactors;
        }

        if (!reactors.Add(sender)) return;

        if (!_reactionsByEmoji.TryGetValue(emojiKey, out var reaction))
        {
            reaction = new MessageReaction
            {
                Emoji = emojiKey,
                Count = reactors.Count,
                Reactors = string.Join(", ", reactors.OrderBy(x => x, StringComparer.Ordinal)),
            };
            _reactionsByEmoji[emojiKey] = reaction;
            Reactions.Add(reaction);
            OnPropertyChanged(nameof(HasReactions));
            return;
        }

        reaction.Count = reactors.Count;
        reaction.Reactors = string.Join(", ", reactors.OrderBy(x => x, StringComparer.Ordinal));
    }
}

public partial class MessageReaction : ObservableObject
{
    public string Emoji { get; init; } = string.Empty;

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _reactors = string.Empty;

    public string Display => $"{Emoji} {Count}";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Display));
}

/// <summary>Delivery state of an outgoing message based on Meshtastic ACKs.</summary>
public enum MessageDelivery
{
    None,
    Sent,
    Delivered,
    Failed,
}
