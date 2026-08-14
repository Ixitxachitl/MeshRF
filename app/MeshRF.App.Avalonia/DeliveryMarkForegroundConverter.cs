// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Colour of the trailing delivery mark on an outgoing message. The two
/// delivery stages share the check glyph, so this is the only thing that tells
/// them apart: muted while the message has merely reached the mesh, green once
/// the recipient has acknowledged it.
/// </summary>
/// <remarks>
/// Brushes are literals rather than theme resources, matching
/// <see cref="IgnoredNodeForegroundConverter"/>: the app is pinned to the dark
/// theme (App.axaml sets RequestedThemeVariant), so there is no second palette
/// to resolve against and no theme change to react to. If a light theme is
/// ever ported, both converters need revisiting together.
/// </remarks>
public sealed class DeliveryMarkForegroundConverter : IValueConverter
{
    public static readonly DeliveryMarkForegroundConverter Instance = new();

    /// <summary>Same grey as BrushSubtle: reaching the mesh is progress, not an
    /// outcome, so it should not pull the eye the way a settled state does.</summary>
    private static readonly IBrush ToMeshBrush = new SolidColorBrush(Color.Parse("#FFB0B0B0"));

    /// <summary>Lifted off the usual #4CAF50 so it stays legible against the
    /// #2A2A2A message-list background at 12px.</summary>
    private static readonly IBrush DeliveredBrush = new SolidColorBrush(Color.Parse("#FF5FD35F"));

    /// <summary>The same red the node list uses for ignored nodes, so failure
    /// reads as one colour across the app.</summary>
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#FFFF6B6B"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            MessageDelivery.DeliveredToMesh => ToMeshBrush,
            MessageDelivery.Delivered       => DeliveredBrush,
            MessageDelivery.Failed          => FailedBrush,
            // No mark is drawn in the other states, so the brush is never used.
            _ => ToMeshBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
