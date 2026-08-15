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
        RadioDeviceKind.Sx1262 => "SX1262 USB",
        _ => kind.ToString(),
    };
}

/// <summary>
/// Display name for a <see cref="Sx1262Board"/> in the board picker. The enum
/// names are bare product names because they cross the C ABI and are persisted
/// in settings.json; the labels add the detail that actually decides the
/// choice, which is what the radio puts out.
/// </summary>
public sealed class Sx1262BoardLabelConverter : IValueConverter
{
    public static readonly Sx1262BoardLabelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Sx1262Board board ? Label(board) : value?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Label(Sx1262Board board) => board switch
    {
        Sx1262Board.MeshStick => "MeshStick (22 dBm)",
        Sx1262Board.MeshToad => "MeshToad V3 (30 dBm)",
        // Phrased as an instruction, not a value: this is the state the picker
        // starts in, and it is the one thing the user has to resolve before
        // the stick will transmit.
        Sx1262Board.Unspecified => "Select your board…",
        _ => board.ToString(),
    };
}
