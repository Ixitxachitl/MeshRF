// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshRF.Scripting;

namespace MeshRF.AvaloniaApp;

/// <summary>One row in the Scripts list.</summary>
public sealed partial class ScriptListItem : ObservableObject
{
    private readonly Action<ScriptListItem, bool> _onEnabledChanged;

    public ScriptListItem(ScriptFile file, Action<ScriptListItem, bool> onEnabledChanged)
    {
        _onEnabledChanged = onEnabledChanged;
        FileName = file.FileName;
        FullPath = file.FullPath;
        Text = file.Text;
        _enabled = file.Enabled;
        Apply(file);
    }

    public string FileName { get; }
    public string FullPath { get; }

    /// <summary>The file's contents as last read or saved. The editor works on
    /// its own copy, so this is what Revert restores to.</summary>
    public string Text { get; private set; }

    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _statusGlyph = string.Empty;
    [ObservableProperty] private string _statusTip = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _hasWarning;

    /// <summary>Whether this file is a feed sync rather than a script — only a
    /// sync has a memory of what it has placed, so only a sync can be
    /// resynced.</summary>
    [ObservableProperty] private bool _isSync;

    /// <summary>Bound to the row's checkbox. Writing it rewrites just the
    /// <c>enabled:</c> line in the file (see
    /// <see cref="ScriptLibrary.SetEnabled"/>), so comments survive.</summary>
    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value) => _onEnabledChanged(this, value);

    /// <summary>Refreshes the row from a freshly read/parsed file.</summary>
    public void Apply(ScriptFile file)
    {
        Text = file.Text;
        DisplayName = file.DisplayName;

        var parse = file.Parse;
        IsSync = parse.IsSync;
        HasError = parse.HasErrors;
        HasWarning = !parse.HasErrors && parse.HasWarnings;

        if (parse.FirstError is { } error)
        {
            StatusGlyph = "✕";
            StatusTip = $"This script cannot run — {error}";
        }
        else if (HasWarning)
        {
            StatusGlyph = "!";
            int count = parse.Problems.Count(p => p.Severity == ScriptProblemSeverity.Warning);
            StatusTip = $"{count} warning{(count == 1 ? "" : "s")} — double-click to see them";
        }
        else
        {
            StatusGlyph = string.Empty;
            StatusTip = string.Empty;
        }
    }
}

/// <summary>One row in the editor's completion list.</summary>
public sealed class ScriptCompletionItem
{
    public ScriptCompletionItem(ScriptSuggestion suggestion)
    {
        Suggestion = suggestion;
        Label = suggestion.Label;
        Note = suggestion.Note;
    }

    public ScriptSuggestion Suggestion { get; }
    public string Label { get; }
    public string Note { get; }
}

/// <summary>A problem as the editor's list shows it.</summary>
public sealed class ScriptProblemItem
{
    public ScriptProblemItem(ScriptProblem problem)
    {
        Problem = problem;
        IsError = problem.Severity == ScriptProblemSeverity.Error;
        Glyph = IsError ? "✕" : "!";
        Text = problem.ToString();
    }

    public ScriptProblem Problem { get; }
    public bool IsError { get; }
    public string Glyph { get; }
    public string Text { get; }
}

