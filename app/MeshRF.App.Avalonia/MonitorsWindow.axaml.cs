// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// What the receiver listens for besides the primary. Every row is computed
/// from the current region, primary and sample rate rather than stored, so
/// the window shows the outcome of the settings as they stand — including
/// which sample rate would bring a preset that is out of range into it.
/// </summary>
public partial class MonitorsWindow : Window
{
    public MonitorsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public static void Open(Window owner, RadioViewModel viewModel)
    {
        // Built on open rather than kept up to date in the background: it is
        // a handful of frequency calculations, and nothing else looks at the
        // rows while the window is closed.
        viewModel.RefreshMonitors();
        var w = new MonitorsWindow { DataContext = viewModel };
        w.Show(owner);
    }

    private RadioViewModel? Vm => DataContext as RadioViewModel;

    /// <summary>The listener a row stands for. Rows are rebuilt from the plan
    /// every time it changes, so they carry a name rather than the object.
    /// </summary>
    private CustomListenerEdit? ListenerFor(object? sender) =>
        sender is Control { DataContext: MonitorPresetRow row } && Vm is { } vm
            ? vm.CustomListenerNamed(row.Name)
            : null;

    private async void OnAddListener(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (await CustomListenerWindow.ShowAsync(this, vm, null) is { } made)
            vm.SaveCustomListener(made, null);
    }

    private async void OnEditListener(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ListenerFor(sender) is not { } existing) return;
        if (await CustomListenerWindow.ShowAsync(this, vm, existing) is { } edited)
            vm.SaveCustomListener(edited, existing);
    }

    private void OnRemoveListener(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || ListenerFor(sender) is not { } existing) return;
        vm.RemoveCustomListenerCommand.Execute(existing);
    }
}
