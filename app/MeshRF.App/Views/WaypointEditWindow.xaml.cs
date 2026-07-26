// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Windows;
using MeshRF.Waypoints;

namespace MeshRF.App.Views;

/// <summary>Result of editing a waypoint: the full set of fields to resend
/// (same <see cref="WaypointRecord.WaypointId"/>, updated content).</summary>
public sealed class WaypointEditResult
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public uint? Icon { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public uint ExpireEpoch { get; init; }
    public uint LockedTo { get; init; }
    public uint GeofenceRadius { get; init; }
    public double? BboxWest { get; init; }
    public double? BboxSouth { get; init; }
    public double? BboxEast { get; init; }
    public double? BboxNorth { get; init; }
    public bool NotifyOnEnter { get; init; }
    public bool NotifyOnExit { get; init; }
    public bool NotifyFavoritesOnly { get; init; }
}

/// <summary>Modal dialog for editing an existing waypoint's fields before
/// resending it (same id) over the mesh, including redrawing its rectangular
/// geofence on an embedded map preview.</summary>
public partial class WaypointEditWindow : Window
{
    private string? _iconGlyph;
    private readonly uint _myNodeNum;

    public WaypointEditWindow(WaypointRecord wp, uint myNodeNum)
    {
        InitializeComponent();
        _myNodeNum = myNodeNum;

        ExpiryHourCombo.ItemsSource = Enumerable.Range(1, 12).Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        ExpiryMinuteCombo.ItemsSource = Enumerable.Range(0, 60).Select(m => m.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        ExpirySecondCombo.ItemsSource = Enumerable.Range(0, 60).Select(s => s.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        ExpiryMeridiemCombo.ItemsSource = new[] { "AM", "PM" };

        NameBox.Text = wp.Name;
        DescriptionBox.Text = wp.Description;
        LatitudeBox.Text = wp.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        LongitudeBox.Text = wp.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        LockToMeCheck.IsChecked = wp.LockedTo != 0;

        _iconGlyph = wp.HasIcon ? wp.IconText : null;
        IconGlyphText.Text = _iconGlyph ?? "📍";

        if (wp.HasExpiry)
        {
            var local = wp.ExpireTime ?? DateTimeOffset.FromUnixTimeSeconds(wp.ExpireEpoch).LocalDateTime;
            UseExpiryCheck.IsChecked = true;
            ExpiryDatePicker.SelectedDate = local.Date;
            int hour12 = ((local.Hour + 11) % 12) + 1;
            ExpiryHourCombo.Text = hour12.ToString("00", CultureInfo.InvariantCulture);
            ExpiryMinuteCombo.Text = local.Minute.ToString("00", CultureInfo.InvariantCulture);
            ExpirySecondCombo.Text = local.Second.ToString("00", CultureInfo.InvariantCulture);
            ExpiryMeridiemCombo.SelectedItem = local.Hour >= 12 ? "PM" : "AM";
        }
        else
        {
            var soon = DateTime.Now.AddHours(1);
            ExpiryDatePicker.SelectedDate = soon.Date;
            ExpiryHourCombo.Text = ((soon.Hour + 11) % 12 + 1).ToString("00", CultureInfo.InvariantCulture);
            ExpiryMinuteCombo.Text = soon.Minute.ToString("00", CultureInfo.InvariantCulture);
            ExpirySecondCombo.Text = soon.Second.ToString("00", CultureInfo.InvariantCulture);
            ExpiryMeridiemCombo.SelectedItem = soon.Hour >= 12 ? "PM" : "AM";
        }
        ExpiryPanel.IsEnabled = UseExpiryCheck.IsChecked == true;

        if (wp.GeofenceRadius > 0)
        {
            UseGeofenceCheck.IsChecked = true;
            GeofenceRadiusBox.Text = wp.GeofenceRadius.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            GeofenceRadiusBox.Text = "100";
        }
        GeofenceRadiusPanel.IsEnabled = UseGeofenceCheck.IsChecked == true;

        BboxPreview.Initialize(wp.Latitude, wp.Longitude, wp.BboxWest, wp.BboxSouth, wp.BboxEast, wp.BboxNorth);
        BboxPreview.BoundingBoxChanged += (_, _) =>
            NotifyPanel.IsEnabled = UseGeofenceCheck.IsChecked == true || BboxPreview.BboxWest is not null;

        NotifyOnEnterCheck.IsChecked = wp.NotifyOnEnter;
        NotifyOnExitCheck.IsChecked = wp.NotifyOnExit;
        NotifyFavoritesOnlyCheck.IsChecked = wp.NotifyFavoritesOnly;
        bool hasAnyGeofence = wp.GeofenceRadius > 0 || BboxPreview.BboxWest is not null;
        NotifyPanel.IsEnabled = hasAnyGeofence;
    }

    public WaypointEditResult? Result { get; private set; }

    /// <summary>Shows the edit dialog for <paramref name="wp"/>; returns the
    /// edited fields, or null if the user cancelled.</summary>
    public static WaypointEditResult? Edit(Window? owner, WaypointRecord wp, uint myNodeNum)
    {
        var dlg = new WaypointEditWindow(wp, myNodeNum) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void OnPickIconClick(object sender, RoutedEventArgs e)
    {
        string? picked = EmojiPickerWindow.PickEmoji(this, EmojiPickerWindow.EmojiPickerMode.WaypointIcon);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            _iconGlyph = picked.Trim();
            IconGlyphText.Text = _iconGlyph;
        }
    }

    private void OnExpiryToggled(object sender, RoutedEventArgs e) =>
        ExpiryPanel.IsEnabled = UseExpiryCheck.IsChecked == true;

    private void OnGeofenceToggled(object sender, RoutedEventArgs e)
    {
        GeofenceRadiusPanel.IsEnabled = UseGeofenceCheck.IsChecked == true;
        bool hasAnyGeofence = UseGeofenceCheck.IsChecked == true || BboxPreview.BboxWest is not null;
        NotifyPanel.IsEnabled = hasAnyGeofence;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(LatitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
            lat is < -90 or > 90)
        {
            MessageBox.Show(this, "Latitude must be a number between -90 and 90.", "Invalid latitude",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(LongitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) ||
            lon is < -180 or > 180)
        {
            MessageBox.Show(this, "Longitude must be a number between -180 and 180.", "Invalid longitude",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // No expiration: mirror the official app's Int.MAX_VALUE sentinel (see
        // WaypointRecord.NeverExpiresEpoch) rather than 0.
        uint expireEpoch = WaypointRecord.NeverExpiresEpoch;
        if (UseExpiryCheck.IsChecked == true)
        {
            if (!TryBuildExpiryEpoch(out expireEpoch))
            {
                MessageBox.Show(this, "Enter a valid expiration date/time.", "Invalid expiration",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        uint geofenceRadius = 0;
        if (UseGeofenceCheck.IsChecked == true)
        {
            if (!uint.TryParse(GeofenceRadiusBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out geofenceRadius) ||
                geofenceRadius == 0)
            {
                MessageBox.Show(this, "Geofence radius must be a positive whole number of meters.", "Invalid radius",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        double? bboxWest = BboxPreview.BboxWest;
        double? bboxSouth = BboxPreview.BboxSouth;
        double? bboxEast = BboxPreview.BboxEast;
        double? bboxNorth = BboxPreview.BboxNorth;

        bool hasGeofence = geofenceRadius > 0 || bboxWest is not null;

        Result = new WaypointEditResult
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Waypoint" : NameBox.Text.Trim(),
            Description = DescriptionBox.Text?.Trim() ?? string.Empty,
            Icon = EmojiToCodePoint(_iconGlyph),
            Latitude = lat,
            Longitude = lon,
            ExpireEpoch = expireEpoch,
            LockedTo = LockToMeCheck.IsChecked == true ? _myNodeNum : 0,
            GeofenceRadius = geofenceRadius,
            BboxWest = bboxWest,
            BboxSouth = bboxSouth,
            BboxEast = bboxEast,
            BboxNorth = bboxNorth,
            NotifyOnEnter = hasGeofence && NotifyOnEnterCheck.IsChecked == true,
            NotifyOnExit = hasGeofence && NotifyOnExitCheck.IsChecked == true,
            NotifyFavoritesOnly = hasGeofence && NotifyFavoritesOnlyCheck.IsChecked == true,
        };
        DialogResult = true;
    }

    private bool TryBuildExpiryEpoch(out uint epoch)
    {
        epoch = 0;
        if (ExpiryDatePicker.SelectedDate is not DateTime date) return false;
        if (!int.TryParse(ExpiryHourCombo.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int hour12) ||
            hour12 is < 1 or > 12)
            return false;
        if (!int.TryParse(ExpiryMinuteCombo.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int minute) ||
            minute is < 0 or > 59)
            return false;
        if (!int.TryParse(ExpirySecondCombo.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int second) ||
            second is < 0 or > 59)
            return false;

        bool isPm;
        if (string.Equals(ExpiryMeridiemCombo.SelectedItem as string, "PM", StringComparison.OrdinalIgnoreCase))
            isPm = true;
        else if (string.Equals(ExpiryMeridiemCombo.SelectedItem as string, "AM", StringComparison.OrdinalIgnoreCase))
            isPm = false;
        else
            return false;

        int hour24 = hour12 % 12;
        if (isPm) hour24 += 12;

        var local = new DateTime(date.Year, date.Month, date.Day, hour24, minute, second, DateTimeKind.Local);
        epoch = (uint)new DateTimeOffset(local).ToUnixTimeSeconds();
        return true;
    }

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
}
