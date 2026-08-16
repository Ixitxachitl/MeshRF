// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// MQTT bridge settings. Every field is a two-way binding onto
/// <see cref="RadioViewModel"/>, which persists on change and reconfigures the
/// bridge — so there is no Save button.
/// </summary>
public partial class MqttSettingsWindow : Window
{
    public MqttSettingsWindow()
    {
        InitializeComponent();

        // Avalonia's ComboBox has no SelectedValuePath, so the precision
        // options are bound by hand: pick the item matching the stored bit
        // count, and write the chosen item's bits back on change.
        PrecisionCombo.SelectionChanged += (_, _) =>
        {
            if (DataContext is RadioViewModel vm && PrecisionCombo.SelectedItem is PositionPrecisionOption o)
                vm.MqttMapReportPositionPrecision = o.Bits;
        };
        DataContextChanged += (_, _) => SyncPrecisionOptions();
    }

    private void SyncPrecisionOptions()
    {
        if (DataContext is not RadioViewModel vm) return;
        var options = vm.MqttMapReportPrecisionOptions;
        PrecisionCombo.ItemsSource = options;
        PrecisionCombo.SelectedItem =
            options.FirstOrDefault(o => o.Bits == vm.MqttMapReportPositionPrecision) ?? options.FirstOrDefault();
    }
}
