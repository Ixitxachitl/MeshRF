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

    /// <summary>A bearer token goes in a fixed header, so there is nothing to
    /// name for it.</summary>
    public bool NeedsParameter => _credential.Placement != ScriptCredentialPlacement.Bearer;

    public string ParameterHint => _credential.Placement switch
    {
        ScriptCredentialPlacement.Header => "X-API-Key",
        ScriptCredentialPlacement.Query => "appid",
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
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    private ScriptCredentialItem? _selected;

    [ObservableProperty] private bool _isValueRevealed;

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>
    /// The credential: line a script needs, ready to copy. Lists every row when
    /// nothing is selected, so an id/secret pair can be taken in one go without
    /// retyping either name.
    /// </summary>
    public string UsageExample
    {
        get
        {
            var named = Credentials.Where(c => c.Name.Trim().Length > 0).Select(c => c.Name.Trim()).ToList();
            if (named.Count == 0) return string.Empty;

            if (Selected is { Name.Length: > 0 } one && named.Count > 1)
                return $"credential: {one.Name.Trim()}      (all: credential: [{string.Join(", ", named)}])";

            return named.Count == 1
                ? $"credential: {named[0]}"
                : $"credential: [{string.Join(", ", named)}]";
        }
    }

    public bool HasUsage => UsageExample.Length > 0;

    public ScriptCredentialItem Add(
        string name = "", ScriptCredentialPlacement placement = ScriptCredentialPlacement.Bearer,
        string parameter = "")
    {
        var credential = new ScriptCredential
        {
            Name = name.Length > 0 ? UniqueName(name) : UniqueName(),
            Placement = placement,
            Parameter = parameter,
        };
        _store.Add(credential);
        var item = new ScriptCredentialItem(credential);
        Credentials.Add(item);
        Selected = item;
        Commit();
        return item;
    }

    /// <summary>Adds the two rows an id/secret API needs, pre-filled with the
    /// parameter names those APIs almost always use.</summary>
    public void AddPair()
    {
        Add("client-id", ScriptCredentialPlacement.Query, "client_id");
        var secret = Add("client-secret", ScriptCredentialPlacement.Query, "client_secret");
        Selected = secret;
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
        else
        {
            Error = string.Empty;
        }

        // Saved regardless: a half-finished entry the user is still typing
        // should survive closing the window, and the error line says what is
        // still missing.
        _save();
    }

    private string UniqueName(string basis = "new-credential")
    {
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

    private void OnAdd(object? sender, RoutedEventArgs e) => AddAndShow(() => _model.Add());

    private void OnAddPair(object? sender, RoutedEventArgs e) => AddAndShow(_model.AddPair);

    private void AddAndShow(Action add)
    {
        add();
        if (_model.Selected is { } added) CredentialGrid.ScrollIntoView(added, null);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_model.Selected is not { } item) return;
        if (!await ConfirmDialog.ConfirmAsync(this, "Remove credential",
                $"Remove \"{item.Name}\"? Any script using it will stop working until the credential is added back."))
            return;
        _model.Remove(item);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
