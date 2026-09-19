// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The Save IQ button in the last-packet panel's header.
///
/// It sits on the strip that collapses the panel, so a click has to reach the
/// button and stop there; and with nothing decoded it has to say so rather
/// than open a file picker over an empty capture.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class LastPacketIqExportTests(HeadlessAvalonia avalonia)
{
    [Fact]
    public void SavingIqWithNothingDecodedSaysSoRatherThanAskingForAPath()
    {
        TempDataDirectory.With(() => avalonia.Run(() =>
        {
            var window = Station();
            var vm = (RadioViewModel)window.DataContext!;

            Click(window, Button(window));
            Settle();

            Assert.Contains("No packet IQ", vm.StatusText);

            window.Close();
            Settle();
        }));
    }

    /// <summary>The header strip collapses the panel wherever it is clicked.
    /// The button is part of that strip, so a save must not fold the
    /// spectrogram away under it.</summary>
    [Fact]
    public void SavingIqLeavesThePanelOpen()
    {
        TempDataDirectory.With(() => avalonia.Run(() =>
        {
            var window = Station();
            var panel = window.FindControl<WaterfallView>("LastPacket");
            Assert.NotNull(panel);
            Assert.True(panel!.IsVisible);

            Click(window, Button(window));
            Settle();

            Assert.True(panel.IsVisible, "the save click collapsed the panel it lives in");

            window.Close();
            Settle();
        }));
    }

    /// <summary>An SDR station, so the spectrum stack — and the panel the
    /// button rides in — is on screen at all.</summary>
    private static MainWindow Station()
    {
        var window = new MainWindow { Width = 1280, Height = 900 };
        window.Show();
        Settle();

        var vm = (RadioViewModel)window.DataContext!;
        vm.SelectedDevice = RadioDeviceKind.HackRf;
        Settle();
        return window;
    }

    private static Button Button(MainWindow window)
    {
        var button = window.FindControl<Button>("LastPacketSaveIq");
        Assert.True(button is not null, "no Save IQ button in the last-packet header");
        return button!;
    }

    /// <summary>A real pointer press and release, not a raised Click: whether
    /// the header strip below sees the same release is the point.</summary>
    private static void Click(Window window, Control target)
    {
        var center = target.TranslatePoint(
            new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window);
        Assert.True(center.HasValue, "the Save IQ button is not laid out");
        window.MouseDown(center!.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }
}
