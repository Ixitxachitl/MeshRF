// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// One direct-message conversation tab, keyed by peer node number. Minimal
/// counterpart to MeshRF.App's ConversationViewModel (no telemetry-history
/// tab, no reactions/reply UI yet).
/// </summary>
public partial class ConversationTabViewModel : ObservableObject, ITabItem
{
    public uint NodeNum { get; }

    public ObservableCollection<ChannelMessage> Messages { get; } = new();

    [ObservableProperty]
    private string _peerName;

    public string TabHeader => PeerName;

    public bool CanClose => true;

    [ObservableProperty]
    private bool _tabNeedsAttention;

    public ConversationTabViewModel(uint nodeNum, string peerName)
    {
        NodeNum = nodeNum;
        _peerName = peerName;
    }
}
