using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Services;

namespace ZogmaDeck.ViewModels;

/// <summary>Une étape (action) de la séquence d'un bouton, avec ses champs éditables générés depuis <see cref="FieldAttribute"/>.</summary>
public sealed class StepViewModel : ObservableObject
{
    private readonly Action _changed;
    private int _index;

    public StepViewModel(DeckAction model, DeckRuntime runtime, Action changed)
    {
        Model = model;
        Runtime = runtime;
        _changed = changed;
        Info = ActionRegistry.GetInfo(model.GetType());
        ErrorPolicies = Enum.GetValues<ErrorPolicy>().Select(v => new Option(v, EnumLabels.Get(v))).ToList();
        foreach (var (prop, field) in DeckAction.GetFields(model.GetType()))
            Fields.Add(FieldViewModel.Create(this, prop, field));
        UpdateVisibility();
    }

    public DeckAction Model { get; }
    public DeckRuntime Runtime { get; }
    public ActionTypeInfo? Info { get; }
    public ObservableCollection<FieldViewModel> Fields { get; } = [];
    public IReadOnlyList<Option> ErrorPolicies { get; }

    public string TypeName => Info?.DisplayName ?? "Action inconnue";
    public string TypeDescription => Info?.Description ?? "Ce type d'action n'est pas reconnu par cette version de ZogmaDeck.";
    public string Glyph => Info?.Glyph ?? "\uE7BA";
    public bool HasFields => Fields.Count > 0;

    public int Index
    {
        get => _index;
        set => Set(ref _index, value);
    }

    public string Description
    {
        get
        {
            try { return Model.Describe(); }
            catch (Exception) { return TypeName; }
        }
    }

    public IReadOnlyList<string> Problems
    {
        get
        {
            try { return Model.Validate().ToList(); }
            catch (Exception ex) { return [ex.Message]; }
        }
    }

    public bool HasProblems => Problems.Count > 0;

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            OnPropertyChanged();
            _changed();
        }
    }

    public Option SelectedErrorPolicy
    {
        get => ErrorPolicies.First(o => Equals(o.Value, Model.OnError));
        set
        {
            if (value?.Value is not ErrorPolicy p || p == Model.OnError) return;
            Model.OnError = p;
            OnPropertyChanged();
            _changed();
        }
    }

    internal void OnFieldChanged()
    {
        UpdateVisibility();
        foreach (var f in Fields) f.RefreshWarning();
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        _changed();
    }

    private void UpdateVisibility()
    {
        foreach (var f in Fields)
        {
            if (f.Attribute.VisibleWhen is not { } controlling) continue;
            var value = Model.GetType().GetProperty(controlling)?.GetValue(Model);
            f.IsVisible = f.Attribute.IsVisibleFor(value);
        }
    }
}

public abstract class FieldViewModel : ObservableObject
{
    private bool _isVisible = true;
    private string? _warning;

    protected FieldViewModel(StepViewModel owner, PropertyInfo property, FieldAttribute attribute)
    {
        Owner = owner;
        Property = property;
        Attribute = attribute;
    }

    public StepViewModel Owner { get; }
    public PropertyInfo Property { get; }
    public FieldAttribute Attribute { get; }
    public string Label => Attribute.Label;
    public string? Hint => Attribute.Hint;
    public string? Placeholder => Attribute.Placeholder;
    public string? Unit => Attribute.Unit;

    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>Avertissement non bloquant (ex. fichier introuvable sur ce poste).</summary>
    public string? Warning
    {
        get => _warning;
        protected set => Set(ref _warning, value);
    }

    protected object? Value
    {
        get => Property.GetValue(Owner.Model);
        set
        {
            if (Equals(Property.GetValue(Owner.Model), value)) return;
            Property.SetValue(Owner.Model, value);
            Owner.OnFieldChanged();
        }
    }

    public virtual void RefreshWarning() { }

