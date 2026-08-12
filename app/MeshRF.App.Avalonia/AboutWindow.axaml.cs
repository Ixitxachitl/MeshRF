// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MeshRF.AvaloniaApp;

/// <summary>About box, mirroring MeshRF.App's AboutWindow.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var asm = Assembly.GetExecutingAssembly();
        // InformationalVersion carries the +commit suffix when the build sets it.
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? asm.GetName().Version?.ToString() ?? "unknown";

        VersionText.Text = $"Version {version}";
        RuntimeText.Text = $".NET {Environment.Version}  ·  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}  ·  " +
                           $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
