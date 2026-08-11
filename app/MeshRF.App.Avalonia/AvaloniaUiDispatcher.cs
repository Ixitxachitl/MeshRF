// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;

namespace MeshRF.AvaloniaApp;

/// <summary>Avalonia-backed <see cref="IUiDispatcher"/>, always posting at background priority.</summary>
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background).GetTask();
}
