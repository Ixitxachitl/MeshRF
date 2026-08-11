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

    public string TabHeader =>
        Config.Role == ChannelRole.Primary ? $"{DisplayName} ★" : DisplayName;

    public bool CanClose => false;

    [ObservableProperty]
    private bool _tabNeedsAttention;

    public ChannelTabViewModel(ChannelConfig config)
    {
        Config = config;
    }
}
