// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshRF.Scripting;

namespace MeshRF.AvaloniaApp;

/// <summary>
/// Manages the automation scripts under %APPDATA%\MeshRF\scripts: one YAML file
/// each, listed in the order they run, with an embedded editor that refuses to
/// save a script it can't parse.
/// </summary>
public partial class ScriptsWindow : Window
{
    /// <summary>YAML forbids tabs for indentation, and this is the width the
    /// editor uses everywhere it inserts or removes one level.</summary>
    private const string Indent = "  ";

    /// <summary>Sections whose value is a list, so pressing Enter after one
    /// offers the first "- " for free.</summary>
    private static readonly string[] ListSections = ["trigger", "condition", "action"];

    private readonly ScriptsViewModel _model;
    private readonly DispatcherTimer _validateTimer;

    /// <summary>Set while the code-behind is replacing the buffer, so the
    /// tab-stripping pass in <see cref="OnEditorTextChanged"/> cannot re-enter
    /// itself.</summary>
    private bool _suppressTextChanged;

    private ScriptHelpWindow? _helpWindow;

    public ScriptsWindow() : this(new ScriptLibrary(), runtime: null) { }

    /// <summary>Opens against a live engine, so the runtime strip can arm it and
    /// every edit reloads it.</summary>
    public ScriptsWindow(IScriptRuntime runtime) : this(new ScriptLibrary(), runtime) { }

    /// <summary>Overridable library so the window can be pointed at a scratch
    /// directory for rendering checks, instead of the user's real scripts.</summary>
    public ScriptsWindow(ScriptLibrary library, IScriptRuntime? runtime = null)
    {
        _model = new ScriptsViewModel(library, runtime);
        InitializeComponent();
        DataContext = _model;

        // Debounced: parsing on every keystroke would flash errors at someone
        // halfway through typing a line they haven't finished yet.
        _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _validateTimer.Tick += (_, _) =>
        {
            _validateTimer.Stop();
            _model.Validate(Editor.Text ?? string.Empty);
        };

        _model.EditorTextChanged += text =>
        {
            _suppressTextChanged = true;
            Editor.Text = text;
            _suppressTextChanged = false;
            Editor.CaretIndex = 0;
            CloseCompletion();
        };

        // A list hanging over an editor nobody is typing in is just a panel in
        // the way.
        Editor.LostFocus += (_, _) => CloseCompletion();

        // Both on the tunnel, so they run before the controls under them act:
        // the TextBox would otherwise swallow the arrow keys, and a ListBoxItem
        // would take focus off the editor on the way to being clicked.
        Editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDownPreview, RoutingStrategies.Tunnel);
        CompletionList.AddHandler(
            InputElement.PointerPressedEvent, OnCompletionPointerPressed, RoutingStrategies.Tunnel);

