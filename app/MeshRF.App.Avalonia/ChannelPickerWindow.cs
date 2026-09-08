// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeshRF.Channels;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Asks which channel — or which open DM peer — an outgoing payload should go
/// to. Avalonia counterpart of MeshRF.App's ChannelPickerWindow, built in code
/// since it's a single combo and two buttons. Used by the map's send-waypoint
/// gesture and by the quick-send bar.
/// </summary>
public sealed class ChannelPickerWindow : Window
{
    /// <summary>One selectable destination: a broadcast channel, or an
    /// already-open DM conversation peer.</summary>
    private sealed class PickerEntry
    {
        public string DisplayName { get; init; } = string.Empty;
        public ChannelConfig? Channel { get; init; }
        public uint? DmNodeNum { get; init; }
        public override string ToString() => DisplayName;
    }

    private readonly ComboBox _combo;
    private bool _accepted;

    private ChannelPickerWindow(IReadOnlyList<PickerEntry> entries, PickerEntry? preferred, string prompt)
    {
        Title = "Choose channel";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));

        _combo = new ComboBox
        {
            ItemsSource = entries,
            SelectedItem = preferred,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var ok = new Button { Content = "OK", MinWidth = 88, IsDefault = true };
        ok.Click += (_, _) => { _accepted = true; Close(); };

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
                        Text = prompt,
                        Margin = new Thickness(0, 0, 0, 8),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _combo,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { ok, cancel },
                    },
                },
            },
        };
    }

    /// <summary>Shows the picker. Returns null when cancelled or when there is
    /// nowhere to send.</summary>
    public static Task<(ChannelConfig? Channel, uint? DmNodeNum)?> PickAsync(
        Window owner, RadioViewModel vm, string prompt = "Send waypoint on which channel?") =>
        PickAsync(owner, vm, prompt, includeOpenDms: true);

    /// <summary>Shows the picker offering channels alone, and returns the one
    /// chosen or null when cancelled.</summary>
    /// <remarks>For a payload whose destination is already settled and where
    /// only the channel to reach it on is in question: an open DM names a node,
    /// which is not an answer to that.</remarks>
    /// <param name="mesh">The channel list to keep the offer to, or null for
    /// every mesh. A request to a node is sent on the settings that node was
    /// heard on, so only that mesh's keys can reach it; offering the others
    /// asks the operator to pick something that cannot work.</param>
    public static async Task<ChannelConfig?> PickChannelAsync(
        Window owner, RadioViewModel vm, string prompt, string? mesh = null) =>
        (await PickAsync(owner, vm, prompt, includeOpenDms: false, mesh))?.Channel;

    private static async Task<(ChannelConfig? Channel, uint? DmNodeNum)?> PickAsync(
        Window owner, RadioViewModel vm, string prompt, bool includeOpenDms, string? mesh = null)
    {
        // A disabled channel has no key and no hash on the air, so it isn't a
        // place anything can be sent.
        var channels = vm.Tabs.OfType<ChannelTabViewModel>()
            .Where(c => !c.Config.IsDisabled)
            .Where(c => mesh is null || c.Config.Preset == mesh)
            .ToList();
        var openDms = includeOpenDms
            ? vm.Tabs.OfType<ConversationTabViewModel>().ToList()
            : [];
        if (channels.Count == 0 && openDms.Count == 0)
        {
            vm.StatusText = mesh is null
                ? "No channel to send on."
                : $"No enabled channel on {mesh} to send on.";
            return null;
        }

        // Two meshes can each hold a channel of the same name, so an offer
        // spanning more than one says which mesh each entry is on. Scoped to
        // one, the mesh is not in question and the name stands alone.
        bool nameTheMesh = channels.Select(c => c.Config.Preset).Distinct().Count() > 1;
        var entries = channels
            .Select(c => new PickerEntry
            {
                DisplayName = nameTheMesh ? $"{c.Config.Preset}: {c.DisplayName}" : c.DisplayName,
                Channel = c.Config,
            })
            .Concat(openDms.Select(d => new PickerEntry
            {
                DisplayName = $"DM: {d.TabHeader}",
                DmNodeNum = d.NodeNum,
            }))
            .ToList();

        // Prefer the channel tab the user is looking at, else the primary of
        // what is on offer. Matched by mesh as well as index: every list
        // numbers its channels from zero, so an index alone names one channel
        // per mesh rather than one channel.
        var selectedConfig = (vm.SelectedTab as ChannelTabViewModel)?.Config;
        var preferredConfig = channels.FirstOrDefault(c => c.Config == selectedConfig)?.Config
            ?? channels.FirstOrDefault(c => c.Config.Role == ChannelRole.Primary)?.Config
            ?? channels.FirstOrDefault()?.Config;

        var preferred = entries.FirstOrDefault(e => e.Channel == preferredConfig)
                        ?? entries.FirstOrDefault();

        var w = new ChannelPickerWindow(entries, preferred, prompt);
        await w.ShowDialog(owner);
        if (!w._accepted || w._combo.SelectedItem is not PickerEntry picked) return null;

        // DMs still ride a channel's PSK even though they're unicast, so
        // resolve one when the user picked a DM entry (which has no config).
        // It comes from the peer's own mesh, which is where the frame goes out.
        var channel = picked.Channel
            ?? (picked.DmNodeNum is { } dm ? vm.ChannelForNode(dm) : null);

        return (channel, picked.DmNodeNum);
    }
}
