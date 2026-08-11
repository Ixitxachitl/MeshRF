// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Minimal but functional radio control surface: proves MeshtasticCore
/// (device select / start-stop RX / signal stats) drives correctly from a
/// non-WPF frontend, not just that the assembly links.
/// </summary>
public partial class RadioViewModel : ObservableObject, IDisposable
{
    // 906.875 MHz = US LongFast slot 20, same default MeshRF.App's
    // MainViewModel starts from.
    private const double DefaultCenterFreqMHz = 906.875;

    private readonly MeshtasticCore? _core;
    private readonly DispatcherTimer _pollTimer;

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
        if (IsRunning)
        {
            RssiDbfs = _core.GetSignalStats().RssiDbfs;
        }
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
            _core.StartRx(LoraPreset.LongFast, hz);
        }
        Poll();
    }

    private bool CanToggleRx() => _core is not null;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(ToggleButtonText));

    public void Dispose()
    {
        _pollTimer.Stop();
        _core?.Dispose();
    }
}
