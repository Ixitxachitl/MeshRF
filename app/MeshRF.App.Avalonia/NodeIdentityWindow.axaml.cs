// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

/// <summary>Modal "My Node" identity/settings dialog, ported from
/// MeshRF.App's NodeIdentityWindow. Scoped to the identity/TX fields this
/// app actually uses — auto-report scheduling, GPS, weather/AQ telemetry,
/// and relay routing aren't ported yet, so those rows aren't shown.</summary>
public partial class NodeIdentityWindow : Window
{
    public NodeIdentityWindow()
    {
        InitializeComponent();
    }
}
