// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// About box. Surfaces the exact build identity — version plus the git commit
/// the SDK stamps into InformationalVersion — and the runtime environment, so a
/// bug report can name precisely what was running. The environment block is
/// selectable and there is a copy button, because that text is the whole point
/// of the dialog.
/// </summary>
public partial class AboutWindow : Window
{
    private readonly string _versionInfo;

    public AboutWindow()
    {
        InitializeComponent();

        var asm = Assembly.GetExecutingAssembly();
        // Looks like "2.0.0+dfc2918": the SDK appends the short SHA via
        // SourceRevisionId (see Directory.Build.props). Split it for display.
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;

        string version = informational;
        string commit = string.Empty;
        int plus = informational.IndexOf('+');
        if (plus >= 0)
        {
            version = informational[..plus];
            commit = informational[(plus + 1)..];
        }
        if (string.IsNullOrWhiteSpace(version))
            version = asm.GetName().Version?.ToString() ?? "unknown";

        var avalonia = typeof(Avalonia.Application).Assembly.GetName().Version;

        VersionText.Text = $"Version {version}";
        CommitText.Text = string.IsNullOrWhiteSpace(commit) ? string.Empty : $"Build {commit}";
        CommitText.IsVisible = !string.IsNullOrWhiteSpace(commit);

        var lines = new[]
        {
            $"MeshRF {version}" + (string.IsNullOrWhiteSpace(commit) ? string.Empty : $" ({commit})"),
            $".NET {Environment.Version}   Avalonia {avalonia}",
            $"{RuntimeInformation.OSDescription}",
            $"{RuntimeInformation.ProcessArchitecture} process on {RuntimeInformation.OSArchitecture}",
        };
        EnvironmentText.Text = string.Join(Environment.NewLine, lines);
        _versionInfo = EnvironmentText.Text;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await clipboard.SetTextAsync(_versionInfo);
            // Confirm in place: there is no status bar on this dialog, and a
            // copy button that looks inert is indistinguishable from a broken one.
            CopyButton.Content = "Copied";
        }
        catch
        {
            // Another process may hold the clipboard.
            CopyButton.Content = "Copy failed";
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
