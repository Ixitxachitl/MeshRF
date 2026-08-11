// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using MeshRF;

namespace MeshRF.AvaloniaApp;

public partial class MainWindow : Window
{
    public string StatusLine { get; } =
        $"Running on {Environment.OSVersion.Platform}, {Environment.OSVersion.VersionString}";

    public string NativeStatusLine { get; }

    public MainWindow()
    {
        InitializeComponent();

        NativeStatusLine = ProbeNativeBridge();
        DataContext = this;
    }

    // Proves the P/Invoke plumbing into the native bridge (native/bridge,
    // built by CMake) works from this project, not just from MeshRF.App
    // (WPF). Doesn't touch a real radio — MeshtasticCore's constructor just
    // creates the native mrf_core_t instance.
    private static string ProbeNativeBridge()
    {
        try
        {
            using var core = new MeshtasticCore();
            return $"Native bridge loaded. Device status: {core.DeviceStatus}";
        }
        catch (Exception ex)
        {
            return $"Native bridge not available: {ex.Message}";
        }
    }
}
