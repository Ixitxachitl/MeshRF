// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Channels;
using MeshRF.Mesh;
using MeshRF.Messages;
using MeshRF.Nodes;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Radio control surface: device select / start-stop RX / signal stats,
/// plus a real (not mocked) message/node list — received frames are fed
/// through the same MeshRxRouter (MeshRF.Core) the WPF app uses, via
/// AvaloniaMeshRxHost.
/// </summary>
public partial class RadioViewModel : ObservableObject, IDisposable
{
    // Mirrors MainViewModel.PayloadLineRegex; matches lines like
    // "  payload[OK] len=31 crc=E511/E511 FFFFFFFF594FA54F...".
    private static readonly Regex PayloadLineRegex = new(
        @"payload(?:\[(?<status>OK|BAD)\])?\s+len=(?<len>\d+)(?:\s+crc=(?<rx>[0-9A-Fa-f]+)/(?<calc>[0-9A-Fa-f]+))?\s+(?<hex>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private readonly MeshtasticCore? _core;
    private readonly DispatcherTimer _pollTimer;
    private readonly NodeStore _nodeStore = new();
    private readonly MessageStore _messageStore = new();
    private readonly ChannelStore _channelStore = new();
    private readonly AvaloniaMeshRxHost _rxHost;
    private readonly MeshRxRouter _rxRouter;

    public ObservableCollection<ITabItem> Tabs => _rxHost.Tabs;
    public ObservableCollection<NodeRecord> Nodes => _rxHost.Nodes;

    [ObservableProperty]
    private ITabItem? _selectedTab;

    [ObservableProperty]
    private string _newChannelName = string.Empty;

    [ObservableProperty]
    private RadioDeviceKind _selectedDevice = RadioDeviceKind.Auto;

    // 906.875 MHz = US LongFast slot 20, same default MeshRF.App's
    // MainViewModel starts from.
    [ObservableProperty]
    private double _centerFreqMHz = 906.875;

    [ObservableProperty]
    private LoraPreset _selectedPreset = LoraPreset.LongFast;

    public LoraPreset[] AvailablePresets { get; } = Enum.GetValues<LoraPreset>();

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _deviceStatus = string.Empty;

    [ObservableProperty]
    private float _rssiDbfs;

    [ObservableProperty]
    private string _messageText = string.Empty;

    public RadioDeviceKind[] AvailableDevices { get; } = Enum.GetValues<RadioDeviceKind>();

    public string ToggleButtonText => IsRunning ? "Stop RX" : "Start RX";

    public RadioViewModel()
    {
        _rxHost = new AvaloniaMeshRxHost(_nodeStore, _channelStore)
        {
            // Ephemeral session identity (random, not persisted) — see
            // AvaloniaMeshRxHost.MyNodeNum. Avoid 0 (unset) and the
            // broadcast address.
            MyNodeNum = (uint)Random.Shared.NextInt64(1, 0xFFFFFFFE),
        };
        _rxRouter = new MeshRxRouter(_rxHost, _messageStore, new AvaloniaUiDispatcher());
        SelectedTab = Tabs.FirstOrDefault();

        try
        {
            _core = new MeshtasticCore();
            StatusText = $"Native bridge loaded ({Environment.OSVersion.Platform}).";
        }
        catch (Exception ex)
        {
            StatusText = $"Native bridge unavailable: {ex.Message}";
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _pollTimer.Tick += (_, _) => Poll();
        _pollTimer.Start();
        Poll();
    }

    private void Poll()
    {
        if (_core is null) return;

        IsRunning = _core.IsRunning;
        DeviceStatus = $"RX: {_core.DeviceName}  TX: {_core.TxDeviceName} — {_core.DeviceStatus}";
        if (!IsRunning) return;

        RssiDbfs = _core.GetSignalStats().RssiDbfs;
        _rxHost.CurrentRssiDbfs = RssiDbfs;

        for (int i = 0; i < 64; i++)
        {
            var ev = _core.PullEvent();
            if (ev is null) break;
            ProcessDemodEvent(ev);
        }
    }

    private void ProcessDemodEvent(string ev)
    {
        if (ev.IndexOf("payload", StringComparison.Ordinal) < 0) return;
        var m = PayloadLineRegex.Match(ev);
        if (!m.Success) return;
        if (!(m.Groups["status"].Success && m.Groups["status"].Value == "OK")) return;

        var frame = HexToBytes(m.Groups["hex"].Value);
        if (frame.Length < MeshHeader.Size) return;
        if (!MeshHeader.TryParse(frame, out var header)) return;

        float? packetRssiDbm = float.IsNegativeInfinity(RssiDbfs) ? null : RssiDbfs;
        _rxRouter.ProcessReceivedFrame(frame, header, snrDb: null, packetRssiDbm: packetRssiDbm);
    }

    private static byte[] HexToBytes(string hex)
    {
        if ((hex.Length & 1) != 0) return Array.Empty<byte>();
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out b[i]))
                return Array.Empty<byte>();
        }
        return b;
    }

    [RelayCommand(CanExecute = nameof(CanToggleRx))]
    private void ToggleRx()
    {
        if (_core is null) return;

        if (_core.IsRunning)
        {
            _core.Stop();
        }
        else
        {
            _core.SetRxDevice(SelectedDevice);
            var hz = (ulong)(CenterFreqMHz * 1_000_000);
            try
            {
                _core.StartRx(SelectedPreset, hz);
            }
            catch (InvalidOperationException ex)
            {
                StatusText = $"Failed to start RX: {ex.Message}";
            }
        }
        Poll();
    }

    private bool CanToggleRx() => _core is not null;

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (_core is null || string.IsNullOrWhiteSpace(MessageText)) return;

        // DMs aren't PKC-sealed here (no node identity/PKI yet) — sent as a
        // legacy channel-PSK-encrypted unicast on the primary channel,
        // exactly like a broadcast but addressed to one node.
        ObservableCollection<ChannelMessage>? messages;
        ChannelConfig channel;
        uint to = 0xFFFFFFFFu;
        switch (SelectedTab)
        {
            case ChannelTabViewModel chanTab:
                messages = chanTab.Messages;
                channel = chanTab.Config;
                break;
            case ConversationTabViewModel convoTab:
                var primary = Tabs.OfType<ChannelTabViewModel>().FirstOrDefault();
                if (primary is null) return;
                messages = convoTab.Messages;
                channel = primary.Config;
                to = convoTab.NodeNum;
                break;
            default:
                return;
        }

        var text = MessageText.Trim();
        var packetId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        var hz = (ulong)(CenterFreqMHz * 1_000_000);

        var frame = MeshEncoder.EncodeTextMessage(channel, _rxHost.MyNodeNum, packetId, text, to: to);

        bool ok = await Task.Run(() => _core.Transmit(SelectedPreset, hz, frame)).ConfigureAwait(true);
        if (!ok)
        {
            StatusText = "Failed to transmit (no TX-capable device selected?).";
            return;
        }

        // Echo locally — we won't decode our own transmission back off the
        // air (MeshRxRouter treats hearing it as isFromUs and drops it).
        messages.Insert(0, new ChannelMessage
        {
            FromId = $"!{_rxHost.MyNodeNum:x8}",
            SenderNodeNum = _rxHost.MyNodeNum,
            Text = text,
            PacketId = packetId,
            IsOutgoing = true,
        });
        MessageText = string.Empty;
    }

    private bool CanSendMessage() =>
        _core?.CanTransmit == true && SelectedTab is not null && !string.IsNullOrWhiteSpace(MessageText);

    partial void OnMessageTextChanged(string value) => SendMessageCommand.NotifyCanExecuteChanged();
    partial void OnSelectedTabChanged(ITabItem? value) => SendMessageCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void MessageNode(NodeRecord? node)
    {
        if (node is null) return;
        SelectedTab = _rxHost.OpenConversation(node.NodeNum);
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ToggleButtonText));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddChannel))]
    private void AddChannel()
    {
        var name = NewChannelName.Trim();
        if (name.Length == 0) return;
        SelectedTab = _rxHost.AddChannel(name);
        NewChannelName = string.Empty;
    }

    private bool CanAddChannel() => !string.IsNullOrWhiteSpace(NewChannelName);

    partial void OnNewChannelNameChanged(string value) => AddChannelCommand.NotifyCanExecuteChanged();

    public void Dispose()
    {
        _pollTimer.Stop();
        _rxRouter.Dispose();
        _rxHost.Dispose();
        _nodeStore.Dispose();
        _messageStore.Dispose();
        _core?.Dispose();
    }
}
