// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;

namespace MeshRF.App;

/// <summary>WPF-backed <see cref="IUiDispatcher"/>, always posting at background priority.</summary>
internal sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.InvokeAsync(action, DispatcherPriority.Background);

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
}
