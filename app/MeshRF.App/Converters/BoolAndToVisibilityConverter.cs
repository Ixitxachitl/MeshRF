// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeshRF.App.Converters;

/// <summary>Visible only when every bound boolean is true. Used to gate a
/// control on more than one condition at once (e.g. "primary channel" AND
/// "feature enabled in settings"), which a single-value BooleanToVisibility
/// binding can't express.</summary>
public sealed class BoolAndToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        foreach (var v in values)
        {
            if (v is not bool b || !b) return Visibility.Collapsed;
        }
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
