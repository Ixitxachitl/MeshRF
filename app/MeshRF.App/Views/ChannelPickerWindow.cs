// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MeshRF.App.ViewModels;

namespace MeshRF.App.Views;

public sealed class ChannelPickerWindow : Window
{
    private readonly ComboBox _channelCombo;

    public ChannelViewModel? SelectedChannel => _channelCombo.SelectedItem as ChannelViewModel;

    public ChannelPickerWindow(IReadOnlyList<ChannelViewModel> channels,
                               int preferredChannelIndex,
                               string prompt)
    {
        Title = "Choose channel";
        Width = 380;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        _channelCombo = new ComboBox
        {
            ItemsSource = channels,
            DisplayMemberPath = nameof(ChannelViewModel.DisplayName),
            SelectedItem = channels.FirstOrDefault(c => c.Config.Index == preferredChannelIndex)
                           ?? channels.FirstOrDefault(),
            MinWidth = 260,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var okButton = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0),
        };
        okButton.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 88,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { okButton, cancelButton },
        };

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = prompt,
                        Margin = new Thickness(0, 0, 0, 8),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _channelCombo,
                    buttons,
                },
            },
        };
    }
}