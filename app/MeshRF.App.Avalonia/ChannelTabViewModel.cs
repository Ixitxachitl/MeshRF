// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Channels;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// One channel tab: persisted <see cref="ChannelConfig"/> plus its in-memory
/// message list. Minimal counterpart to MeshRF.App's ChannelViewModel (no
/// editable-settings commands or game high-score tables yet).
/// </summary>
public partial class ChannelTabViewModel : ObservableObject, ITabItem
{
    public ChannelConfig Config { get; }

    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    public string DisplayName =>
        string.IsNullOrEmpty(Config.Name) ? $"Channel {Config.Index}" : Config.Name;

    public string TabHeader
    {
        get
        {
            var header = Config.Role switch
            {
                ChannelRole.Primary => $"{DisplayName} ★",
                // A disabled channel keeps its tab and history but is inert on the air:
                // it decodes nothing and can't be sent on, so say so in the header.
                ChannelRole.Disabled => $"{DisplayName} (off)",
                _ => DisplayName,
            };
            // A channel in a secondary preset's list says which: two lists can
            // each hold a "LongFast", and only one of them is on LongFast.
            return Config.Preset.Length == 0 ? header : $"{header} · {Config.Preset}";
        }
    }

    public bool CanClose => false;

    [ObservableProperty]
    private bool _tabNeedsAttention;

    [ObservableProperty]
    private bool _startsTabGroup;

    /// <summary>Suppress the incoming-text ringtone for this channel. Persisted
    /// in settings.json's MutedRingtoneChannels, the same key MeshRF.App
    /// uses.</summary>
    [ObservableProperty]
    private bool _muteRtttl;

    public ChannelTabViewModel(ChannelConfig config)
    {
        Config = config;
    }

    /// <summary>Config's fields (name/role/etc.) were edited in place — refresh
    /// the computed display properties that don't otherwise get notified.</summary>
    public void NotifyConfigChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(TabHeader));
    }
}
