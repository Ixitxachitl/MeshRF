// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.App.ViewModels;

/// <summary>One row in the Tetris high-score table as broadcast on the mesh.</summary>
public sealed record TetrisHighScoreEntry(int Rank, uint NodeNum, string ShortName, uint Score);
