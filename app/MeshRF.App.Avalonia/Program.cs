// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace MeshRF.AvaloniaApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Before Avalonia, and before anything reads settings.json: a second
        // instance must not get far enough to write it.
        if (!SingleInstance.TryAcquire(out var raisedRunningInstance))
        {
            Console.Error.WriteLine(raisedRunningInstance
                ? "MeshRF is already running — brought its window to the front."
                : "MeshRF is already running.");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        // Inter carries no colour emoji, so emoji need a second font. Register
        // it as a font-manager fallback rather than appending it to the app-wide
        // FontFamily: a fallback is consulted per codepoint, only for codepoints
        // the primary font can't draw, so body text stays in Inter no matter
        // what the emoji font contains. Naming all three platforms' emoji fonts
        // in one FontFamily chain is what broke Linux — see
        // EmojiCatalog.PlatformFamily.
        .With(new FontManagerOptions
        {
            FontFallbacks =
            [
                new FontFallback { FontFamily = new FontFamily(EmojiCatalog.PlatformFamily) },
            ],
        })
        .LogToTrace();
}
