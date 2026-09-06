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
        w.RefreshPrecisionOptions();
        w.MuteRtttlCheck.IsChecked = channel.MuteRtttl;
        w.UplinkCheck.IsChecked = channel.Config.UplinkEnabled;
        w.DownlinkCheck.IsChecked = channel.Config.DownlinkEnabled;
        w.HashText.Text = HashLabel(channel.Config);
        bool primaryList = channel.Config.Preset.Length == 0;
        w.ListText.Text = primaryList
            ? "the primary, whatever the toolbar is set to"
            : channel.Config.Preset;
        w.MqttLabel.IsVisible = primaryList;
        w.MqttRow.IsVisible = primaryList;
        w._loading = false;
        w.Show(owner);
    }

    /// <summary>
    /// Offers only the precisions this channel's key allows, and selects what
    /// it is actually set to.
    /// </summary>
    /// <remarks>
    /// A channel anyone can decrypt is a channel anyone can read a position
    /// off, so firmware caps those at PositionPrecisionPolicy.MaxOnPublicKey on
    /// the way out. Offering the finer settings anyway would let someone pick
    /// "Precise", see it saved, and believe it — the transmit path would quietly
    /// send something coarser. Better that the choice is never on the menu, and
    /// the line underneath says why.
    /// </remarks>
    private void RefreshPrecisionOptions()
    {
        if (_channel is null) return;

        bool wasLoading = _loading;
        _loading = true;

        byte ceiling = PositionPrecisionPolicy.CeilingFor(_channel.Config);
        var units = _viewModel?.CurrentUnitSystem ?? UnitSystem.Metric;
        var options = DisplayUnits.BuildPositionPrecisionOptions(units)
                                  .Where(o => o.Bits <= ceiling)
                                  .ToList();

        PrecisionCombo.ItemsSource = options;
        byte current = Math.Min(_channel.Config.PositionPrecision, ceiling);
        PrecisionCombo.SelectedItem = options.FirstOrDefault(o => o.Bits == current) ?? options[0];

        PrecisionNote.Text = ceiling < 32
            ? "Anyone can decrypt this channel, so location is capped at "
              + $"{options[^1].Label.Replace("Within ", string.Empty)}. Set a key of your own for finer sharing."
            : string.Empty;
        PrecisionNote.IsVisible = ceiling < 32;

        _loading = wasLoading;
    }

    /// <summary>Firmware's generateHash() returns -1 for a disabled channel, so
    /// there is no hash byte to show — nothing on the air can match it.</summary>
    private static string HashLabel(ChannelConfig config) =>
        config.IsDisabled ? "disabled — matches no traffic" : $"hash 0x{config.Hash:X2}";

    private static string PskToText(byte[] psk)
    {
        if (psk.Length == 0) return string.Empty;
        if (psk.Length == 1 && psk[0] == 0x00) return string.Empty;
        return Convert.ToBase64String(psk);
    }

    /// <summary>
    /// Parses the PSK box into stored PSK bytes, or null when the text can't be
    /// a PSK. <paramref name="message"/> carries the rejection reason, or a
    /// warning about a key that parsed but offers no privacy.
    ///
    /// A single stored byte is Meshtastic's shorthand for the well-known default
    /// key, and channel.proto defines it only for 0..10 ("shown to user as
    /// simple1 through 10"). Firmware range-checks nothing: <c>getKey()</c>
    /// expands any value up to 255 into the default key with its last byte
    /// bumped, so a one-byte entry yields a channel that looks configured and
    /// private while using a key published in the firmware source. We still
    /// accept it — a monitor has to be able to match whatever a real node is
    /// configured with, including out-of-spec values — but say what it is.
    /// </summary>
    private static byte[]? PskFromText(string? text, out string? message)
    {
        message = null;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0 || s.Equals("none", StringComparison.OrdinalIgnoreCase)) return Array.Empty<byte>();
        if (s.Equals("default", StringComparison.OrdinalIgnoreCase)) return new byte[] { 0x01 };

        bool explicitHex = s.StartsWith("hex:", StringComparison.OrdinalIgnoreCase);
        if (s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) s = s["base64:".Length..];
        if (explicitHex) s = s["hex:".Length..];

        // A bare 32- or 64-character hex key is also well-formed base64, and
        // decoding it that way yields 24 or 48 meaningless bytes. Nobody pastes
        // a base64 key that happens to be hex digits only, so read it as hex.
        bool looksHex = s.Length is 32 or 64 && s.All(Uri.IsHexDigit);

        byte[]? bytes = null;
        if (!explicitHex && !looksHex)
            try { bytes = Convert.FromBase64String(s); } catch { }
        if (bytes is null)
            try { bytes = Convert.FromHexString(s); } catch { }
        if (bytes is null)
        {
            message = "PSK not saved — enter base64, hex, \"default\", or leave blank.";
            return null;
        }

        switch (bytes.Length)
        {
            case 0:
            case 16:
            case 32:
                return bytes;
            // 0 is "no crypto" and 1 is what the Default key button writes.
            case 1 when bytes[0] < 2:
                return bytes;
            case 1:
                message = bytes[0] <= 10
                    ? $"Saved — shorthand {bytes[0]} is the public default key with its last byte bumped, not a private key."
                    : $"Saved — shorthand {bytes[0]} is outside the documented 0-10 range; firmware still expands it to the "
                      + "public default key with its last byte bumped, not a private key.";
                return bytes;
            case > 32:
                message = $"PSK not saved — a key can't exceed 32 bytes (got {bytes.Length}).";
                return null;
            default:
                message = $"Saved — {bytes.Length} bytes is not an AES key size; firmware zero-pads it to "
                        + $"{(bytes.Length < 16 ? 16 : 32)}, and so do we.";
                return bytes;
        }
    }

    // These write a complete, valid key, so they commit immediately rather than
    // waiting for focus to leave the box.
    /// <summary>Puts the default key in the box the way the rest of Meshtastic
    /// writes it.</summary>
    /// <remarks>
    /// It used to write the word "default", which the parser understands but
    /// which compares against nothing. AQ== — base64 of the single byte 0x01 —
    /// is what the phone and web apps show for this key, so a channel here can
    /// be checked against the same channel there by reading both. Spelling the
    /// sixteen bytes out instead would be the same key and the same channel
    /// hash, but it would not look like it beside another app.
    /// </remarks>
    private void OnUseDefaultKey(object? sender, RoutedEventArgs e)
    {
        PskBox.Text = Convert.ToBase64String(new byte[] { 0x01 });
        Apply();
    }
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

        var psk = PskFromText(PskBox.Text, out var pskMessage);
        if (psk is null)
        {
            StatusText.Text = pskMessage;
            return;
        }

        _channel.Config.Name = NameBox.Text?.Trim() ?? string.Empty;
        if (RoleCombo.SelectedItem is ChannelRole role) _channel.Config.Role = role;
        _channel.Config.Psk = psk;
        if (PrecisionCombo.SelectedItem is PositionPrecisionOption precision)
            _channel.Config.PositionPrecision = precision.Bits;
        // The key that just landed decides the ceiling, so clamp before saving
        // rather than storing a setting the transmit path would refuse anyway.
        byte ceiling = PositionPrecisionPolicy.CeilingFor(_channel.Config);
        if (_channel.Config.PositionPrecision > ceiling)
            _channel.Config.PositionPrecision = ceiling;
        _channel.Config.UplinkEnabled = UplinkCheck.IsChecked == true;
        _channel.Config.DownlinkEnabled = DownlinkCheck.IsChecked == true;
        // Mute lives on the tab, not the ChannelConfig — it's a local
        // preference in settings.json rather than a mesh channel field.
        _channel.MuteRtttl = MuteRtttlCheck.IsChecked == true;

        _viewModel.SaveChannelSettings(_channel);

        // The name feeds the hash, so it can change under an edit.
        HashText.Text = HashLabel(_channel.Config);
        // A key edit can move the ceiling either way, so the picker is rebuilt
        // against the key that was just saved.
        RefreshPrecisionOptions();
        // Carries a warning about a key that saved but offers no privacy, and
        // clears a PSK rejection once the field parses again.
        StatusText.Text = pskMessage ?? string.Empty;
    }
}
