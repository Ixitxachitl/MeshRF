// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.Windows.Data;

namespace MeshtasticRF.App.Converters;

/// <summary>Returns the logical negation of a boolean. Used to disable a
/// control while a related toggle (e.g. AGC) is on.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
