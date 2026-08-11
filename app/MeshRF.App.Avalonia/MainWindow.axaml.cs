// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace MeshRF.AvaloniaApp;

public partial class MainWindow : Window
{
    private readonly RadioViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }
}
