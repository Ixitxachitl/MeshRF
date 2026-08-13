// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace MeshRF;

/// <summary>
/// The app-wide date/time display patterns, switched by the unit system:
/// metric mode uses European conventions (day-first dates, 24-hour clock),
/// imperial keeps the original US forms. A mutable static rather than a
/// setting of its own so every renderer — Core display properties, XAML
/// converters, log stamps — reads one source of truth; the view model that
/// owns the unit system flips <see cref="European"/> when it changes.
/// </summary>
public static class UiFormats
{
    /// <summary>True in metric mode. Set by the unit-system owner; display
    /// code only reads it.</summary>
    public static bool European { get; set; }

    public static string DateTimePattern => European ? "dd/MM/yyyy HH:mm:ss" : "M/d/yyyy h:mm:ss tt";

    public static string TimePattern => European ? "HH:mm:ss" : "h:mm:ss tt";

    /// <summary>Local date + time in the current convention.</summary>
    public static string Stamp(DateTime local) =>
        local.ToString(DateTimePattern, CultureInfo.CurrentCulture);

    /// <summary>Local time of day in the current convention.</summary>
    public static string Time(DateTime local) =>
        local.ToString(TimePattern, CultureInfo.CurrentCulture);
}
