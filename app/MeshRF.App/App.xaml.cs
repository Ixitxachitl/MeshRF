// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace MeshRF.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Apply persisted theme before MainWindow is constructed so it
        // picks up the right brushes on first paint.
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.Theme);
    }
}
