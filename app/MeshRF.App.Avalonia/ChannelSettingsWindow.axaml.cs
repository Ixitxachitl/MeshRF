// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshRF.Channels;

namespace MeshRF.AvaloniaApp;

/// <summary>Per-channel settings dialog (name/role/PSK/location sharing/mute/
/// MQTT uplink+downlink), ported from MeshRF.App's ChannelSettingsWindow.</summary>
public partial class ChannelSettingsWindow : Window
{
    private ChannelTabViewModel? _channel;
    private RadioViewModel? _viewModel;

    /// <summary>Set while Open populates the controls. Assigning ItemsSource and
    /// the initial values raises the same change events the user's edits do, so
    /// without this the dialog would save the channel back over itself — and
    /// would do so before _channel was even assigned.</summary>
    private bool _loading;

    public ChannelSettingsWindow()
    {
        InitializeComponent();
        _loading = true;
        RoleCombo.ItemsSource = Enum.GetValues<ChannelRole>();
        PrecisionCombo.ItemsSource = DisplayUnits.BuildPositionPrecisionOptions(UnitSystem.Metric);
        _loading = false;
    }

    public static void Open(Window owner, RadioViewModel viewModel, ChannelTabViewModel channel)
    {
        var w = new ChannelSettingsWindow();
        w._loading = true;
        w._channel = channel;
        w._viewModel = viewModel;
        w.NameBox.Text = channel.Config.Name;
        w.RoleCombo.SelectedItem = channel.Config.Role;
        w.PskBox.Text = PskToText(channel.Config.Psk);
        var options = (IReadOnlyList<PositionPrecisionOption>)w.PrecisionCombo.ItemsSource!;
        w.PrecisionCombo.SelectedItem = options.FirstOrDefault(o => o.Bits == channel.Config.PositionPrecision) ?? options[0];
        w.MuteRtttlCheck.IsChecked = channel.MuteRtttl;
        w.UplinkCheck.IsChecked = channel.Config.UplinkEnabled;
        w.DownlinkCheck.IsChecked = channel.Config.DownlinkEnabled;
        w.HashText.Text = $"hash 0x{channel.Config.Hash:X2}";
        w._loading = false;
        w.Show(owner);
    }

    private static string PskToText(byte[] psk)
    {
        if (psk.Length == 0) return string.Empty;
        if (psk.Length == 1 && psk[0] == 0x00) return string.Empty;
        return Convert.ToBase64String(psk);
    }

    private static byte[]? PskFromText(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return Array.Empty<byte>();
        if (s.Equals("default", StringComparison.OrdinalIgnoreCase)) return new byte[] { 0x01 };
        if (s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) s = s["base64:".Length..];
        if (s.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)) s = s["hex:".Length..];
        try { return Convert.FromBase64String(s); } catch { }
        try { return Convert.FromHexString(s); } catch { }
        return null;
    }

    // These write a complete, valid key, so they commit immediately rather than
    // waiting for focus to leave the box.
    private void OnUseDefaultKey(object? sender, RoutedEventArgs e) { PskBox.Text = "default"; Apply(); }
    private void OnRandom16(object? sender, RoutedEventArgs e) { PskBox.Text = Convert.ToBase64String(ChannelConfig.NewRandomPsk(16)); Apply(); }
    private void OnRandom32(object? sender, RoutedEventArgs e) { PskBox.Text = Convert.ToBase64String(ChannelConfig.NewRandomPsk(32)); Apply(); }

    private void OnFieldChanged(object? sender, RoutedEventArgs e) => Apply();

    /// <summary>Commits an edit whose field never lost focus — closing the
    /// window while still typing in the name or PSK box.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Apply();
        base.OnClosing(e);
    }

    /// <summary>
    /// Writes the current control values through to the channel and saves.
    ///
    /// A rejected PSK aborts the whole save rather than just its own field: the
    /// key and the name/role travel together as one channel definition, and
    /// storing the rest against a key the user is mid-way through typing would
    /// define a channel they never asked for.
    /// </summary>
    private void Apply()
    {
        if (_loading || _channel is null || _viewModel is null) return;

        var psk = PskFromText(PskBox.Text);
        if (psk is null)
        {
            StatusText.Text = "PSK not saved — enter base64, hex, \"default\", or leave blank.";
            return;
        }

        _channel.Config.Name = NameBox.Text?.Trim() ?? string.Empty;
        if (RoleCombo.SelectedItem is ChannelRole role) _channel.Config.Role = role;
        _channel.Config.Psk = psk;
        if (PrecisionCombo.SelectedItem is PositionPrecisionOption precision)
            _channel.Config.PositionPrecision = precision.Bits;
        _channel.Config.UplinkEnabled = UplinkCheck.IsChecked == true;
        _channel.Config.DownlinkEnabled = DownlinkCheck.IsChecked == true;
        // Mute lives on the tab, not the ChannelConfig — it's a local
        // preference in settings.json rather than a mesh channel field.
        _channel.MuteRtttl = MuteRtttlCheck.IsChecked == true;

        _viewModel.SaveChannelSettings(_channel);

        // The name feeds the hash, so it can change under an edit.
        HashText.Text = $"hash 0x{_channel.Config.Hash:X2}";
        // Clears a PSK rejection once the field parses again.
        StatusText.Text = string.Empty;
    }
}
