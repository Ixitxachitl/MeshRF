// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF;
using MeshRF.AvaloniaApp;
using MeshRF.Channels;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The per-channel settings dialog. Its hints carry the things a channel
/// cannot say for itself — the hash it matches on the air, why the finer
/// location settings are missing, what a rejected key was rejected for — so
/// they have to be readable rather than squeezed into whatever a column of
/// buttons left over.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class ChannelSettingsLayoutTests(HeadlessAvalonia avalonia)
{
    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Opens the dialog on a channel anyone can decrypt, which is the
    /// case that shows every hint at once.</summary>
    private static (Window Owner, ChannelSettingsWindow Dialog) OpenOn(ChannelConfig config)
    {
        var owner = new MainWindow { Width = 1280, Height = 900 };
        owner.Show();
        Settle();

        var vm = (RadioViewModel)owner.DataContext!;
        var tab = vm.Tabs.OfType<ChannelTabViewModel>().First();
        tab.Config.Name = config.Name;
        tab.Config.Psk = config.Psk;
        tab.Config.Role = config.Role;
        tab.Config.PositionPrecision = config.PositionPrecision;

        ChannelSettingsWindow.Open(owner, vm, tab);
        Settle();

        var dialog = owner.OwnedWindows.OfType<ChannelSettingsWindow>().Single();
        dialog.Width = 620;
        Settle();
        return (owner, dialog);
    }

    private static T Named<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>
    /// The note explaining a capped location setting is a sentence, not a
    /// label. Wedged into the third column beside four buttons it wrapped to a
    /// sliver; it now runs the width of the dialog under the control it is
    /// about.
    /// </summary>
    [Fact]
    public void TheLocationNoteIsWideEnoughToRead() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, dialog) = OpenOn(new ChannelConfig
        {
            Name = "Alta",
            Psk = new byte[] { 0x01 },   // the public default key: caps precision
            Role = ChannelRole.Secondary,
            PositionPrecision = 13,
        });

        var note = Named<TextBlock>(dialog, "PrecisionNote");
        Assert.True(note.IsVisible, "a channel on the default key should say why precision is capped");
        Assert.Contains("capped at", note.Text);

        // Wide enough that the sentence is not a column of single words, and
        // no taller than a couple of lines at that width.
        Assert.True(note.Bounds.Width >= 360,
                    $"the note is only {note.Bounds.Width:0} px wide");
        Assert.True(note.Bounds.Height <= 60,
                    $"the note wrapped to {note.Bounds.Height:0} px tall");

        dialog.Close();
        owner.Close();
    }));

    /// <summary>
    /// Nothing is clipped and nothing runs past the dialog. Every hint sits
    /// inside the window that owns it, at whatever height the content needs.
    /// </summary>
    [Fact]
    public void EveryRowFitsInsideTheDialog() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, dialog) = OpenOn(new ChannelConfig
        {
            Name = "Alta",
            Psk = new byte[] { 0x01 },
            Role = ChannelRole.Secondary,
            PositionPrecision = 13,
        });

        foreach (var name in new[] { "NameBox", "HashText", "PskBox", "PrecisionCombo", "PrecisionNote", "ListText" })
        {
            var control = Named<Control>(dialog, name);
            if (!control.IsVisible) continue;
            var origin = ((Visual)control).TranslatePoint(default, dialog) ?? default;
            Assert.True(origin.X >= 0, $"{name} starts off the left edge at {origin.X:0}");
            Assert.True(origin.X + control.Bounds.Width <= dialog.Width + 1,
                        $"{name} runs {origin.X + control.Bounds.Width - dialog.Width:0} px past the right edge");
        }

        dialog.Close();
        owner.Close();
    }));

    /// <summary>
    /// A hint that has nothing to say leaves no hole where it would have been.
    /// The rows carry their own spacing rather than the grid carrying it,
    /// because a collapsed row still takes its share of a grid's RowSpacing.
    /// </summary>
    [Fact]
    public void AHiddenHintTakesNoRoom() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, dialog) = OpenOn(new ChannelConfig
        {
            Name = "club",
            Psk = ChannelConfig.NewRandomPsk(32),   // a private key: nothing to explain
            Role = ChannelRole.Secondary,
            PositionPrecision = 32,
        });

        var note = Named<TextBlock>(dialog, "PrecisionNote");
        Assert.False(note.IsVisible);

        var combo = Named<ComboBox>(dialog, "PrecisionCombo");
        var mute = Named<CheckBox>(dialog, "MuteRtttlCheck");
        double comboBottom = (((Visual)combo).TranslatePoint(default, dialog) ?? default).Y + combo.Bounds.Height;
        double muteTop = (((Visual)mute).TranslatePoint(default, dialog) ?? default).Y;

        Assert.True(muteTop - comboBottom < 14,
                    $"the collapsed hint left a {muteTop - comboBottom:0} px gap");

        dialog.Close();
        owner.Close();
    }));

    /// <summary>The name box is the field people actually type in, so it must
    /// not be the narrowest thing on the row.</summary>
    [Fact]
    public void TheEditableFieldsGetTheRoom() => avalonia.Run(() => TempDataDirectory.With(() =>
    {
        var (owner, dialog) = OpenOn(new ChannelConfig
        {
            Name = "Alta",
            Psk = ChannelConfig.NewRandomPsk(32),
            Role = ChannelRole.Secondary,
        });

        var name = Named<TextBox>(dialog, "NameBox");
        var psk = Named<TextBox>(dialog, "PskBox");

        Assert.True(name.Bounds.Width >= 240, $"the name box is only {name.Bounds.Width:0} px wide");
        // A 32-byte key is 44 base64 characters and the box is monospaced, so
        // it has to be wide enough to read one without scrolling.
        Assert.True(psk.Bounds.Width >= 320, $"the PSK box is only {psk.Bounds.Width:0} px wide");

        dialog.Close();
        owner.Close();
    }));
}
