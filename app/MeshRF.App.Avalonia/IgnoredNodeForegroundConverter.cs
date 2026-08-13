// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Node list row/cell foreground for the "ignored" state: red when the node
/// is ignored, the app's normal text color otherwise. Replaces the old
/// dedicated 🔇 column — ignored nodes now read as red text instead,
/// matching MeshRF.App's row-highlight behavior.
/// </summary>
/// <remarks>
/// Returns an explicit brush for both states rather than falling back to
/// <see cref="Avalonia.AvaloniaProperty.UnsetValue"/> on the non-ignored
/// case: this Setter lives in the DataGrid's own local Styles (closer to the
/// row/cell than the app-wide default), and once Avalonia resolves the
/// property through that local Style it does not keep searching ancestor
/// Styles collections for a fallback — an Unset value there resolves to the
/// DataGrid theme's raw default (black) rather than the app's normal color.
/// </remarks>
public sealed class IgnoredNodeForegroundConverter : IValueConverter
{
    public static readonly IgnoredNodeForegroundConverter Instance = new();

    private static readonly IBrush IgnoredBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.Parse("#FFE6E6E6"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? IgnoredBrush : NormalBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
