// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeshRF.App.ViewModels;
using MeshRF.Channels;

namespace MeshRF.App.Views;

public sealed class ChannelPickerWindow : Window
{
    private readonly ComboBox _channelCombo;

    /// <summary>One selectable destination: either a broadcast channel or an
    /// already-open DM conversation peer.</summary>
    private sealed class PickerEntry
    {
        public string DisplayName { get; init; } = string.Empty;
        public ChannelConfig? Channel { get; init; }
        public uint? DmNodeNum { get; init; }
    }

    /// <summary>The broadcast channel picked, or null when a DM peer was picked instead.</summary>
    public ChannelConfig? SelectedChannel => (_channelCombo.SelectedItem as PickerEntry)?.Channel;

    /// <summary>The DM peer's node number picked, or null when a broadcast channel was picked instead.</summary>
    public uint? SelectedDmNodeNum => (_channelCombo.SelectedItem as PickerEntry)?.DmNodeNum;

    public ChannelPickerWindow(IReadOnlyList<ChannelViewModel> channels,
                               int preferredChannelIndex,
                               string prompt,
                               IReadOnlyList<ConversationViewModel>? openDms = null)
    {
        Title = "Choose channel";
        Width = 380;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var entries = channels
            .Select(c => new PickerEntry { DisplayName = c.DisplayName, Channel = c.Config })
            .Concat((openDms ?? []).Select(convo =>
                new PickerEntry { DisplayName = $"DM: {convo.TabHeader}", DmNodeNum = convo.NodeNum }))
            .ToList();

        var preferredEntry = entries.FirstOrDefault(e => e.Channel?.Index == preferredChannelIndex)
                             ?? entries.FirstOrDefault();

        _channelCombo = new ComboBox
        {
            ItemsSource = entries,
            DisplayMemberPath = nameof(PickerEntry.DisplayName),
            SelectedItem = preferredEntry,
            MinWidth = 260,
            Margin = new Thickness(0, 0, 0, 12),
        };

        // MainWindow installs an app-wide ComboBox class handler that swallows
        // mouse-wheel scrolling unless the combo sits inside a scrollable
        // panel (added so wheel-scrolling the settings forms doesn't also
        // cycle their combo values). This picker's combo has no such
        // ancestor, so without this override the wheel would do nothing here.
        // handledEventsToo lets us still run after that class handler marks
        // the event handled.
        _channelCombo.AddHandler(PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnChannelComboWheel), handledEventsToo: true);

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

    private void OnChannelComboWheel(object sender, MouseWheelEventArgs e)
    {
        if (_channelCombo.IsDropDownOpen) return; // let the open dropdown list scroll itself
        int count = _channelCombo.Items.Count;
        if (count == 0) return;

        int newIndex = Math.Clamp(_channelCombo.SelectedIndex + (e.Delta > 0 ? -1 : 1), 0, count - 1);
        if (newIndex != _channelCombo.SelectedIndex)
            _channelCombo.SelectedIndex = newIndex;
        e.Handled = true;
    }
}