    public static FieldViewModel Create(StepViewModel owner, PropertyInfo p, FieldAttribute f)
    {
        var kind = f.Kind;
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (kind == FieldKind.Auto)
            kind = t == typeof(bool) ? FieldKind.Toggle
                : t == typeof(int) ? FieldKind.Integer
                : t.IsEnum ? FieldKind.Choice
                : FieldKind.Text;
        return kind switch
        {
            FieldKind.Toggle => new ToggleFieldViewModel(owner, p, f),
            FieldKind.Integer => new IntegerFieldViewModel(owner, p, f),
            FieldKind.Slider => new SliderFieldViewModel(owner, p, f),
            FieldKind.Choice => new ChoiceFieldViewModel(owner, p, f),
            FieldKind.KeyChord => new KeyChordFieldViewModel(owner, p, f),
            FieldKind.Profile => new ProfileFieldViewModel(owner, p, f),
            FieldKind.ObsScene or FieldKind.ObsInput => new ObsListFieldViewModel(owner, p, f, kind == FieldKind.ObsScene),
            _ => new TextFieldViewModel(owner, p, f, kind),
        };
    }
}

public sealed class TextFieldViewModel : FieldViewModel
{
    private readonly FieldKind _kind;

    public TextFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f, FieldKind kind) : base(owner, p, f)
    {
        _kind = kind;
        BrowseFileCommand = new RelayCommand(BrowseFile);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        RefreshWarning();
    }

    public string Text
    {
        get => Value as string ?? "";
        set
        {
            Value = value;
            OnPropertyChanged();
        }
    }

    public bool IsMultiline => _kind is FieldKind.MultilineText or FieldKind.Json;
    public bool IsMonospace => _kind == FieldKind.Json;
    public bool CanBrowseFile => _kind is FieldKind.FilePath or FieldKind.FileOrUrl;
    public bool CanBrowseFolder => _kind is FieldKind.FolderPath or FieldKind.FileOrUrl;
    public double MinLines => IsMultiline ? 4 : 1;
    public ICommand BrowseFileCommand { get; }
    public ICommand BrowseFolderCommand { get; }

    private void BrowseFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = Attribute.FileFilter ?? "Tous les fichiers|*.*",
            CheckFileExists = true,
            Title = Label,
        };
        var current = ResourcePaths.Resolve(Text, Owner.Runtime.Paths.DataDirectory);
        if (current.Length > 0 && Path.IsPathFullyQualified(current) && Directory.Exists(Path.GetDirectoryName(current)))
            dlg.InitialDirectory = Path.GetDirectoryName(current);
        if (dlg.ShowDialog() == true) Text = dlg.FileName;
    }

    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = Label };
        if (dlg.ShowDialog() == true) Text = dlg.FolderName;
    }

    public override void RefreshWarning()
    {
        if (_kind is not (FieldKind.FilePath or FieldKind.FolderPath or FieldKind.FileOrUrl) || string.IsNullOrWhiteSpace(Text))
        {
            Warning = null;
            return;
        }
        if (_kind == FieldKind.FileOrUrl && OpenAction.LooksLikeUri(Text.Trim()))
        {
            Warning = null;
            return;
        }
        var full = ResourcePaths.Resolve(Text, Owner.Runtime.Paths.DataDirectory);
        if (!Path.IsPathFullyQualified(full))
        {
            Warning = null; // nom de programme résolu par Windows (ex. notepad.exe)
            return;
        }
        Warning = File.Exists(full) || Directory.Exists(full) ? null : "Introuvable sur ce poste : " + full;
    }
}

public sealed class ToggleFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : FieldViewModel(owner, p, f)
{
    public bool IsChecked
    {
        get => Value is true;
        set
        {
            Value = value;
            OnPropertyChanged();
        }
    }
}

public sealed class IntegerFieldViewModel : FieldViewModel
{
    private string _text;
    private string? _error;

    public IntegerFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : base(owner, p, f)
    {
        _text = Convert.ToString(Value, CultureInfo.CurrentCulture) ?? "0";
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            var cleaned = value.Replace(" ", "").Replace(" ", "").Replace(" ", "");
            if (!int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n))
            {
                Error = "Nombre entier attendu.";
                return;
            }
            var min = double.IsNaN(Attribute.Min) ? int.MinValue : (int)Attribute.Min;
            var max = double.IsNaN(Attribute.Max) ? int.MaxValue : (int)Attribute.Max;
            if (n < min || n > max)
            {
                Error = $"Valeur entre {min:N0} et {max:N0}.";
                return;
            }
            Error = null;
            Value = n;
            OnPropertyChanged(nameof(Human));
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Conversion lisible des millisecondes.</summary>
    public string? Human => Unit == "ms" && Value is int ms && ms >= 1000 ? $"= {ms / 1000.0:0.##} s" : null;
}

