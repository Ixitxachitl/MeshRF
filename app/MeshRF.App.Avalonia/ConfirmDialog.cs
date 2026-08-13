// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// A yes/no confirmation prompt, built in code like <see cref="ChannelPickerWindow"/>
/// since it's just a message and two buttons. Used before destructive actions
/// (deleting nodes/waypoints) so a stray click or keypress can't silently
/// remove something.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private bool _accepted;

    private ConfirmDialog(string title, string message, string confirmText)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));

        var confirm = new Button
        {
            Content = confirmText,
            MinWidth = 88,
            IsDefault = true,
            Background = new SolidColorBrush(Color.Parse("#FF6B6B")),
            Foreground = Brushes.White,
        };
        confirm.Click += (_, _) => { _accepted = true; Close(); };

        var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        Margin = new Thickness(0, 0, 0, 16),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { confirm, cancel },
                    },
                },
            },
        };
    }

    /// <summary>Shows the prompt and waits for the user's choice. Returns true
    /// only if the confirm button was clicked (Escape/Cancel/closing all count
    /// as declining).</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message, string confirmText = "Delete")
    {
        var w = new ConfirmDialog(title, message, confirmText);
        await w.ShowDialog(owner);
        return w._accepted;
    }
}
