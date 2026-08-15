// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// A single-line text prompt, built in code like <see cref="ConfirmDialog"/>.
/// Used where a name has to be collected before something is created — naming a
/// new script, for instance, whose file name is its identity.
/// </summary>
public sealed class TextPromptDialog : Window
{
    private readonly TextBox _input;
    private bool _accepted;

    private TextPromptDialog(string title, string message, string initialText, string confirmText)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));

        _input = new TextBox { Text = initialText, Margin = new Thickness(0, 0, 0, 16) };

        var confirm = new Button { Content = confirmText, MinWidth = 88, IsDefault = true };
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
                        Margin = new Thickness(0, 0, 0, 8),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _input,
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

        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>Shows the prompt and returns what was typed, or null if the user
    /// cancelled or left it blank.</summary>
    public static async Task<string?> PromptAsync(
        Window owner, string title, string message, string initialText = "", string confirmText = "Create")
    {
        var dialog = new TextPromptDialog(title, message, initialText, confirmText);
        await dialog.ShowDialog(owner);
        if (!dialog._accepted) return null;
        var text = dialog._input.Text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
