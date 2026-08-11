// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF;

/// <summary>
/// Portable stand-in for the UI-framework dispatcher (WPF's
/// <c>System.Windows.Threading.Dispatcher</c>, Avalonia's
/// <c>Avalonia.Threading.Dispatcher</c>). Every call site in the shared RX
/// pipeline wants the same thing — run this on the UI thread at background
/// priority, so a burst of received packets can't starve UI rendering — so
/// this interface intentionally doesn't expose priority as a parameter.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread and returns immediately.</summary>
    void Post(Action action);

    /// <summary>Queues <paramref name="action"/> to run on the UI thread and returns a task that completes when it has run.</summary>
    Task InvokeAsync(Action action);
}
