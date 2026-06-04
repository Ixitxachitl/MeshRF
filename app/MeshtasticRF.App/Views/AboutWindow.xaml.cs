// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace MeshtasticRF.App.Views;

/// <summary>
/// Modal "About" dialog. Surfaces the build identity (version + git commit)
/// read from the assembly's informational version so users can report exactly
/// which build they are running.
/// </summary>
public partial class AboutWindow : Window
{
    public string VersionText { get; }
    public string CommitText { get; }
    public string RuntimeText { get; }

    private readonly string _fullVersionInfo;

    public AboutWindow()
    {
        InitializeComponent();

        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;

        // Informational version looks like "0.1.0+dfc2918" (the SDK appends the
        // git SHA via SourceRevisionId). Split it for display.
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

        VersionText = $"Version {version}";
        CommitText = string.IsNullOrWhiteSpace(commit) ? string.Empty : $"Build {commit}";
        RuntimeText = $".NET {Environment.Version}  ·  {RuntimeInformation.OSArchitecture}";
        _fullVersionInfo =
            $"MeshtasticRF {version}\n" +
            (string.IsNullOrWhiteSpace(commit) ? string.Empty : $"Build {commit}\n") +
            $".NET {Environment.Version} ({RuntimeInformation.OSArchitecture})";

        DataContext = this;
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: ignore if no browser is available.
        }
        e.Handled = true;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_fullVersionInfo); }
        catch { /* clipboard may be locked by another process */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
