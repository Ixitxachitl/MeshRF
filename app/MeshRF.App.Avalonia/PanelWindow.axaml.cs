// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// The window one of the main window's six panels moves into when it is
/// popped out. It owns no state of the panel's own: the controls it shows are
/// the very same instances the main window built, moved across, so the panel
/// keeps its selection, scroll position and live data through the move.
/// </summary>
/// <remarks>
/// Closing docks the panel back rather than destroying it, which is why the
/// window cancels its own close. <see cref="AllowClose"/> is the way out of
/// that, used when the app is shutting down and there is nothing to dock back
/// into.
/// </remarks>
public partial class PanelWindow : Window
{
    public PanelWindow()
    {
        InitializeComponent();
    }

    /// <summary>Which panel this window is showing — the key its geometry is
    /// stored under. See MainWindow.Panels.cs.</summary>
    public string PanelKey { get; init; } = string.Empty;

    /// <summary>Set while the app is closing down, so the window closes for
    /// real instead of handing its panel back to a window that is going away.
    /// </summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when the user closes the window, meaning the panel
    /// should go back where it came from.</summary>
    public event EventHandler? DockBackRequested;

    /// <summary>Restores the window's remembered geometry, guarding against a
    /// position on a monitor that is no longer attached.</summary>
    public void ApplyBounds(PanelWindowSettings state)
    {
        if (state.Width is double w && w > 0) Width = Math.Max(MinWidth, w);
        if (state.Height is double h && h > 0) Height = Math.Max(MinHeight, h);

        if (state.Left is double left && state.Top is double top && IsOnSomeScreen(left, top))
        {
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(top));
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        WindowState = string.Equals(state.WindowState, nameof(Avalonia.Controls.WindowState.Maximized),
                                    StringComparison.OrdinalIgnoreCase)
            ? Avalonia.Controls.WindowState.Maximized
            : Avalonia.Controls.WindowState.Normal;
    }

    /// <summary>Reads the window's geometry back out onto its settings entry.
    /// Only recorded while the window is in its normal state, since maximized
    /// Width/Height say nothing about where un-maximizing would put it.
    /// </summary>
    public void CaptureBounds(PanelWindowSettings state)
    {
        if (WindowState == Avalonia.Controls.WindowState.Normal)
        {
            state.Left = Position.X;
            state.Top = Position.Y;
            state.Width = Width;
            state.Height = Height;
        }
        state.WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? nameof(Avalonia.Controls.WindowState.Maximized)
            : nameof(Avalonia.Controls.WindowState.Normal);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (AllowClose) return;

        e.Cancel = true;
        DockBackRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsOnSomeScreen(double left, double top)
    {
        var all = Screens?.All;
        if (all is null || all.Count == 0) return false;

        // Enough of the title bar has to land on a screen to be grabbable —
        // the same test the main window's geometry restore makes.
        foreach (var screen in all)
        {
            var b = screen.Bounds;
            if (left + 80 > b.X && top + 80 > b.Y &&
                left < b.X + b.Width - 40 && top < b.Y + b.Height - 40)
                return true;
        }
        return false;
    }
}
