// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshRF.Waypoints;

namespace MeshRF.AvaloniaApp;

public sealed record WaypointEditResult(string Name, string Description, double Latitude, double Longitude);

/// <summary>Minimal modal dialog for editing a waypoint's name/description/
/// position before resending it (same id) over the mesh. Ported from
/// MeshRF.App's WaypointEditWindow, scoped down to the fields this app's
/// WaypointRecord model actually surfaces in its list view — geofence/lock/
/// icon editing isn't exposed here yet.</summary>
public partial class WaypointEditWindow : Window
{
    private WaypointEditResult? _result;

    public WaypointEditWindow()
    {
        InitializeComponent();
    }

    public static async Task<WaypointEditResult?> EditAsync(Window owner, WaypointRecord wp)
    {
        var win = new WaypointEditWindow();
        win.NameBox.Text = wp.Name;
        win.DescriptionBox.Text = wp.Description;
        win.LatBox.Text = wp.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        win.LonBox.Text = wp.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        await win.ShowDialog(owner);
        return win._result;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(LatBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || lat < -90 || lat > 90)
        {
            ShowError("Latitude must be a number between -90 and 90.");
            return;
        }
        if (!double.TryParse(LonBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || lon < -180 || lon > 180)
        {
            ShowError("Longitude must be a number between -180 and 180.");
            return;
        }

        _result = new WaypointEditResult(NameBox.Text?.Trim() ?? string.Empty,
            DescriptionBox.Text?.Trim() ?? string.Empty, lat, lon);
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
