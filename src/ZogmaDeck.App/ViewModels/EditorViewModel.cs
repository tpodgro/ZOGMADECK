using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Execution;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Helpers;
using ZogmaDeck.Services;

namespace ZogmaDeck.ViewModels;

public sealed record MoveRequest(int From, int To);

public sealed class EditorViewModel : ObservableObject
{
    private readonly DeckRuntime _rt;
    private ProfileEditViewModel? _selectedProfile;
    private int _selectedTab;
    private string _saveStatus = "";

    public EditorViewModel(DeckRuntime runtime)
    {
        _rt = runtime;
        Settings = new SettingsViewModel(runtime);
        Obs = new ObsSettingsViewModel(runtime);
        Backup = new BackupViewModel(runtime, Reload);
        Diagnostics = new DiagnosticsViewModel(runtime);
        Appearance = new AppearanceViewModel(runtime);
        _rt.LayoutChanged += (_, _) => SelectedProfile?.RefreshLayout();
        _rt.SettingsChanged += (_, _) => SelectedProfile?.RefreshLayout(); // aperçus des tuiles (apparence)

        AddProfileCommand = new RelayCommand(AddProfile);
        DuplicateProfileCommand = new RelayCommand(DuplicateProfile, () => SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null && Profiles.Count > 1);
        MoveProfileCommand = new RelayCommand(p =>
        {
            if (p is MoveRequest m) MoveProfile(m.From, m.To);
        });
        MoveProfileUpCommand = new RelayCommand(() => MoveSelectedProfile(-1), () => SelectedProfile is not null && Profiles.IndexOf(SelectedProfile) > 0);
        MoveProfileDownCommand = new RelayCommand(() => MoveSelectedProfile(1),
            () => SelectedProfile is not null && Profiles.IndexOf(SelectedProfile) < Profiles.Count - 1);
        ShowInPanelCommand = new RelayCommand(() =>
        {
            if (SelectedProfile is not null) _rt.SwitchTo(SelectedProfile.Model.Id);
        });

        _rt.Saved += (_, _) => SaveStatus = _rt.LastSaveError is { } e
            ? "Erreur d'enregistrement : " + e
            : $"Enregistré automatiquement à {_rt.LastSavedAt:HH:mm:ss}";
        Reload();
    }

