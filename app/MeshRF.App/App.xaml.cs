// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MeshRF.App;

public partial class App : Application
{
    private static readonly object ExceptionLogLock = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionLogging();
        base.OnStartup(e);
        // Apply persisted theme before MainWindow is constructed so it
        // picks up the right brushes on first paint.
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
    }

    private void RegisterGlobalExceptionLogging()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteExceptionLog("DispatcherUnhandledException", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        WriteExceptionLog("AppDomain.UnhandledException", ex, e.ExceptionObject?.ToString());
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteExceptionLog("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    private static void WriteExceptionLog(string source, Exception? ex, string? fallback = null)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeshRF",
                "logs",
                "unhandled-exceptions.log");

            var dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("========================================");
            sb.AppendLine($"UTC: {DateTime.UtcNow:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Process: {Environment.ProcessPath ?? Process.GetCurrentProcess().ProcessName}");
            if (ex is not null)
            {
                sb.AppendLine(ex.ToString());
            }
            else if (!string.IsNullOrWhiteSpace(fallback))
            {
                sb.AppendLine(fallback);
            }
            else
            {
                sb.AppendLine("(No exception payload provided)");
            }

            lock (ExceptionLogLock)
            {
                File.AppendAllText(logPath, sb.ToString());
            }
        }
        catch
        {
            // Best-effort logging only; never fail startup because diagnostics failed.
        }
    }
}
