// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Holds MeshRF to one running copy per user. Two copies share one
/// %APPDATA%\MeshRF\settings.json and write it on every change, so the second
/// to close silently overwrites the first's window layout, channels and radio
/// selection — and they compete for the same SDR besides.
/// </summary>
internal static class SingleInstance
{
    // Per-user, not machine-wide: settings.json lives in the user's roaming
    // profile, so two people signed in to the same machine are two independent
    // instances. "Local\" is the Windows session namespace and is ignored on
    // other platforms, where named mutexes are already per-user.
    private const string MutexName = @"Local\MeshRF.SingleInstance";
    private const string ActivateEventName = @"Local\MeshRF.Activate";

    // Held for the life of the process. The OS releases it on exit — including
    // a crash — so a killed instance doesn't lock the app out.
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;

    /// <summary>
    /// True when this process is the one instance and may start up. False when
    /// another is already running, in which case <paramref name="raisedRunningInstance"/>
    /// says whether its window was brought to the front — only Windows has the
    /// named event that carries the request.
    /// </summary>
    public static bool TryAcquire(out bool raisedRunningInstance)
    {
        raisedRunningInstance = false;
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            ListenForActivation();
            return true;
        }

        mutex.Dispose();
        raisedRunningInstance = RaiseRunningInstance();
        return false;
    }

    /// <summary>Waits for a second launch to ask for the window, for as long as
    /// this process lives.</summary>
    private static void ListenForActivation()
    {
        // Named events are Windows-only. Elsewhere a second launch still
        // refuses to start, it just can't raise the window that stopped it.
        if (!OperatingSystem.IsWindows()) return;

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activate = activate;
        new Thread(() =>
        {
            while (true)
            {
                activate.WaitOne();
                Dispatcher.UIThread.Post(RaiseMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "MeshRF activation listener",
        }.Start();
    }

    /// <summary>Asks the instance that already owns the mutex to show itself.
    /// </summary>
    private static bool RaiseRunningInstance()
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate)) return false;
            using (activate) activate.Set();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // The running instance belongs to another user (fast user
            // switching, a service account): its window isn't ours to raise.
            return false;
        }
    }

    private static void RaiseMainWindow()
    {
        // Posted, so it can arrive before OnFrameworkInitializationCompleted
        // has built the window.
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not { } window) return;

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
    }
}
