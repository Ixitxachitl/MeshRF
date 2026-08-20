// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Left margin for the tab that starts a new group — the first conversation
/// after the channels — and none for every other tab.
/// </summary>
/// <remarks>
/// <para>The gap has to come from the <c>TabItem</c>'s own margin rather than
/// from anything inside its header. The Fluent theme draws the selected tab's
/// underline across the whole <c>TabItem</c>, ignoring its padding, so a
/// divider placed in the header is underlined along with the title — which is
/// exactly what it looked like before this. A margin moves the tab and its
/// underline together, leaving space beside them that nothing paints into.
/// </para>
/// <para>The divider is then drawn back into that space with a negative
/// margin, so it lands in the gap rather than inside the tab.</para>
/// </remarks>
public sealed class TabGroupMarginConverter : IValueConverter
{
    public static readonly TabGroupMarginConverter Instance = new();

    /// <summary>Wide enough for the rule to sit clear of the tabs either side.
    /// The rule's own negative margin is set against this.</summary>
    public const double GapWidth = 16;

    private static readonly Thickness Gap = new(GapWidth, 0, 0, 0);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Gap : default(Thickness);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
