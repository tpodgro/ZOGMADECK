using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Helpers;
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
            FieldKind.Sound => new SoundFieldViewModel(owner, p, f),
            FieldKind.Time => new TimeFieldViewModel(owner, p, f),
            FieldKind.Game => new GameFieldViewModel(owner, p, f),
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

    public bool IsMultiline => _kind is FieldKind.MultilineText or FieldKind.Json or FieldKind.Code;
    public bool IsMonospace => _kind is FieldKind.Json or FieldKind.Code;
    public bool CanBrowseFile => _kind is FieldKind.FilePath or FieldKind.FileOrUrl;
    public bool CanBrowseFolder => _kind is FieldKind.FolderPath or FieldKind.FileOrUrl;
    public double MinLines => _kind == FieldKind.Code ? 8 : IsMultiline ? 4 : 1;
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

/// <summary>
/// Fichier audio : saisie, parcourir, bibliothèque intégrée, durée, choix de l'extrait (curseur à deux poignées) et écoute.
/// Le volume et l'extrait s'appliquent aussi au son en cours d'écoute.
/// </summary>
public sealed class SoundFieldViewModel : FieldViewModel
{
    private double? _duration;
    private int _durationRequest;
    private static bool _autoPreview = true;

    public SoundFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : base(owner, p, f)
    {
        BrowseFileCommand = new RelayCommand(BrowseFile);
        PreviewCommand = new AsyncCommand(PreviewAsync, ex => Warning = ex.Message);
        StopCommand = new RelayCommand(() => Owner.Runtime.Audio.Stop(Owner.Model));
        ResetSegmentCommand = new RelayCommand(() => SetSegment(0, 0), () => Sound is { } a && (a.StartSeconds > 0 || a.EndSeconds > 0));
        OpenSourceCommand = new RelayCommand(o => { if (o is string url) _ = Owner.Runtime.Shell.OpenAsync(url, CancellationToken.None); });
        RefreshWarning();
        _ = LoadDurationAsync();
    }

    public ICommand BrowseFileCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResetSegmentCommand { get; }
    public ICommand OpenSourceCommand { get; }
    public IReadOnlyList<LibrarySound> Library => SoundLibrary.Sounds;
    public IReadOnlyList<Option> OnlineSources => SoundLibrary.OnlineSources.Select(s => new Option(s.Url, s.Name)).ToList();

    private PlaySoundAction? Sound => Owner.Model as PlaySoundAction;

    public string Text
    {
        get => Value as string ?? "";
        set
        {
            Value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedLibrarySound));
            RefreshWarning();
            _ = LoadDurationAsync();
        }
    }

    /// <summary>Son de la bibliothèque correspondant au fichier actuel ; le choisir le copie dans les données.</summary>
    public LibrarySound? SelectedLibrarySound
    {
        get => Library.FirstOrDefault(s => string.Equals($"{SoundLibrary.AssetsFolder}/{s.File}", Text.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null || ReferenceEquals(value, SelectedLibrarySound)) return;
            try
            {
                Text = SoundLibrary.Install(value, Owner.Runtime.Paths.DataDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warning = "Copie du son impossible : " + ex.Message;
            }
        }
    }

    // ----- Extrait -----
    public bool HasDuration => _duration is > 0;
    public double Duration => _duration ?? 1;

    /// <summary>Pas des poignées et des flèches : 0,1 s (0,01 s pour un son très court).</summary>
    public double Step => Duration < 3 ? 0.01 : 0.1;

    public double RangeStart
    {
        get => Math.Min(Sound?.StartSeconds ?? 0, Duration);
        set => SetSegment(value, RangeEndRaw);
    }

    public double RangeEnd
    {
        get => Sound is { EndSeconds: > 0 } a ? Math.Min(a.EndSeconds, Duration) : Duration;
        set => SetSegment(Sound?.StartSeconds ?? 0, value);
    }

    private double RangeEndRaw => Sound?.EndSeconds ?? 0;

    /// <summary>Écouter automatiquement l'extrait après chaque réglage d'une poignée.</summary>
    public bool AutoPreview
    {
        get => _autoPreview;
        set
        {
            _autoPreview = value;
            OnPropertyChanged();
        }
    }

    public string SegmentText
    {
        get
        {
            if (!HasDuration) return "Choisissez un fichier lisible pour régler l'extrait.";
            var a = Sound!;
            if (a.StartSeconds <= 0 && a.EndSeconds <= 0) return $"Fichier entier ({TimeFormat.Format(Math.Round(Duration, 1))}).";
            var end = a.EndSeconds > 0 ? Math.Min(a.EndSeconds, Duration) : Duration;
            return $"Extrait : {TimeFormat.Format(a.StartSeconds)} → {TimeFormat.Format(end)} ({end - a.StartSeconds:0.0#} s sur {TimeFormat.Format(Math.Round(Duration, 1))}).";
        }
    }

    private void SetSegment(double start, double end)
    {
        if (Sound is not { } a) return;
        var round = Step < 0.1 ? 2 : 1;
        start = Math.Round(Math.Max(0, start), round);
        end = Math.Round(Math.Max(0, end), round);
        // Poignée de fin au bout du fichier = « jusqu'à la fin » (0), pour rester juste si le fichier change.
        if (HasDuration && end >= Duration - Step / 2) end = 0;
        if (start <= Step / 2) start = 0;
        if (a.StartSeconds == start && a.EndSeconds == end) return;
        a.StartSeconds = start;
        a.EndSeconds = end;
        Owner.OnFieldChanged();
        RaiseSegment();
    }

    private void RaiseSegment()
    {
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(SegmentText));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Fin d'un glissement de poignée : réécoute l'extrait si l'option est cochée.</summary>
    public void OnRangeCommitted()
    {
        if (AutoPreview && HasDuration) PreviewCommand.Execute(null);
    }

    // ----- Fichier -----
    private string FullPath => ResourcePaths.Resolve(Text, Owner.Runtime.Paths.DataDirectory);

    private void BrowseFile()
    {
        var dlg = new OpenFileDialog { Filter = Attribute.FileFilter ?? PlaySoundAction.AudioFilter, CheckFileExists = true, Title = Label };
        var current = FullPath;
        if (current.Length > 0 && Path.IsPathFullyQualified(current) && Directory.Exists(Path.GetDirectoryName(current)))
            dlg.InitialDirectory = Path.GetDirectoryName(current);
        if (dlg.ShowDialog() == true) Text = dlg.FileName;
    }

    /// <summary>Écoute avec le volume et l'extrait réglés ; un nouveau clic relance depuis le début de l'extrait.</summary>
    private async Task PreviewAsync()
    {
        RefreshWarning();
        if (Sound is not { } a) return;
        var request = a.ToRequest(Owner.Runtime.Paths.DataDirectory) with { WaitForEnd = false, Repeat = SoundRepeatMode.Restart };
        await Owner.Runtime.Audio.PlayAsync(request, CancellationToken.None);
    }

    private async Task LoadDurationAsync()
    {
        var request = ++_durationRequest;
        var path = FullPath;
        TimeSpan? duration = null;
        if (path.Length > 0 && File.Exists(path)) duration = await AudioService.GetDurationAsync(path);
        if (request != _durationRequest) return; // un autre fichier a été choisi entre-temps
        _duration = duration?.TotalSeconds;
        foreach (var n in new[] { nameof(HasDuration), nameof(Duration), nameof(Step) }) OnPropertyChanged(n);
        RaiseSegment();
    }

    /// <summary>Appelée après chaque modification de l'étape : le volume suit en direct le son en cours d'écoute.</summary>
    public override void RefreshWarning()
    {
        if (Sound is { } a) Owner.Runtime.Audio.SetVolume(a, PlaySoundAction.Gain(a.Volume));
        if (string.IsNullOrWhiteSpace(Text))
        {
            Warning = null;
            return;
        }
        var full = FullPath;
        Warning = !Path.IsPathFullyQualified(full) || File.Exists(full) ? null : "Introuvable sur ce poste : " + full;
    }
}