public sealed class SliderFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : FieldViewModel(owner, p, f)
{
    public double Minimum => double.IsNaN(Attribute.Min) ? 0 : Attribute.Min;
    public double Maximum => double.IsNaN(Attribute.Max) ? 100 : Attribute.Max;

    public double SliderValue
    {
        get => Convert.ToDouble(Value, CultureInfo.InvariantCulture);
        set
        {
            Value = (int)Math.Round(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
        }
    }

    public string ValueText => $"{SliderValue:0} {Unit}".Trim();
}

public sealed class ChoiceFieldViewModel : FieldViewModel
{
    public ChoiceFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : base(owner, p, f)
    {
        var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        Options = Enum.GetValues(t).Cast<object>().Select(v => new Option(v, EnumLabels.Get(t, v))).ToList();
    }

    public IReadOnlyList<Option> Options { get; }

    public Option? Selected
    {
        get => Options.FirstOrDefault(o => Equals(o.Value, Value));
        set
        {
            if (value is null) return;
            Value = value.Value;
            OnPropertyChanged();
        }
    }
}

public sealed class KeyChordFieldViewModel : FieldViewModel
{
    public KeyChordFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : base(owner, p, f)
    {
        SpecialKeys = VirtualKeys.SpecialKeys.Select(k => new Option(k.Name, k.Display)).ToList();
        ClearCommand = new RelayCommand(() => Keys = "");
    }

    public IReadOnlyList<Option> SpecialKeys { get; }
    public ICommand ClearCommand { get; }

    public string Keys
    {
        get => Value as string ?? "";
        set
        {
            Value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Error));
            OnPropertyChanged(nameof(SelectedSpecial));
        }
    }

    public Option? SelectedSpecial
    {
        get => SpecialKeys.FirstOrDefault(o => string.Equals(o.Value as string, Keys, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value?.Value is string s) Keys = s;
        }
    }

    public string? Error => string.IsNullOrEmpty(Keys) ? null : KeyChord.TryParse(Keys, out _, out var e) ? null : e;
}

public sealed class ProfileFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : FieldViewModel(owner, p, f)
{
    /// <summary>Recalculée à chaque lecture : reflète les profils créés ou renommés depuis.</summary>
    public IReadOnlyList<Option> Options => Owner.Runtime.Config.Profiles.Select(pr => new Option(pr.Id, pr.Name)).ToList();

    public Option? Selected
    {
        get => Options.FirstOrDefault(o => Equals(o.Value, Value));
        set
        {
            if (value?.Value is not Guid id) return;
            if (Owner.Model is SwitchProfileAction s) s.ProfileName = value.Label;
            Value = (Guid?)id;
            OnPropertyChanged();
        }
    }

    public override void RefreshWarning() =>
        Warning = Value is Guid id && Options.All(o => !Equals(o.Value, id)) ? "Ce profil n'existe plus." : null;
}

/// <summary>Nom de scène ou de source OBS : saisie libre, avec liste récupérée depuis OBS à la demande.</summary>
public sealed class ObsListFieldViewModel : FieldViewModel
{
    private readonly bool _scenes;
    private string? _status;

    public ObsListFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f, bool scenes) : base(owner, p, f)
    {
        _scenes = scenes;
        RefreshCommand = new AsyncCommand(RefreshAsync, ex => Status = ex.Message);
        if (Text.Length > 0) Items.Add(Text);
    }

    public ObservableCollection<string> Items { get; } = [];
    public ICommand RefreshCommand { get; }

    public string Text
    {
        get => Value as string ?? "";
        set
        {
            Value = value;
            OnPropertyChanged();
        }
    }

    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    private async Task RefreshAsync()
    {
        Status = "Interrogation d'OBS…";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var names = _scenes
            ? await Owner.Runtime.Obs.GetSceneNamesAsync(cts.Token)
            : await Owner.Runtime.Obs.GetInputNamesAsync(cts.Token);
        var current = Text;
        Items.Clear();
        foreach (var n in names) Items.Add(n);
        Text = current;
        Status = names.Count == 0
            ? (_scenes ? "Aucune scène trouvée." : "Aucune source trouvée.")
            : $"{names.Count} {(_scenes ? "scène(s)" : "source(s)")} trouvée(s) dans OBS.";
    }
}
