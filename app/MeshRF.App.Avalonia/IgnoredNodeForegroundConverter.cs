// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Node list row/cell foreground for the "ignored" state: red when the node
/// is ignored, otherwise falls through to whatever lower-priority style would
/// otherwise apply (the app's default text brush, or an icon's own explicit
/// color). Replaces the old dedicated 🔇 column — ignored nodes now read as
/// red text instead, matching MeshRF.App's row-highlight behavior.
/// </summary>
public sealed class IgnoredNodeForegroundConverter : IValueConverter
{
    public static readonly IgnoredNodeForegroundConverter Instance = new();

    private static readonly IBrush IgnoredBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? IgnoredBrush : AvaloniaProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
