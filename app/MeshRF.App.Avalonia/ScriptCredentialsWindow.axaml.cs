// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshRF.Scripting;

namespace MeshRF.AvaloniaApp;

/// <summary>One credential, as the editor binds to it. Wraps the stored record
/// so edits write straight through to what gets saved.</summary>
public sealed partial class ScriptCredentialItem : ObservableObject
{
    private readonly ScriptCredential _credential;

    public ScriptCredentialItem(ScriptCredential credential) => _credential = credential;

    public ScriptCredential Record => _credential;

    public string Name
    {
        get => _credential.Name;
        set
        {
            if (_credential.Name == value) return;
            _credential.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string Placement
    {
        get => _credential.Placement.ToString();
        set
        {
            if (!Enum.TryParse<ScriptCredentialPlacement>(value, out var parsed)) return;
            if (_credential.Placement == parsed) return;
            _credential.Placement = parsed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(NeedsParameter));
            OnPropertyChanged(nameof(ParameterHint));
        }
    }

    public string Parameter
    {
        get => _credential.Parameter;
        set
        {
            if (_credential.Parameter == value) return;
            _credential.Parameter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string Value
    {
        get => _credential.Value;
        set
        {
            if (_credential.Value == value) return;
            _credential.Value = value;
            OnPropertyChanged();
        }
    }

    public string Parameter2
    {
        get => _credential.Parameter2;
        set
        {
            if (_credential.Parameter2 == value) return;
            _credential.Parameter2 = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string Value2
    {
        get => _credential.Value2;
        set
        {
            if (_credential.Value2 == value) return;
            _credential.Value2 = value;
            OnPropertyChanged();
        }
    }

    /// <summary>A bearer token goes in a fixed header, so there is nothing to
    /// name for it — and nothing to pair it with.</summary>
    public bool NeedsParameter => _credential.Placement != ScriptCredentialPlacement.Bearer;

    public string ParameterHint => _credential.Placement switch
    {
        ScriptCredentialPlacement.Header => "X-API-Key",
        ScriptCredentialPlacement.Query => "client_id",
        _ => string.Empty,
    };

    public string Parameter2Hint => _credential.Placement switch
    {
        ScriptCredentialPlacement.Header => "(optional)",
        ScriptCredentialPlacement.Query => "client_secret (optional)",
        _ => string.Empty,
    };

    /// <summary>How it attaches. Never includes the value.</summary>
    public string Summary => _credential.Describe();
}

public sealed partial class ScriptCredentialsViewModel : ObservableObject
{
    private readonly List<ScriptCredential> _store;
    private readonly Action _save;

    public ScriptCredentialsViewModel(List<ScriptCredential> store, Action save)
    {
        _store = store;
        _save = save;
        foreach (var credential in store) Credentials.Add(new ScriptCredentialItem(credential));
        Selected = Credentials.FirstOrDefault();
    }

    public ObservableCollection<ScriptCredentialItem> Credentials { get; } = [];

    public IReadOnlyList<string> Placements { get; } =
        Enum.GetNames<ScriptCredentialPlacement>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    [NotifyPropertyChangedFor(nameof(UsageExample))]
    private ScriptCredentialItem? _selected;

    [ObservableProperty] private bool _isValueRevealed;

    public bool IsValueHidden => !IsValueRevealed;

    partial void OnIsValueRevealedChanged(bool value) => OnPropertyChanged(nameof(IsValueHidden));

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Shows the two lines a script needs to use this credential, so
    /// the name does not have to be remembered or retyped from memory.</summary>
    public string UsageExample =>
        Selected is null
            ? string.Empty
            : $"""
               - http:
                   url: "https://example.com/api"
                   credential: {Selected.Name}
               """;

    public void Add()
    {
        var credential = new ScriptCredential { Name = UniqueName(), Placement = ScriptCredentialPlacement.Bearer };
        _store.Add(credential);
        var item = new ScriptCredentialItem(credential);
        Credentials.Add(item);
        Selected = item;
        Commit();
    }

    public void Remove(ScriptCredentialItem item)
    {
        _store.Remove(item.Record);
        Credentials.Remove(item);
        Selected = Credentials.FirstOrDefault();
        Commit();
    }

    /// <summary>Validates and persists. Names have to be unique and non-empty,
    /// since that is all a script has to go on.</summary>
    public void Commit()
    {
        var named = Credentials.Where(c => c.Name.Trim().Length > 0).ToList();

        if (named.Count != Credentials.Count)
        {
            Error = "A credential needs a name before a script can use it.";
        }
        else if (named.Select(c => c.Name.Trim())
                      .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                      .Any(g => g.Count() > 1))
        {
            Error = "Two credentials share a name — a script could not tell them apart.";
        }
        else if (Credentials.Any(c => c.NeedsParameter && c.Parameter.Trim().Length == 0))
        {
            Error = "A header or query credential needs the header/parameter name filling in.";
        }
        else if (Credentials.Any(c => c.Parameter2.Trim().Length > 0 &&
                                      string.Equals(c.Parameter2.Trim(), c.Parameter.Trim(),
                                                    StringComparison.OrdinalIgnoreCase)))
        {
            // Both halves would target the same parameter, and the second would
            // simply overwrite the first.
            Error = "The two parameter names are the same — the second would replace the first.";
        }
        else
        {
            Error = string.Empty;
        }

        // Saved regardless: a half-finished entry the user is still typing
        // should survive closing the window, and the error line says what is
        // still missing.
        _save();
    }

    private string UniqueName()
    {
        const string basis = "new-credential";
        if (Credentials.All(c => !string.Equals(c.Name, basis, StringComparison.OrdinalIgnoreCase))) return basis;
        for (int i = 2; ; i++)
        {
            var candidate = $"{basis}-{i}";
            if (Credentials.All(c => !string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }
}

/// <summary>
/// Manages the API keys scripts authenticate with. Opened from the Scripts
/// window; the values live in settings, protected at rest, and never in the
/// script files themselves.
/// </summary>
public partial class ScriptCredentialsWindow : Window
{
    private readonly ScriptCredentialsViewModel _model;

    /// <summary>Parameterless form so the XAML runtime loader can reach this
    /// resource; the real entry point is the constructor below.</summary>
    public ScriptCredentialsWindow() : this([], static () => { }) { }

    public ScriptCredentialsWindow(List<ScriptCredential> store, Action save)
    {
        _model = new ScriptCredentialsViewModel(store, save);
        InitializeComponent();
        DataContext = _model;
        // Committed on close as well as on each edit, so a value typed and then
        // dismissed with the keyboard is not lost.
        Closing += (_, _) => _model.Commit();
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        _model.Add();
        if (_model.Selected is { } added) CredentialList.ScrollIntoView(added);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } item) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Remove credential",
                $"Remove \"{item.Name}\"? Any script using it will stop working until the credential is added back."))
            return;
        _model.Remove(item);
    }

    private void OnToggleReveal(object? sender, RoutedEventArgs e) =>
        _model.IsValueRevealed = !_model.IsValueRevealed;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
