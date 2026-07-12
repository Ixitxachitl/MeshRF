// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.App.ViewModels;

/// <summary>One row in the Chirpy Runner high-score table as broadcast on the mesh.</summary>
public sealed record ChirpyRunnerHighScoreEntry(int Rank, uint NodeNum, string ShortName, uint Score, uint ScoreId = 0);
