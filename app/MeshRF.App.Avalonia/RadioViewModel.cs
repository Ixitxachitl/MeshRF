// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    // 906.875 MHz = US LongFast slot 20, same default MeshRF.App's
    // MainViewModel starts from.
    private const double DefaultCenterFreqMHz = 906.875;

    // Mirrors MainViewModel.PayloadLineRegex; matches lines like
    // "  payload[OK] len=31 crc=E511/E511 FFFFFFFF594FA54F...".
    private static readonly Regex PayloadLineRegex = new(
        @"payload(?:\[(?<status>OK|BAD)\])?\s+len=(?<len>\d+)(?:\s+crc=(?<rx>[0-9A-Fa-f]+)/(?<calc>[0-9A-Fa-f]+))?\s+(?<hex>[0-9A-Fa-f]+)",
        RegexOptions.Compiled);

    private readonly MeshtasticCore? _core;
    private readonly DispatcherTimer _pollTimer;
    private readonly NodeStore _nodeStore = new();
    private readonly MessageStore _messageStore = new();
    private readonly AvaloniaMeshRxHost _rxHost;
    private readonly MeshRxRouter _rxRouter;

    public ObservableCollection<ChannelMessage> Messages => _rxHost.Messages;
    public ObservableCollection<NodeRecord> Nodes => _rxHost.Nodes;

    [ObservableProperty]
    private RadioDeviceKind _selectedDevice = RadioDeviceKind.Auto;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _deviceStatus = string.Empty;

    [ObservableProperty]
    private float _rssiDbfs;

    public RadioDeviceKind[] AvailableDevices { get; } = Enum.GetValues<RadioDeviceKind>();

    public string ToggleButtonText => IsRunning ? "Stop RX" : "Start RX";

    public RadioViewModel()
    {
        _rxHost = new AvaloniaMeshRxHost(_nodeStore);
        _rxRouter = new MeshRxRouter(_rxHost, _messageStore, new AvaloniaUiDispatcher());

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
            var hz = (ulong)(DefaultCenterFreqMHz * 1_000_000);
            try
            {
                _core.StartRx(LoraPreset.LongFast, hz);
            }
            catch (InvalidOperationException ex)
            {
                StatusText = $"Failed to start RX: {ex.Message}";
            }
        }
        Poll();
    }

    private bool CanToggleRx() => _core is not null;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(ToggleButtonText));

    public void Dispose()
    {
        _pollTimer.Stop();
        _rxRouter.Dispose();
        _core?.Dispose();
    }
}
