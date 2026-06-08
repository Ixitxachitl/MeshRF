// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.Windows.Data;

namespace MeshRF.App.Converters;

/// <summary>Converts a bool to an Opacity value: true → 1.0, false → 0.4.
/// Used to dim labels that accompany a disabled control.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
