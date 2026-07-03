// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeshRF.App.Views;

public partial class RawJsonFeedWindow : Window
{
    public RawJsonFeedWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Prime Emoji.Wpf shaping/cache so first row expansion does not pay
        // one-time initialization cost on the interaction path.
        var warmup = new Emoji.Wpf.TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Text = "warmup 🙂",
            TextWrapping = TextWrapping.Wrap,
        };
        warmup.Measure(new Size(300, double.PositiveInfinity));
    }
}