/// <summary>Position dans un son, saisie « 1:23.5 » ou « 83.5 » ; stockée en secondes.</summary>
public sealed class TimeFieldViewModel : FieldViewModel
{
    private string _text;
    private string? _error;

    public TimeFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : base(owner, p, f)
    {
        var v = Convert.ToDouble(Value ?? 0d, CultureInfo.InvariantCulture);
        _text = v > 0 ? TimeFormat.Format(v) : "";
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            if (!TimeFormat.TryParse(value, out var seconds))
            {
                Error = "Format attendu : 1:23.5 (minutes:secondes) ou 83.5 (secondes).";
                return;
            }
            Error = null;
            Value = seconds;
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }
}

/// <summary>Jeu à lancer : liste des jeux détectés pour la plateforme choisie, ou identifiant saisi.</summary>
public sealed class GameFieldViewModel : FieldViewModel
{
    private GamePlatform? _loadedFor;
    private string? _status;

    public GameFieldViewModel(StepViewModel owner, PropertyInfo p, FieldAttribute f) : base(owner, p, f)
    {
        RefreshCommand = new RelayCommand(() => Load(force: true));
        Load(force: true);
    }

    public ObservableCollection<InstalledGame> Games { get; } = [];
    public ICommand RefreshCommand { get; }

    private GamePlatform Platform =>
        Attribute.DependsOn is { } dep && Owner.Model.GetType().GetProperty(dep)?.GetValue(Owner.Model) is GamePlatform gp ? gp : GamePlatform.Steam;

    public string Text
    {
        get => Value as string ?? "";
        set
        {
            Value = (value ?? "").Trim();
            if (Owner.Model is LaunchGameAction l) l.GameName = Games.FirstOrDefault(g => g.Id == l.GameId)?.Name;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Selected));
        }
    }

    public InstalledGame? Selected
    {
        get => Games.FirstOrDefault(g => string.Equals(g.Id, Text, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null) return;
            Text = value.Id;
        }
    }

    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Recharge la liste quand la plateforme change (appelée après chaque modification de l'étape).</summary>
    public override void RefreshWarning() => Load(force: false);

    private void Load(bool force)
    {
        var platform = Platform;
        if (!force && _loadedFor == platform) return;
        var changedPlatform = _loadedFor is not null && _loadedFor != platform;
        _loadedFor = platform;
        Games.Clear();
        foreach (var g in Owner.Runtime.Games.GetInstalledGames(platform)) Games.Add(g);
        Status = platform == GamePlatform.BattleNet
            ? "Codes des jeux Battle.net courants ; un autre code peut être saisi."
            : Games.Count == 0
                ? $"Aucun jeu {EnumLabels.Get(platform)} détecté sur ce poste : saisissez l'identifiant."
                : $"{Games.Count} jeu(x) {EnumLabels.Get(platform)} détecté(s).";
        if (changedPlatform && Games.All(g => g.Id != Text)) Text = "";
        OnPropertyChanged(nameof(Selected));
    }
}
