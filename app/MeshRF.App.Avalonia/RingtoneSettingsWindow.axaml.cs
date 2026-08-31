// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Every notification setting in one place: the three alert tunes, how long
/// each plays, and the volume they share. Every field is a two-way binding
/// onto <see cref="RadioViewModel"/>, which persists on change, so there is
/// no Save button — same as the MQTT dialog.
/// </summary>
public partial class RingtoneSettingsWindow : Window
{
    public RingtoneSettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Opens the dialog, or focuses it when it is already up.</summary>
    public static void Show(Window owner, RadioViewModel viewModel)
    {
        if (s_open is { } existing)
        {
            existing.Activate();
            return;
        }

        var w = new RingtoneSettingsWindow { DataContext = viewModel };
        s_open = w;
        w.Closed += (_, _) => s_open = null;
        w.Show(owner);
    }

    private static RingtoneSettingsWindow? s_open;
}