        Closing += OnWindowClosing;
    }

    // ----- list actions --------------------------------------------------------

    private async void OnNew(object? sender, RoutedEventArgs e)
    {
        var name = await TextPromptDialog.PromptAsync(this, "New script",
            "Name for the new script. It becomes the file name, and is how the script is identified.",
            "my-script");
        if (name is null) return;

        var created = _model.Create(name);
        if (created is not null) OpenScript(created);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_model.SelectedScript is not { } item) return;

        if (!await ConfirmDialog.ConfirmAsync(this, "Delete script",
                $"Delete {item.FileName}? This removes the file from the scripts folder and cannot be undone."))
            return;

        _model.Delete(item);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object? sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (_model.SelectedScript is not { } moved) return;
        _model.MoveSelected(delta);
        // Moving rebuilds nothing, but the ListBox loses focus follow-through
        // on the moved row, so put the selection back where the user's eye is.
        _model.SelectedScript = moved;
        ScriptList.ScrollIntoView(moved);
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDirty) return;
        _model.Reload();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Visual source) return;

        // A double-click on the enable checkbox is two toggles, not a request
        // to open the editor.
        if (source.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;

        if (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is ScriptListItem item)
            OpenScript(item);
    }

    private async void OpenScript(ScriptListItem item)
    {
        if (_model.IsDirty && !ReferenceEquals(_model.OpenScript, item))
        {
            if (!await ConfirmDialog.ConfirmAsync(this, "Discard changes",
                    $"{_model.OpenScript?.FileName} has unsaved changes. Discard them?",
                    confirmText: "Discard"))
                return;
        }
        _model.Open(item);
        Editor.Focus();
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_model.Library.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No file manager, or the platform refused — nothing actionable, and
            // the path is on screen in the Help window anyway.
        }
    }

    private ScriptCredentialsWindow? _credentialsWindow;

    private void OnCredentials(object? sender, RoutedEventArgs e)
    {
        if (_credentialsWindow is not null) { _credentialsWindow.Activate(); return; }
        if (_model.CredentialStore is not { } store) return;

        _credentialsWindow = new ScriptCredentialsWindow(store, _model.SaveCredentials);
        _credentialsWindow.Closed += (_, _) => _credentialsWindow = null;
        _credentialsWindow.Show(this);
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
    {
        if (_helpWindow is not null) { _helpWindow.Activate(); return; }
        _helpWindow = new ScriptHelpWindow();
        _helpWindow.Closed += (_, _) => _helpWindow = null;
        _helpWindow.Show(this);
    }

    // ----- editor actions ------------------------------------------------------

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (!_model.Save(Editor.Text ?? string.Empty)) return;
        _model.RefreshMoveState();
    }

    private void OnRevert(object? sender, RoutedEventArgs e) => _model.Revert();

    private async void OnCloseEditor(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDirty &&
            !await ConfirmDialog.ConfirmAsync(this, "Discard changes",
                $"{_model.OpenScript?.FileName} has unsaved changes. Discard them?", confirmText: "Discard"))
            return;

        _model.CloseEditor();
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_model.IsDirty || e.IsProgrammatic) return;

        e.Cancel = true;
        if (!await ConfirmDialog.ConfirmAsync(this, "Discard changes",
                $"{_model.OpenScript?.FileName} has unsaved changes. Discard them and close?",
                confirmText: "Discard"))
            return;

        _model.CloseEditor();
        Close();
    }

    /// <summary>Puts the caret on the line a problem came from and selects it,
    /// so a message like "Line 12, column 3" is one click from the mistake.</summary>
    private void OnProblemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ProblemList.SelectedItem is not ScriptProblemItem item) return;
        if (item.Problem.Line <= 0) return;

        var text = Editor.Text ?? string.Empty;
        var (start, end) = LineBounds(text, item.Problem.Line);
        Editor.SelectionStart = start;
        Editor.SelectionEnd = end;
        Editor.CaretIndex = end;
        Editor.Focus();
    }

    // ----- assisted formatting -------------------------------------------------

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        var text = Editor.Text ?? string.Empty;

        // YAML rejects tabs for indentation outright, and a pasted-in tab is
        // the single most common way to make a file that won't parse. Convert
        // rather than complain, since there is only one thing the user meant.
        if (text.Contains('\t'))
        {
            int caret = Editor.CaretIndex;
            int tabsBefore = text[..Math.Clamp(caret, 0, text.Length)].Count(c => c == '\t');

            _suppressTextChanged = true;
            Editor.Text = text.Replace("\t", Indent);
            _suppressTextChanged = false;

            Editor.CaretIndex = caret + tabsBefore * (Indent.Length - 1);
            text = Editor.Text ?? string.Empty;
        }

        // Compared against what is on disk rather than simply set: Avalonia
        // raises TextChanged for programmatic assignment too, and not always
        // inside the assignment, so opening or reverting a script would
        // otherwise mark it dirty the instant it loaded. Comparing also means
        // undoing back to the original clears the flag, which is what the ·
        // in the title bar should mean.
        _model.IsDirty = !string.Equals(text, _model.OpenScript?.Text, StringComparison.Ordinal);
        _validateTimer.Stop();
        _validateTimer.Start();

        // Offered as you type rather than only on request: the four keys this
        // fires on all name something already configured, so there is nothing
        // to guess and no reason to make anyone remember a shortcut to see it.
        ShowCompletion();
    }

    /// <summary>
    /// Gives the open completion list first refusal on the keys it drives.
    /// </summary>
    /// <remarks>
    /// On the tunnel, not the bubble, because a TextBox handles the arrow keys
    /// itself and marks them handled — a bubbling handler is never reached, so
    /// Up and Down moved the caret instead of the highlight. Tunnelling runs
    /// this before the control sees the key at all.
    /// </remarks>
    private void OnEditorKeyDownPreview(object? sender, KeyEventArgs e)
    {
        if (!CompletionPopup.IsOpen) return;

        switch (e.Key)
        {
            case Key.Escape:
                CloseCompletion();
                e.Handled = true;
                break;

            case Key.Down:
                MoveCompletion(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveCompletion(-1);
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Tab:
                AcceptCompletion();
                e.Handled = true;
                break;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Ctrl+Space asks for the list where it did not open on its own —
            // on a value already typed in full, most usefully.
            case Key.Space when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                ShowCompletion();
                e.Handled = true;
                break;

            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                ContinueLine();
                e.Handled = true;
                break;

            case Key.Tab:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Outdent();
                else Editor.SelectedText = Indent;
                e.Handled = true;
                break;
        }
    }

    // ----- completion ----------------------------------------------------------

    /// <summary>What the open list would replace, so accepting a row knows
    /// which characters it stands in for.</summary>
    private ScriptCompletionResult? _completion;

    /// <summary>
    /// Offers the channels, nodes and credentials this radio knows about
    /// wherever the caret sits in a value that names one.
    /// </summary>
    /// <remarks>
    /// Suggested rather than validated, and only for the four keys that name
    /// something already configured. A node id is eight hex digits with nothing
    /// in it to say whose node it is, which is the whole reason this exists —
    /// the name rides along as the note beside each row and, where the line has
    /// room, as a comment written in after the value.
    /// </remarks>
    private void ShowCompletion()
    {
        _completion = ScriptCompletion.Suggest(
            Editor.Text ?? string.Empty, Editor.CaretIndex, _model.Completions);

        if (_completion is null)
        {
            CloseCompletion();
            return;
        }

        CompletionList.ItemsSource = _completion.Suggestions.Select(s => new ScriptCompletionItem(s)).ToList();
        CompletionList.SelectedIndex = 0;

        if (CaretRect() is not { } caret)
        {
            CloseCompletion();
            return;
        }
        // The bottom of the caret, so the list hangs under the line being
        // typed rather than over it.
        CompletionPopup.HorizontalOffset = caret.X;
        CompletionPopup.VerticalOffset = caret.Bottom;
        CompletionPopup.IsOpen = true;
    }

    private void CloseCompletion()
    {
        _completion = null;
        CompletionPopup.IsOpen = false;
    }

    private void MoveCompletion(int delta)
    {
        int count = CompletionList.ItemCount;
        if (count == 0) return;
        // Wraps, so Up from the first row reaches the last without a long hold.
        int next = (CompletionList.SelectedIndex + delta + count) % count;
        CompletionList.SelectedIndex = next;
        if (CompletionList.SelectedItem is { } item) CompletionList.ScrollIntoView(item);
    }

    /// <summary>
    /// Takes a suggestion on a single click.
    /// </summary>
    /// <remarks>
    /// Handled on the way down, and marked handled, so the row never gets to
    /// select itself or take focus: focus leaving the editor closes the list,
    /// which used to beat the click to it. That is why nothing happened until
    /// Tab was pressed instead.
    /// </remarks>
    private void OnCompletionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)
            is not { DataContext: ScriptCompletionItem item }) return;

        CompletionList.SelectedItem = item;
        AcceptCompletion();
        e.Handled = true;
    }

    /// <summary>Splices the selected value in over what has been typed, and
    /// writes its note in as a comment when the line has nothing else on
    /// it.</summary>
    private void AcceptCompletion()
    {
        if (_completion is not { } completion ||
            CompletionList.SelectedItem is not ScriptCompletionItem chosen)
        {
            CloseCompletion();
            return;
        }

        var insert = chosen.Suggestion.Insert;
        if (completion.AllowComment && chosen.Suggestion.NoteInFile && chosen.Note.Length > 0)
            insert += $"   # {chosen.Note}";

        // The popup goes first: replacing the text raises TextChanged, which
        // would otherwise reopen the list on the value just accepted.
        CloseCompletion();

        Editor.SelectionStart = completion.Start;
        Editor.SelectionEnd = completion.Start + completion.Length;
        Editor.SelectedText = insert;
        Editor.CaretIndex = completion.Start + insert.Length;
        Editor.Focus();
    }

    /// <summary>
    /// The caret's rectangle in the editor's own coordinates, for the popup to
    /// hang under.
    /// </summary>
    /// <remarks>
    /// Read off the text presenter's layout rather than computed from a
    /// character width, so it stays right through scrolling and does not assume
    /// the font is monospaced even though this one is.
    /// </remarks>
    private Rect? CaretRect()
    {
        if (Editor.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter ||
            presenter.TextLayout is not { } layout)
            return null;

        var text = Editor.Text ?? string.Empty;
        var local = layout.HitTestTextPosition(Math.Clamp(Editor.CaretIndex, 0, text.Length));
        if (presenter.TranslatePoint(local.TopLeft, Editor) is not { } origin) return null;

        return new Rect(origin, local.Size);
    }

    /// <summary>
    /// Enter carries the current line's shape onto the next one: the same
    /// indentation, one level deeper after a "key:", and a fresh "- " when
    /// continuing a list. This is the whole of the editor's "assist" — enough
    /// to keep a hand-written file structurally valid without pretending to be
    /// a full YAML-aware editor.
    /// </summary>
    private void ContinueLine()
    {
        var text = Editor.Text ?? string.Empty;
        int caret = Math.Clamp(Editor.CaretIndex, 0, text.Length);
        int lineStart = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;

        var before = text[lineStart..caret];
        var indent = new string(' ', before.Length - before.TrimStart(' ').Length);
        var trimmed = before.Trim();

        string continuation;
        if (trimmed.EndsWith(':'))
        {
            // "trigger:" opens a list, so offer the dash too; any other key
            // just opens a nested block.
            var key = trimmed[..^1].TrimStart('-', ' ').Trim();
            continuation = ListSections.Contains(key, StringComparer.Ordinal)
                ? indent + Indent + "- "
                : indent + Indent;
        }
        else if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            // Inside a list item, options line up under the item's text rather
            // than under its dash — "- text: x" then "  ignore_case: false".
            continuation = indent + Indent;
        }
        else
        {
            continuation = indent;
        }

        Editor.SelectedText = "\n" + continuation;
    }

    /// <summary>Shift+Tab removes one indent level from the start of the
    /// current line, wherever the caret happens to be on it.</summary>
    private void Outdent()
    {
        var text = Editor.Text ?? string.Empty;
        int caret = Math.Clamp(Editor.CaretIndex, 0, text.Length);
        int lineStart = caret > 0 ? text.LastIndexOf('\n', caret - 1) + 1 : 0;

        int spaces = 0;
        while (spaces < Indent.Length && lineStart + spaces < text.Length && text[lineStart + spaces] == ' ')
            spaces++;
        if (spaces == 0) return;

        Editor.SelectionStart = lineStart;
        Editor.SelectionEnd = lineStart + spaces;
        Editor.SelectedText = string.Empty;
        Editor.CaretIndex = Math.Max(lineStart, caret - spaces);
    }

    /// <summary>Character range of a 1-based line, for jump-to-problem.</summary>
    private static (int Start, int End) LineBounds(string text, int line)
    {
        int start = 0;
        for (int i = 1; i < line; i++)
        {
            int next = text.IndexOf('\n', start);
            if (next < 0) return (start, text.Length);
            start = next + 1;
        }
        int end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        // Leave a trailing \r out of the selection on CRLF files.
        if (end > start && text[end - 1] == '\r') end--;
        return (start, end);
    }
}
