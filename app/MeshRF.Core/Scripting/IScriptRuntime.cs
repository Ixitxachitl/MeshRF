// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshRF.Scripting;

/// <summary>
/// The running engine, as the Scripts window sees it.
/// </summary>
/// <remarks>
/// An interface rather than a direct reference to the view model so the Scripts
/// window stays openable without a radio session — it edits files on disk, and
/// only these three controls need anything live. When no runtime is attached
/// the window hides them.
/// </remarks>
public interface IScriptRuntime
{
    /// <summary>Master switch. Off by default: enabling scripts is a decision
    /// to let the app transmit unattended, and it should be made deliberately.</summary>
    bool ScriptsEnabled { get; set; }

    /// <summary>Evaluate and log everything, but transmit nothing. The way to
    /// develop a script without keying up.</summary>
    bool ScriptsDryRun { get; set; }

    /// <summary>One line for the window: how many scripts are armed and how
    /// busy they have been.</summary>
    string ScriptsStatus { get; }

    /// <summary>Raised when <see cref="ScriptsStatus"/> changes.</summary>
    event Action? ScriptsStatusChanged;

    /// <summary>Re-read the scripts directory. Called when the Scripts window
    /// changes anything.</summary>
    void ReloadScripts();

    /// <summary>
    /// Forgets what one feed sync has placed, so its next poll — brought
    /// forward to now — treats every record as new and puts the markers back.
    /// </summary>
    /// <remarks>
    /// For a memory that has drifted from what is actually on the maps in a
    /// way nothing can detect: markers cleared on the receiving nodes, or a
    /// feed whose ids changed under it. Markers deleted from this node's own
    /// list are noticed without asking.
    /// </remarks>
    void ResyncFeed(string fileName);

    /// <summary>API keys scripts authenticate with, for the Credentials dialog
    /// to edit. Lives with the runtime rather than the library because the
    /// values are protected in settings, not in the script files.</summary>
    List<ScriptCredential> ScriptCredentials { get; }

    /// <summary>Persists credential edits.</summary>
    void SaveScriptCredentials();

    /// <summary>
    /// The channels and nodes this radio knows about, for the editor to offer
    /// where a script names one.
    /// </summary>
    /// <remarks>
    /// Read on each keystroke that opens the list rather than cached, since a
    /// node heard while the window is open should be offerable straight away.
    /// Without a runtime the window edits files with no radio behind them, and
    /// there is nothing to suggest — hence
    /// <see cref="ScriptCompletionSource.Empty"/>.
    /// </remarks>
    ScriptCompletionSource ScriptCompletions { get; }
}
