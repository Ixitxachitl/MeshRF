// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// What this station broadcasts as a beacon: whether to, what it says, how
/// often, the channel it advertises, and the channels it goes out on.
/// </summary>
/// <remarks>
/// The destination list is the part with no default. Firmware treats an empty
/// one as "send on the running preset over the primary channel"; this sends
/// nothing at all, because a beacon names a mesh to strangers and which meshes
/// it reaches is not a thing to be inferred.
/// </remarks>
public partial class BeaconSettingsWindow : Window
{
    public BeaconSettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Insert a picked emoji at the caret in the message box, the
    /// same way the compose box takes one.</summary>
    private async void OnPickBeaconEmoji(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RadioViewModel viewModel) return;

        var glyph = await EmojiPickerWindow.PickAsync(this);
        if (string.IsNullOrEmpty(glyph)) return;

        var text = viewModel.BeaconMessage ?? string.Empty;
        int caret = Math.Clamp(MessageBox.CaretIndex, 0, text.Length);
        viewModel.BeaconMessage = text.Insert(caret, glyph);
        MessageBox.CaretIndex = caret + glyph.Length;
        MessageBox.Focus();
    }

    public static BeaconSettingsWindow Open(Window owner, RadioViewModel viewModel)
    {
        // Channels come and go while this window is closed, so both pickers
        // are built from what exists at the moment it opens.
        viewModel.RefreshBeaconChannelOptions();
        var w = new BeaconSettingsWindow { DataContext = viewModel };
        w.Show(owner);
        return w;
    }
}
