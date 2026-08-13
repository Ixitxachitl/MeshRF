// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Renders a DateTime through <see cref="UiFormats"/> instead of a literal
/// StringFormat, so every grid column follows the unit system's date
/// convention (metric = European day-first, 24-hour). Reads the pattern at
/// convert time: rows rendered after a unit switch pick up the new form
/// without any rebinding.
/// </summary>
public sealed class UiStampConverter : IValueConverter
{
    public static readonly UiStampConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTime dt => UiFormats.Stamp(dt),
        DateTimeOffset dto => UiFormats.Stamp(dto.LocalDateTime),
        null => string.Empty,
        _ => value.ToString(),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
