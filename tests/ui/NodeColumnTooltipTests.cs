// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using MeshRF.AvaloniaApp;
using Xunit;

namespace MeshRF.UiTests;

/// <summary>
/// The node table is a wide row of short headings — 🔑, 🐇, SNR, Heard on —
/// and what each holds, where it comes from and what a blank one means is not
/// something a heading has room to say. Every column carries a tooltip that
/// does, so nothing in the table has to be guessed at.
/// </summary>
[Collection(HeadlessAvalonia.CollectionName)]
public class NodeColumnTooltipTests(HeadlessAvalonia avalonia)
{
    [Fact]
    public void EveryNodeColumnSaysWhatItHolds() =>
        TempDataDirectory.With(() => avalonia.Run(() =>
        {
            var window = new MainWindow { Width = 1280, Height = 900 };
            window.Show();
            for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();

            var grid = window.FindControl<DataGrid>("NodesGridProxy");
            Assert.True(grid is not null, "no node table in the window");

            // A header given as a bare string cannot carry one: the tooltip
            // lives on a control, which is why these are templated headers.
            var silent = grid!.Columns
                .Where(c => c.Header is not Control header
                            || ToolTip.GetTip(header) is not string { Length: > 0 })
                .Select(c => c.Header is TextBlock t ? t.Text ?? c.SortMemberPath : c.SortMemberPath)
                .ToList();
            int columns = grid.Columns.Count;

            window.Close();
            for (int i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();

            Assert.True(columns > 10, $"only {columns} node columns were found");
            Assert.Empty(silent);
        }));
}
