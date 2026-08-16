// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

public sealed record WaypointEditResult(
    string Name,
    string Description,
    double Latitude,
    double Longitude,
    uint? Icon,
    uint ExpireEpoch,
    uint LockedTo,
    uint GeofenceRadius,
    double? BboxWest,
    double? BboxSouth,
    double? BboxEast,
    double? BboxNorth,
    bool NotifyOnEnter,
    bool NotifyOnExit,
    bool NotifyFavoritesOnly);

/// <summary>Modal dialog for editing a waypoint before resending it (same id)
/// over the mesh. Covers every field the map's new-waypoint composer can set:
/// icon, name, description, position, lock, expiry, circular geofence with its
/// notify flags, and the bounding box. The box is typed in as four degrees here
/// rather than picked on the map, since the dialog is modal over it.</summary>
public partial class WaypointEditWindow : Window
{
    private WaypointEditResult? _result;

    /// <summary>Set when the waypoint is locked to someone else's node: that
    /// lock is preserved rather than editable, so we carry it through save.</summary>
    private uint _lockedToOther;

    private uint _myNodeNum;

    private string _icon = string.Empty;

    public WaypointEditWindow()
    {
        InitializeComponent();
    }

    public static async Task<WaypointEditResult?> EditAsync(Window owner, WaypointRecord wp, uint myNodeNum)
    {
        var win = new WaypointEditWindow();
        win.Load(wp, myNodeNum);
        await win.ShowDialog(owner);
        return win._result;
    }

    private void Load(WaypointRecord wp, uint myNodeNum)
    {
        _myNodeNum = myNodeNum;
        _icon = wp.IconText;
        ShowIcon();

        NameBox.Text = wp.Name;
        DescriptionBox.Text = wp.Description;
        LatBox.Text = wp.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        LonBox.Text = wp.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        if (wp.LockedTo != 0 && wp.LockedTo != myNodeNum)
        {
            _lockedToOther = wp.LockedTo;
            LockToMeBox.IsChecked = false;
            LockToMeBox.IsEnabled = false;
            LockedElsewhereText.Text = $"Locked to !{wp.LockedTo:x8}; the lock is kept as-is.";
            LockedElsewhereText.IsVisible = true;
        }
        else
        {
            LockToMeBox.IsChecked = wp.LockedTo != 0;
        }

        UseExpiryBox.IsChecked = wp.HasExpiry;
        var expiry = wp.ExpireTime ?? DateTime.Now.AddDays(1).Date.AddHours(12);
        ExpiryDatePicker.SelectedDate = new DateTimeOffset(expiry.Date);
        ExpiryTimePicker.SelectedTime = expiry.TimeOfDay;

        UseGeofenceBox.IsChecked = wp.HasCircularGeofence;
        GeofenceRadiusBox.Text = wp.GeofenceRadius > 0
            ? wp.GeofenceRadius.ToString(CultureInfo.InvariantCulture)
            : "100";
        NotifyOnEnterBox.IsChecked = wp.NotifyOnEnter;
        NotifyOnExitBox.IsChecked = wp.NotifyOnExit;
        NotifyFavoritesOnlyBox.IsChecked = wp.NotifyFavoritesOnly;

        UseBoundingBoxBox.IsChecked = wp.HasBoundingBoxGeofence;
        BboxWestBox.Text = FormatDegrees(wp.BboxWest);
        BboxSouthBox.Text = FormatDegrees(wp.BboxSouth);
        BboxEastBox.Text = FormatDegrees(wp.BboxEast);
        BboxNorthBox.Text = FormatDegrees(wp.BboxNorth);
    }

