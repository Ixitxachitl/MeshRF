// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Labels frequency slot 0 as "Auto (n)" in the slot picker, where n is the slot
/// Auto currently resolves to.
///
/// Zero is firmware's own sentinel for "no slot chosen" (<c>channel_num == 0</c>
/// in Config.LoRaConfig), which is why it is the stored value rather than a
/// separate flag: on that setting the frequency follows the region, preset and
/// primary channel name, exactly as a real node's does. Naming the resolved slot
/// inline keeps that from being invisible — the picker says where Auto has
/// landed without the user having to pin it to find out. Slots the user picks
/// are 1-based and shown as themselves.
/// </summary>
/// <remarks>
/// Multi-value because the resolved slot is view-model state, not a property of
/// the item: values[0] is the slot being rendered, values[1] the view model's
/// current <c>AutoResolvedSlot</c>.
/// </remarks>
public sealed class SlotLabelConverter : IMultiValueConverter
{
    public static readonly SlotLabelConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not int slot) return null;
        if (slot != 0) return slot.ToString(culture);
        // The resolved slot is unavailable for the moment between the template
        // binding and the view model reporting it; "Auto" alone is still true.
        return values.Count > 1 && values[1] is int resolved
            ? string.Create(culture, $"Auto ({resolved})")
            : "Auto";
    }
}