    public ObservableCollection<ProfileEditViewModel> Profiles { get; } = [];
    public SettingsViewModel Settings { get; }
    public ObsSettingsViewModel Obs { get; }
    public BackupViewModel Backup { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public AppearanceViewModel Appearance { get; }

    public ICommand AddProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand DeleteProfileCommand { get; }
    public ICommand MoveProfileCommand { get; }
    public ICommand MoveProfileUpCommand { get; }
    public ICommand MoveProfileDownCommand { get; }
    public ICommand ShowInPanelCommand { get; }

    public ProfileEditViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set => Set(ref _selectedProfile, value);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value) && value == 5) Diagnostics.Refresh();
        }
    }

    public string SaveStatus
    {
        get => _saveStatus;
        private set => Set(ref _saveStatus, value);
    }

    internal void Changed() => _rt.NotifyChanged();

    /// <summary>Reconstruit tout depuis la configuration (après import ou restauration).</summary>
    public void Reload()
    {
        var selectedId = SelectedProfile?.Model.Id ?? _rt.Config.ActiveProfileId;
        Profiles.Clear();
        foreach (var p in _rt.Config.Profiles) Profiles.Add(new ProfileEditViewModel(p, this, _rt));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Model.Id == selectedId) ?? Profiles.FirstOrDefault();
        Settings.Reload();
        Obs.Reload();
        Appearance.Reload();
    }

    /// <summary>Sélectionne un bouton (ou en crée un) dans le profil actif du panneau.</summary>
    public void Focus(Guid? buttonId, bool addNew)
    {
        SelectedTab = 0;
        var profile = buttonId is { } id
            ? Profiles.FirstOrDefault(p => p.Model.Buttons.Any(b => b.Id == id))
            : Profiles.FirstOrDefault(p => p.Model.Id == _rt.Config.ActiveProfileId);
        if (profile is null) return;
        SelectedProfile = profile;
        if (addNew) profile.AddButton();
        else if (buttonId is { } bid) profile.SelectedButton = profile.Buttons.FirstOrDefault(b => b.Model.Id == bid);
    }

    private void AddProfile()
    {
        var names = _rt.Config.Profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = "Nouveau profil";
        for (var i = 2; names.Contains(name); i++) name = $"Nouveau profil {i}";
        var model = new Profile { Name = name };
        _rt.Config.Profiles.Add(model);
        var vm = new ProfileEditViewModel(model, this, _rt);
        Profiles.Add(vm);
        SelectedProfile = vm;
        Changed();
    }

    private void DuplicateProfile()
    {
        if (SelectedProfile is null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(SelectedProfile.Model, JsonDefaults.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Profile>(json, JsonDefaults.Options)!;
        copy.Id = Guid.NewGuid();
        foreach (var b in copy.Buttons) b.Id = Guid.NewGuid();
        var names = _rt.Config.Profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        copy.Name = $"{SelectedProfile.Model.Name} (copie)";
        for (var i = 2; names.Contains(copy.Name); i++) copy.Name = $"{SelectedProfile.Model.Name} (copie {i})";
        var index = _rt.Config.Profiles.IndexOf(SelectedProfile.Model) + 1;
        _rt.Config.Profiles.Insert(index, copy);
        var vm = new ProfileEditViewModel(copy, this, _rt);
        Profiles.Insert(index, vm);
        SelectedProfile = vm;
        Changed();
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null || Profiles.Count <= 1) return;
        var p = SelectedProfile;
        var answer = MessageBox.Show(
            $"Supprimer le profil « {p.Model.Name} » et ses {p.Model.Buttons.Count} bouton(s) ?\n\nUne copie de la configuration reste disponible dans les sauvegardes automatiques.",
            "ZogmaDeck", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _rt.Store.CreateSnapshot("avant-suppression");
        var index = Profiles.IndexOf(p);
        _rt.Config.Profiles.Remove(p.Model);
        Profiles.Remove(p);
        if (_rt.Config.ActiveProfileId == p.Model.Id) _rt.SwitchTo(_rt.Config.Profiles[0].Id);
        SelectedProfile = Profiles[Math.Min(index, Profiles.Count - 1)];
        Changed();
    }

    private void MoveSelectedProfile(int delta)
    {
        if (SelectedProfile is null) return;
        var i = Profiles.IndexOf(SelectedProfile);
        MoveProfile(i, i + delta);
    }

    private void MoveProfile(int from, int to)
    {
        if (from < 0 || from >= Profiles.Count || to < 0 || to >= Profiles.Count || from == to) return;
        var vm = Profiles[from];
        Profiles.Move(from, to);
        _rt.Config.Profiles.RemoveAt(from);
        _rt.Config.Profiles.Insert(to, vm.Model);
        SelectedProfile = vm;
        Changed();
    }
}

public sealed class ProfileEditViewModel : ObservableObject
{
    private readonly EditorViewModel _editor;
    private readonly DeckRuntime _rt;
    private ButtonEditViewModel? _selectedButton;

    public ProfileEditViewModel(Profile model, EditorViewModel editor, DeckRuntime runtime)
    {
        Model = model;
        _editor = editor;
        _rt = runtime;
        foreach (var b in model.Buttons) Buttons.Add(new ButtonEditViewModel(b, this, runtime));
        AddButtonCommand = new RelayCommand(AddButton);
        DuplicateButtonCommand = new RelayCommand(DuplicateButton, () => SelectedButton is not null);
        DeleteButtonCommand = new RelayCommand(DeleteButton, () => SelectedButton is not null);
        MoveButtonCommand = new RelayCommand(p =>
        {
            if (p is MoveRequest m) MoveButton(m.From, m.To);
        });
        MoveLeftCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedButton is not null && Buttons.IndexOf(SelectedButton) > 0);
        MoveRightCommand = new RelayCommand(() => MoveSelected(1),
            () => SelectedButton is not null && Buttons.IndexOf(SelectedButton) < Buttons.Count - 1);
    }

    public Profile Model { get; }
    public ObservableCollection<ButtonEditViewModel> Buttons { get; } = [];
    public ICommand AddButtonCommand { get; }
    public ICommand DuplicateButtonCommand { get; }
    public ICommand DeleteButtonCommand { get; }
    public ICommand MoveButtonCommand { get; }
    public ICommand MoveLeftCommand { get; }
    public ICommand MoveRightCommand { get; }

    public string Name
    {
        get => Model.Name;
        set
        {
            var v = value ?? "";
            if (v == Model.Name) return;
            Model.Name = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NameError));
            if (!string.IsNullOrWhiteSpace(v)) _editor.Changed();
        }
    }

    /// <summary>Taille ou colonnes modifiées depuis le panneau.</summary>
    public void RefreshLayout()
    {
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(ButtonSize));
        foreach (var b in Buttons) b.RefreshPreview();
    }

    public string? NameError => string.IsNullOrWhiteSpace(Model.Name) ? "Le nom ne peut pas être vide." : null;

    public int Columns
    {
        get => Model.Columns;
        set
        {
            value = Math.Clamp(value, Profile.MinColumns, Profile.MaxColumns);
            if (value == Model.Columns) return;
            Model.Columns = value;
            OnPropertyChanged();
            _editor.Changed();
        }
    }

    public int ButtonSize
    {
        get => Model.ButtonSize;
        set
        {
            value = Math.Clamp(value, Profile.MinButtonSize, Profile.MaxButtonSize);
            if (value == Model.ButtonSize) return;
            Model.ButtonSize = value;
            OnPropertyChanged();
            foreach (var b in Buttons) b.RefreshPreview();
            _editor.Changed();
        }
    }

    public ButtonEditViewModel? SelectedButton
    {
        get => _selectedButton;
        set
        {
            if (!Set(ref _selectedButton, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNoSelection));
        }
    }

    public bool HasSelection => SelectedButton is not null;
    public bool HasNoSelection => SelectedButton is null;

    internal void Changed() => _editor.Changed();

    public void AddButton()
    {
        var model = new DeckButton
        {
            Label = "Nouveau",
            Icon = ButtonIcon.FromGlyph("\uE945"),
            Color = ColorHelper.Palette[Buttons.Count % ColorHelper.Palette.Count],
        };
        Model.Buttons.Add(model);
        var vm = new ButtonEditViewModel(model, this, _rt);
        Buttons.Add(vm);
        SelectedButton = vm;
        Changed();
    }

    private void DuplicateButton()
    {
        if (SelectedButton is null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(SelectedButton.Model, JsonDefaults.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<DeckButton>(json, JsonDefaults.Options)!;
        copy.Id = Guid.NewGuid();
        copy.Label = string.IsNullOrWhiteSpace(copy.Label) ? "Copie" : copy.Label + " (copie)";
        var index = Model.Buttons.IndexOf(SelectedButton.Model) + 1;
        Model.Buttons.Insert(index, copy);
        var vm = new ButtonEditViewModel(copy, this, _rt);
        Buttons.Insert(index, vm);
        SelectedButton = vm;
        Changed();
    }

    private void DeleteButton()
    {
        if (SelectedButton is not { } b) return;
        if (b.Model.Actions.Count > 0 &&
            MessageBox.Show($"Supprimer le bouton « {b.Model.Label} » ?", "ZogmaDeck", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var index = Buttons.IndexOf(b);
        Model.Buttons.Remove(b.Model);
        Buttons.Remove(b);
        SelectedButton = Buttons.Count == 0 ? null : Buttons[Math.Min(index, Buttons.Count - 1)];
        Changed();
    }

    private void MoveSelected(int delta)
    {
        if (SelectedButton is null) return;
        var i = Buttons.IndexOf(SelectedButton);
        MoveButton(i, i + delta);
    }

    public void MoveButton(int from, int to)
    {
        if (from < 0 || from >= Buttons.Count || to < 0 || to >= Buttons.Count || from == to) return;
        var vm = Buttons[from];
        Buttons.Move(from, to);
        Model.Buttons.RemoveAt(from);
        Model.Buttons.Insert(to, vm.Model);
        SelectedButton = vm;
        Changed();
    }
}

public sealed class ActionMenuEntry(ActionTypeInfo info)
{
    public ActionTypeInfo Info { get; } = info;
    public string Name => Info.DisplayName;
    public string Glyph => Info.Glyph;
    public string Description => Info.Description;
}

public sealed class ActionMenuGroup(string category, IReadOnlyList<ActionMenuEntry> entries)
{
    public string Category { get; } = category;
    public IReadOnlyList<ActionMenuEntry> Entries { get; } = entries;
}

public sealed class ButtonEditViewModel : ObservableObject
{
    private readonly ProfileEditViewModel _profile;
    private readonly DeckRuntime _rt;
    private StepViewModel? _selectedStep;
    private PanelButtonViewModel _preview = null!;
    private string _colorText;
    private string? _testResult;
    private bool _testIsError;

    public ButtonEditViewModel(DeckButton model, ProfileEditViewModel profile, DeckRuntime runtime)
    {
        Model = model;
        _profile = profile;
        _rt = runtime;
        _colorText = model.Color;
        foreach (var a in model.Actions) Steps.Add(new StepViewModel(a, runtime, OnStepChanged));
        Renumber();
        HideOptions = Enum.GetValues<HideBehavior>().Select(v => new Option(v, EnumLabels.Get(v))).ToList();

        AddStepCommand = new RelayCommand(p =>
        {
            if (p is ActionMenuEntry e) AddStep(e.Info.TypeId);
        });
        DuplicateStepCommand = new RelayCommand(DuplicateStep, () => SelectedStep is not null);
        DeleteStepCommand = new RelayCommand(DeleteStep, () => SelectedStep is not null);
        MoveStepUpCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedStep is not null && Steps.IndexOf(SelectedStep) > 0);
        MoveStepDownCommand = new RelayCommand(() => MoveSelected(1), () => SelectedStep is not null && Steps.IndexOf(SelectedStep) < Steps.Count - 1);
        MoveStepCommand = new RelayCommand(p =>
        {
            if (p is MoveRequest m) MoveStep(m.From, m.To);
        });
        SetColorCommand = new RelayCommand(p =>
        {
            if (p is string hex) ColorText = hex;
        });
        BrowseImageCommand = new RelayCommand(BrowseImage);
        BrowseProgramIconCommand = new RelayCommand(BrowseProgramIcon);
        UseLaunchedProgramIconCommand = new RelayCommand(() => UseProgramIcon(LaunchedProgram!), () => LaunchedProgram is not null);
        Picker = new IconPickerViewModel(Pick);
        Picker.Kind = Model.Icon.Kind is IconKind.Emoji or IconKind.Brand ? Model.Icon.Kind : IconKind.Glyph;
        Picker.Refresh();
        // Commande toujours active : un second clic pendant l'exécution annule le test.
        TestCommand = new RelayCommand(() => _ = TestAsync());
        RefreshPreview();
    }

    public DeckButton Model { get; }
    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public IReadOnlyList<Option> HideOptions { get; }
    public static IReadOnlyList<string> Palette => ColorHelper.Palette;

    public static IReadOnlyList<ActionMenuGroup> ActionMenu { get; } = ActionRegistry.All
        .GroupBy(i => i.Category)
        .Select(g => new ActionMenuGroup(g.Key, g.Select(i => new ActionMenuEntry(i)).ToList()))
        .ToList();

    public ICommand AddStepCommand { get; }
    public ICommand DuplicateStepCommand { get; }
    public ICommand DeleteStepCommand { get; }
    public ICommand MoveStepUpCommand { get; }
    public ICommand MoveStepDownCommand { get; }
    public ICommand MoveStepCommand { get; }
    public ICommand SetColorCommand { get; }
    public ICommand BrowseImageCommand { get; }
    public ICommand BrowseProgramIconCommand { get; }
    public ICommand UseLaunchedProgramIconCommand { get; }
    public ICommand TestCommand { get; }

    public PanelButtonViewModel Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    public void RefreshPreview()
    {
        Preview = new PanelButtonViewModel(Model, _profile.Model.ButtonSize, int.MaxValue, _rt.Paths.DataDirectory, _rt.Config.Settings.Panel);
        OnPropertyChanged(nameof(Summary));
    }

    private void Changed(bool preview = true)
    {
        if (preview) RefreshPreview();
        _profile.Changed();
    }

    public string Summary => Model.Actions.Count switch
    {
        0 => "Aucune action",
        1 => "1 action",
        var n => $"{n} actions",
    };

    public bool HasProblems => Steps.Any(s => s.HasProblems);

    public string Label
    {
        get => Model.Label;
        set
        {
            if (Model.Label == value) return;
            Model.Label = value ?? "";
            OnPropertyChanged();
            Changed();
        }
    }

    public bool ShowLabel
    {
        get => Model.ShowLabel;
        set
        {
            if (Model.ShowLabel == value) return;
            Model.ShowLabel = value;
            OnPropertyChanged();
            Changed();
        }
    }

    public string ColorText
    {
        get => _colorText;
        set
        {
            if (!Set(ref _colorText, value)) return;
            var v = value.Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            OnPropertyChanged(nameof(ColorError));
            if (!ConfigValidator.IsValidColor(v)) return;
            Model.Color = v.ToUpperInvariant();
            OnPropertyChanged(nameof(ColorBrush));
            Changed();
        }
    }

    public string? ColorError
    {
        get
        {
            var v = _colorText.Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            return ConfigValidator.IsValidColor(v) ? null : "Format attendu : #RRGGBB";
        }
    }

    public Brush ColorBrush => ColorHelper.Brush(ColorHelper.Parse(Model.Color, Colors.SteelBlue));

    // --- Icône ---
    public IconPickerViewModel Picker { get; }

    public bool IsIconNone
    {
        get => Model.Icon.Kind == IconKind.None;
        set { if (value) SetIconKind(IconKind.None); }
    }

    public bool IsIconGlyph
    {
        get => Model.Icon.Kind == IconKind.Glyph;
        set { if (value) SetIconKind(IconKind.Glyph); }
    }

    public bool IsIconEmoji
    {
        get => Model.Icon.Kind == IconKind.Emoji;
        set { if (value) SetIconKind(IconKind.Emoji); }
    }

    public bool IsIconBrand
    {
        get => Model.Icon.Kind == IconKind.Brand;
        set { if (value) SetIconKind(IconKind.Brand); }
    }

    public bool IsIconImage
    {
        get => Model.Icon.Kind == IconKind.Image;
        set { if (value) SetIconKind(IconKind.Image); }
    }

    /// <summary>Le sélecteur s'affiche pour les symboles, emoji et logos.</summary>
    public bool ShowPicker => Model.Icon.Kind is IconKind.Glyph or IconKind.Emoji or IconKind.Brand;

    public string CurrentIconText => Model.Icon.Kind switch
    {
        IconKind.Glyph => "Symbole : " + (IconLibrary.Fluent.FirstOrDefault(c => c.Value == Model.Icon.Glyph)?.Name ?? "?"),
        IconKind.Emoji => "Emoji : " + (IconLibrary.Emoji.FirstOrDefault(c => c.Value == Model.Icon.Glyph)?.Name ?? Model.Icon.Glyph),
        IconKind.Brand => "Logo : " + (IconLibrary.Brand(Model.Icon.Glyph)?.Title ?? Model.Icon.Glyph),
        IconKind.Image => "Image",
        _ => "Aucune icône",
    };

    private void SetIconKind(IconKind kind)
    {
        if (Model.Icon.Kind == kind) return;
        var previous = Model.Icon.Kind;
        Model.Icon.Kind = kind;
        // Valeur par défaut pour un aperçu immédiat ; l'utilisateur choisit ensuite dans la liste.
        Model.Icon.Glyph = kind switch
        {
            IconKind.Glyph when previous != IconKind.Glyph => IconCatalog.Glyphs[0].Glyph,
            IconKind.Emoji when previous != IconKind.Emoji => "⭐",
            IconKind.Brand when previous != IconKind.Brand => IconLibrary.Brands.FirstOrDefault(b => b.Slug == "obsstudio")?.Slug
                                                              ?? IconLibrary.Brands.FirstOrDefault()?.Slug,
            _ => Model.Icon.Glyph,
        };
        if (kind is IconKind.Glyph or IconKind.Emoji or IconKind.Brand) Picker.Kind = kind;
        RaiseIconChanged();
        Changed();
    }

    private void Pick(IconChoice choice)
    {
        Model.Icon.Kind = choice.Kind;
        Model.Icon.Glyph = choice.Value;
        // Un logo prend la couleur officielle de la marque (modifiable ensuite).
        if (choice.Kind == IconKind.Brand && IconLibrary.Brand(choice.Value) is { } brand) ColorText = brand.Hex;
        RaiseIconChanged();
        Changed();
    }

    private void RaiseIconChanged()
    {
        foreach (var n in new[]
                 {
                     nameof(IsIconNone), nameof(IsIconGlyph), nameof(IsIconEmoji), nameof(IsIconBrand), nameof(IsIconImage),
                     nameof(ShowPicker), nameof(CurrentIconText), nameof(ImagePath), nameof(ImageWarning), nameof(LaunchedProgram),
                 })
            OnPropertyChanged(n);
    }

    public string? ImagePath => Model.Icon.ImagePath;

    public string? ImageWarning => Model.Icon.Kind == IconKind.Image && !ResourcePaths.Exists(Model.Icon.ImagePath, _rt.Paths.DataDirectory)
        ? (string.IsNullOrEmpty(Model.Icon.ImagePath) ? "Choisissez une image ou l'icône d'un programme." : "Image introuvable : " + Model.Icon.ImagePath)
        : null;

    /// <summary>Programme lancé par la première action « Lancer une application » du bouton, s'il existe.</summary>
    public string? LaunchedProgram => Model.Actions.OfType<LaunchAppAction>().Select(a => a.Path).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

    private void SetImage(string relativePath)
    {
        Model.Icon.ImagePath = relativePath;
        Model.Icon.Kind = IconKind.Image;
        RaiseIconChanged();
        Changed();
    }

    private void BrowseImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choisir une image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff;*.webp|Tous les fichiers|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        if (ImageLoader.Load(dlg.FileName, "", 64) is null)
        {
            MessageBox.Show("Cette image ne peut pas être lue.", "ZogmaDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            // Copie dans le dossier de données : la configuration reste valide même si l'original est déplacé.
            SetImage(ImageLoader.ImportToAssets(dlg.FileName, _rt.Paths.AssetsDirectory));
        }
        catch (IOException ex)
        {
            MessageBox.Show("Copie de l'image impossible : " + ex.Message, "ZogmaDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Icône officielle d'un programme ou raccourci installé (Discord, Teams, Photoshop…), extraite en 256 px.</summary>
    private void BrowseProgramIcon()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choisir un programme ou un raccourci",
            Filter = "Programmes et raccourcis|*.exe;*.lnk;*.ico;*.dll|Tous les fichiers|*.*",
            CheckFileExists = true,
            DereferenceLinks = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + "\\Programs",
        };
        if (dlg.ShowDialog() == true) UseProgramIcon(dlg.FileName);
    }

    private void UseProgramIcon(string path)
    {
        try
        {
            if (ShellIcons.SaveToAssets(path, _rt.Paths.AssetsDirectory) is { } rel) SetImage(rel);
            else MessageBox.Show("Impossible d'obtenir l'icône de ce fichier.", "ZogmaDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException ex)
        {
            MessageBox.Show("Enregistrement de l'icône impossible : " + ex.Message, "ZogmaDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public Option SelectedHide
    {
        get => HideOptions.First(o => Equals(o.Value, Model.HideAfterRun));
        set
        {
            if (value?.Value is not HideBehavior h || h == Model.HideAfterRun) return;
            Model.HideAfterRun = h;
            OnPropertyChanged();
            Changed(preview: false);
        }
    }

    // --- Étapes ---
    public StepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!Set(ref _selectedStep, value)) return;
            OnPropertyChanged(nameof(HasSelectedStep));
        }
    }

    public bool HasSelectedStep => SelectedStep is not null;
    public bool HasNoSteps => Steps.Count == 0;

    private void OnStepChanged()
    {
        OnPropertyChanged(nameof(HasProblems));
        Changed();
    }

    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++) Steps[i].Index = i + 1;
        OnPropertyChanged(nameof(HasNoSteps));
        OnPropertyChanged(nameof(HasProblems));
    }

    public void AddStep(string typeId)
    {
        var action = ActionRegistry.Create(typeId);
        var index = SelectedStep is null ? Model.Actions.Count : Model.Actions.IndexOf(SelectedStep.Model) + 1;
        Model.Actions.Insert(index, action);
        var vm = new StepViewModel(action, _rt, OnStepChanged);
        Steps.Insert(index, vm);
        Renumber();
        SelectedStep = vm;
        Changed();
    }

    private void DuplicateStep()
    {
        if (SelectedStep is null) return;
        var copy = SelectedStep.Model.Clone();
        var index = Model.Actions.IndexOf(SelectedStep.Model) + 1;
        Model.Actions.Insert(index, copy);
        var vm = new StepViewModel(copy, _rt, OnStepChanged);
        Steps.Insert(index, vm);
        Renumber();
        SelectedStep = vm;
        Changed();
    }

    private void DeleteStep()
    {
        if (SelectedStep is not { } s) return;
        var index = Steps.IndexOf(s);
        Model.Actions.Remove(s.Model);
        Steps.Remove(s);
        Renumber();
        SelectedStep = Steps.Count == 0 ? null : Steps[Math.Min(index, Steps.Count - 1)];
        Changed();
    }

    private void MoveSelected(int delta)
    {
        if (SelectedStep is null) return;
        var i = Steps.IndexOf(SelectedStep);
        MoveStep(i, i + delta);
    }

    public void MoveStep(int from, int to)
    {
        if (from < 0 || from >= Steps.Count || to < 0 || to >= Steps.Count || from == to) return;
        var vm = Steps[from];
        Steps.Move(from, to);
        Model.Actions.RemoveAt(from);
        Model.Actions.Insert(to, vm.Model);
        Renumber();
        SelectedStep = vm;
        Changed();
    }

    // --- Test ---
    public bool IsTesting => _rt.IsRunning(Model.Id);

    public string TestButtonText => IsTesting ? "Annuler" : "Tester";

    public string TestTarget => _rt.Focus.Target != IntPtr.Zero
        ? $"Les frappes clavier iront à : « {Shorten(_rt.Focus.TargetDescription)} »"
        : "Aucune fenêtre cible mémorisée pour les frappes clavier.";

    public string? TestResult
    {
        get => _testResult;
        private set => Set(ref _testResult, value);
    }

    public bool TestIsError
    {
        get => _testIsError;
        private set => Set(ref _testIsError, value);
    }

    private async Task TestAsync()
    {
        if (IsTesting)
        {
            await _rt.RunAsync(Model, RunOrigin.Editor); // second appel = annulation
            return;
        }
        TestResult = "Exécution…";
        TestIsError = false;
        OnPropertyChanged(nameof(TestTarget));
        var run = _rt.RunAsync(Model, RunOrigin.Editor);
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(TestButtonText));
        var result = await run;
        OnPropertyChanged(nameof(IsTesting));
        OnPropertyChanged(nameof(TestButtonText));
        if (result is null) return;
        var lines = result.Steps.Select(s => $"{s.Index + 1}. {StatusLabel(s.Status)} — {s.Description}{(s.Error is null ? "" : " : " + s.Error)}");
        TestResult = $"{result.Summary}\n{string.Join('\n', lines)}";
        TestIsError = result.Status is SequenceStatus.Failed or SequenceStatus.CompletedWithErrors;
    }

    private static string StatusLabel(StepStatus s) => s switch
    {
        StepStatus.Succeeded => "OK",
        StepStatus.Failed => "Échec",
        StepStatus.Skipped => "Ignorée",
        StepStatus.Cancelled => "Annulée",
        _ => s.ToString(),
    };

    private static string Shorten(string s) => s.Length > 60 ? s[..59] + "…" : s;
}