    private static string FormatDegrees(double? value) =>
        value is double d ? d.ToString("F6", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Shows the chosen emoji, or a placeholder when the waypoint has
    /// no icon, so the button is still findable.</summary>
    private void ShowIcon() =>
        IconButton.Content = string.IsNullOrEmpty(_icon) ? "＋" : _icon;

    private async void OnPickIcon(object? sender, RoutedEventArgs e)
    {
        // A waypoint icon travels as a single uint32 code point, so multi-scalar
        // emoji (flags, keycaps, ZWJ sequences) can't be offered here.
        var picked = await EmojiPickerWindow.PickAsync(this, singleCodePointOnly: true);
        if (string.IsNullOrEmpty(picked)) return;
        _icon = picked;
        ShowIcon();
    }

    private void OnClearIcon(object? sender, RoutedEventArgs e)
    {
        _icon = string.Empty;
        ShowIcon();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!TryParseDegrees(LatBox.Text, -90, 90, out var lat))
        {
            ShowError("Latitude must be a number between -90 and 90.");
            return;
        }
        if (!TryParseDegrees(LonBox.Text, -180, 180, out var lon))
        {
            ShowError("Longitude must be a number between -180 and 180.");
            return;
        }

        uint geofenceRadius = 0;
        if (UseGeofenceBox.IsChecked == true)
        {
            if (!uint.TryParse(GeofenceRadiusBox.Text, NumberStyles.Integer,
                               CultureInfo.InvariantCulture, out geofenceRadius) || geofenceRadius == 0)
            {
                ShowError("Geofence radius must be a whole number of meters above 0.");
                return;
            }
        }

        double? bboxWest = null, bboxSouth = null, bboxEast = null, bboxNorth = null;
        if (UseBoundingBoxBox.IsChecked == true)
        {
            if (!TryParseDegrees(BboxWestBox.Text, -180, 180, out var west)
                || !TryParseDegrees(BboxEastBox.Text, -180, 180, out var east)
                || !TryParseDegrees(BboxSouthBox.Text, -90, 90, out var south)
                || !TryParseDegrees(BboxNorthBox.Text, -90, 90, out var north))
            {
                ShowError("Bounding box needs all four edges: longitudes between -180 and 180, latitudes between -90 and 90.");
                return;
            }
            // Normalise the way the map's corner pick does, so a box typed in
            // either order still sends west<east and south<north.
            bboxWest = Math.Min(west, east);
            bboxEast = Math.Max(west, east);
            bboxSouth = Math.Min(south, north);
            bboxNorth = Math.Max(south, north);
        }

        uint expireEpoch = WaypointRecord.NeverExpiresEpoch;
        if (UseExpiryBox.IsChecked == true)
        {
            if (ExpiryDatePicker.SelectedDate is not DateTimeOffset date)
            {
                ShowError("Pick an expiration date, or clear \"Set expiration\".");
                return;
            }
            var time = ExpiryTimePicker.SelectedTime ?? TimeSpan.Zero;
            var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local).Add(time);
            long epoch = new DateTimeOffset(local).ToUnixTimeSeconds();
            expireEpoch = epoch is > 0 and <= uint.MaxValue
                ? (uint)epoch
                : WaypointRecord.NeverExpiresEpoch;
        }

        uint lockedTo = _lockedToOther != 0
            ? _lockedToOther
            : LockToMeBox.IsChecked == true ? _myNodeNum : 0;

        // The notify flags only mean something alongside a geofence.
        bool hasGeofence = geofenceRadius > 0 || bboxWest is not null;

        _result = new WaypointEditResult(
            NameBox.Text?.Trim() ?? string.Empty,
            DescriptionBox.Text?.Trim() ?? string.Empty,
            lat, lon,
            EmojiToCodePoint(_icon),
            expireEpoch,
            lockedTo,
            geofenceRadius,
            bboxWest, bboxSouth, bboxEast, bboxNorth,
            hasGeofence && NotifyOnEnterBox.IsChecked == true,
            hasGeofence && NotifyOnExitBox.IsChecked == true,
            hasGeofence && NotifyFavoritesOnlyBox.IsChecked == true);
        Close();
    }

    private static bool TryParseDegrees(string? text, double min, double max, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && value >= min && value <= max;

    private static uint? EmojiToCodePoint(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;
        try
        {
            int cp = char.ConvertToUtf32(emoji.Trim(), 0);
            return cp > 0 ? (uint)cp : null;
        }
        catch { return null; }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
