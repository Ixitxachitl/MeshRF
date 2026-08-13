// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Display name for a <see cref="RadioDeviceKind"/> in the RX/TX pickers.
///
/// The enum member is <c>Null</c> because its value is part of the C ABI and
/// mirrors <c>mrf::hal::DeviceKind</c> — renaming it would mean renaming the
/// native side too, and "Null" is the right word there. It is the wrong word in
/// a dropdown, where it means "no radio", so it is relabelled at the point of
/// display only. The persisted setting still stores the enum name, so existing
/// settings.json files keep working.
/// </summary>
public sealed class RadioDeviceKindLabelConverter : IValueConverter
{
    public static readonly RadioDeviceKindLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RadioDeviceKind kind ? Label(kind) : value?.ToString();

    /// <summary>One-way only: the ComboBox binds SelectedItem to the enum
    /// itself, so nothing ever converts a label back.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Label(RadioDeviceKind kind) => kind switch
    {
        RadioDeviceKind.Null => "None",
        _ => kind.ToString(),
    };
}