/// <summary>
/// State for the Scripts window: the list of files on disk, and the one being
/// edited. Deliberately independent of <see cref="RadioViewModel"/> — nothing
/// here touches the radio, so the window can be opened, edited and closed with
/// no receive session running.
/// </summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly ScriptLibrary _library;
    private readonly IScriptRuntime? _runtime;

    public ScriptsViewModel() : this(new ScriptLibrary(), runtime: null) { }

    public ScriptsViewModel(ScriptLibrary library, IScriptRuntime? runtime = null)
    {
        _library = library;
        _runtime = runtime;
        if (_runtime is not null)
            _runtime.ScriptsStatusChanged += () => OnPropertyChanged(nameof(RuntimeStatus));
        Reload();
    }

    // ----- runtime controls ---------------------------------------------------

    /// <summary>False when the window was opened without a radio session (the
    /// render harness does this), which hides the runtime strip — the file
    /// management half of the window works perfectly well on its own.</summary>
    public bool HasRuntime => _runtime is not null;

    public bool RuntimeEnabled
    {
        get => _runtime?.ScriptsEnabled ?? false;
        set
        {
            if (_runtime is null || _runtime.ScriptsEnabled == value) return;
            _runtime.ScriptsEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RuntimeStatus));
        }
    }

    public bool RuntimeDryRun
    {
        get => _runtime?.ScriptsDryRun ?? false;
        set
        {
            if (_runtime is null || _runtime.ScriptsDryRun == value) return;
            _runtime.ScriptsDryRun = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RuntimeStatus));
        }
    }

    public string RuntimeStatus => _runtime?.ScriptsStatus ?? string.Empty;

    /// <summary>What the editor offers where a script names a channel, a node
    /// or a credential. Empty without a radio session, since there is then
    /// nothing this node knows to suggest.</summary>
    public ScriptCompletionSource Completions => _runtime?.ScriptCompletions ?? ScriptCompletionSource.Empty;

    /// <summary>The credential list the dialog edits, or null when the window
    /// was opened without a runtime.</summary>
    public List<ScriptCredential>? CredentialStore => _runtime?.ScriptCredentials;

    public void SaveCredentials() => _runtime?.SaveScriptCredentials();

    /// <summary>Tells the engine to re-read the directory. Called after any
    /// change the window makes, so an edit takes effect without restarting or
    /// reopening anything.</summary>
    private void NotifyRuntime()
    {
        _runtime?.ReloadScripts();
        OnPropertyChanged(nameof(RuntimeStatus));
    }

    public ScriptLibrary Library => _library;

    public ObservableCollection<ScriptListItem> Scripts { get; } = [];

    public ObservableCollection<ScriptProblemItem> Problems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanResync))]
    [NotifyPropertyChangedFor(nameof(CanMoveUp))]
    [NotifyPropertyChangedFor(nameof(CanMoveDown))]
    private ScriptListItem? _selectedScript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    [NotifyPropertyChangedFor(nameof(IsEditorClosed))]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    private ScriptListItem? _openScript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _hasErrors;

    [ObservableProperty] private string _validationSummary = string.Empty;

    /// <summary>Recolours the summary strip. Two bools rather than a state
    /// string so the XAML can drive style classes directly.</summary>
    [ObservableProperty] private bool _isValidationWarning;
    [ObservableProperty] private bool _isValidationError;

    [ObservableProperty] private bool _hasProblems;

    public bool IsEditorOpen => OpenScript is not null;
    public bool IsEditorClosed => OpenScript is null;

    public string EditorTitle =>
        OpenScript is null ? string.Empty : $"{OpenScript.FileName}{(IsDirty ? " •" : "")}";

    public bool CanDelete => SelectedScript is not null;

    /// <summary>A sync, and a runtime to tell about it. Editing files with no
    /// radio session is fine; there is just no engine holding a memory.</summary>
    public bool CanResync => HasRuntime && SelectedScript is { IsSync: true };

    /// <summary>
    /// Forgets what the selected feed has placed, so its next poll — now —
    /// puts every marker back.
    /// </summary>
    [RelayCommand]
    private void Resync()
    {
        if (SelectedScript is not { IsSync: true } item || _runtime is null) return;
        _runtime.ResyncFeed(item.FileName);
    }
    public bool CanMoveUp => SelectedScript is not null && Scripts.IndexOf(SelectedScript) > 0;
    public bool CanMoveDown => SelectedScript is not null && Scripts.IndexOf(SelectedScript) < Scripts.Count - 1;

    /// <summary>Save is blocked while the script has errors — an unparseable
    /// file on disk would be a script that silently never runs, so the editor
    /// refuses to create one.</summary>
    public bool CanSave => IsEditorOpen && IsDirty && !HasErrors;
    public bool CanRevert => IsEditorOpen && IsDirty;

    // ----- list ---------------------------------------------------------------

    /// <summary>Re-reads the directory, preserving the selection and the open
    /// editor by file name where those files still exist.</summary>
    public void Reload()
    {
        var selectedName = SelectedScript?.FileName;
        var openName = OpenScript?.FileName;

        Scripts.Clear();
        foreach (var file in _library.Load())
            Scripts.Add(new ScriptListItem(file, OnItemEnabledChanged));

        SelectedScript = Scripts.FirstOrDefault(s => s.FileName == selectedName);
        OpenScript = Scripts.FirstOrDefault(s => s.FileName == openName);
        RefreshMoveState();
    }

    /// <summary>Guards the checkbox's write-through while the view model is the
    /// one setting <see cref="ScriptListItem.Enabled"/> — after a save, the row
    /// is being synced *from* the file, and writing back would re-enter
    /// <see cref="ReopenFromDisk"/> and yank the caret out of the editor.</summary>
    private bool _syncingEnabled;

    private void OnItemEnabledChanged(ScriptListItem item, bool enabled)
    {
        if (_syncingEnabled) return;
        _library.SetEnabled(item.FileName, enabled);
        NotifyRuntime();
        // The toggle edits the file the editor may have open, so keep the
        // buffer honest rather than letting a later Save write the old value
        // back over it.
        if (ReferenceEquals(OpenScript, item) && !IsDirty) ReopenFromDisk(item);
    }

    private void ReopenFromDisk(ScriptListItem item)
    {
        var refreshed = _library.Load().FirstOrDefault(f => f.FileName == item.FileName);
        if (refreshed is null) return;
        item.Apply(refreshed);
        EditorTextChanged?.Invoke(refreshed.Text);
    }

    /// <summary>Raised when the buffer has to be replaced from outside the
    /// editor (revert, or an enabled-toggle rewriting the open file). The window
    /// owns the TextBox, so it applies the new text and resets the caret.</summary>
    public event Action<string>? EditorTextChanged;

    public void MoveSelected(int delta)
    {
        if (SelectedScript is null) return;
        int from = Scripts.IndexOf(SelectedScript);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Scripts.Count) return;

        Scripts.Move(from, to);
        _library.SetOrder(Scripts.Select(s => s.FileName));
        NotifyRuntime();
        RefreshMoveState();
    }

    public void RefreshMoveState()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(CanDelete));
    }

    public ScriptListItem? Create(string name)
    {
        var fileName = _library.Create(name);
        Reload();
        var created = Scripts.FirstOrDefault(s => s.FileName == fileName);
        SelectedScript = created;
        return created;
    }

    public void Delete(ScriptListItem item)
    {
        _library.Delete(item.FileName);
        if (ReferenceEquals(OpenScript, item)) CloseEditor();
        Reload();
        NotifyRuntime();
    }

    // ----- editor -------------------------------------------------------------

    public void Open(ScriptListItem item)
    {
        OpenScript = item;
        IsDirty = false;
        EditorTextChanged?.Invoke(item.Text);
        Validate(item.Text);
    }

    public void CloseEditor()
    {
        OpenScript = null;
        IsDirty = false;
        Problems.Clear();
        HasProblems = false;
        ValidationSummary = string.Empty;
        IsValidationWarning = false;
        IsValidationError = false;
    }

    public void Revert()
    {
        if (OpenScript is null) return;
        IsDirty = false;
        EditorTextChanged?.Invoke(OpenScript.Text);
        Validate(OpenScript.Text);
    }

    /// <summary>Re-parses the buffer and refreshes the problem list. Called on a
    /// short debounce as the user types, so a half-typed line doesn't flash
    /// errors on every keystroke.</summary>
    public void Validate(string text)
    {
        var result = ScriptParser.Parse(text);

        Problems.Clear();
        // Errors first, then by line — but file-wide problems (line 0) go last
        // rather than first. "the script has no trigger:" is usually a
        // consequence of the misspelled key two lines up, and the fix belongs
        // at the top of the list.
        foreach (var problem in result.Problems
                     .OrderByDescending(p => p.Severity == ScriptProblemSeverity.Error)
                     .ThenBy(p => p.Line == 0 ? int.MaxValue : p.Line))
            Problems.Add(new ScriptProblemItem(problem));

        HasErrors = result.HasErrors;
        HasProblems = Problems.Count > 0;

        int errors = result.Problems.Count(p => p.Severity == ScriptProblemSeverity.Error);
        int warnings = result.Problems.Count - errors;

        IsValidationError = errors > 0;
        IsValidationWarning = errors == 0 && warnings > 0;

        if (errors > 0)
        {
            ValidationSummary = $"{errors} problem{(errors == 1 ? "" : "s")} to fix before this script can be saved";
        }
        else if (warnings > 0)
        {
            ValidationSummary = $"Valid, with {warnings} warning{(warnings == 1 ? "" : "s")}";
        }
        else
        {
            ValidationSummary = DescribeValid(result.Script);
        }
    }

    /// <summary>A one-line plain-English summary of a valid script, so the user
    /// gets confirmation of what they just wrote rather than only silence.</summary>
    private static string DescribeValid(MeshScript? script)
    {
        if (script is null) return "Valid";

        var triggers = script.Triggers.Count;
        var actions = script.Actions.Count;
        var conditions = script.Conditions.Count;

        var summary = $"Valid — {triggers} trigger{(triggers == 1 ? "" : "s")}, " +
                      $"{conditions} condition{(conditions == 1 ? "" : "s")}, " +
                      $"{actions} action{(actions == 1 ? "" : "s")}";
        return script.Enabled ? summary : $"{summary} (disabled)";
    }

    /// <summary>Writes the buffer to disk. Returns false without writing if the
    /// script still has errors.</summary>
    public bool Save(string text)
    {
        if (OpenScript is null) return false;

        var result = ScriptParser.Parse(text);
        if (result.HasErrors)
        {
            Validate(text);
            return false;
        }

        _library.Save(OpenScript.FileName, text);
        IsDirty = false;

        // Re-read rather than trusting the buffer: the alias shown in the list
        // and the enabled toggle both come from the file, and a save may have
        // changed either.
        var saved = _library.Load().FirstOrDefault(f => f.FileName == OpenScript.FileName);
        if (saved is not null)
        {
            OpenScript.Apply(saved);
            _syncingEnabled = true;
            OpenScript.Enabled = saved.Enabled;
            _syncingEnabled = false;
        }
        Validate(text);
        NotifyRuntime();
        return true;
    }
}
