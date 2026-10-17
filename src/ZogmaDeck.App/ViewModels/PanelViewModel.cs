using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ZogmaDeck.Core.Execution;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Obs;
using ZogmaDeck.Helpers;
using ZogmaDeck.Services;

namespace ZogmaDeck.ViewModels;

public sealed class ProfileTab(Guid id, string name, bool isActive, int index)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsActive { get; } = isActive;
    public string ToolTip { get; } = index < 9 ? $"{name} (Ctrl+{index + 1})" : name;
}

public sealed class AddTileViewModel
{
    public AddTileViewModel(double size, PanelAppearance look)
    {
        Size = size;
        GlyphSize = Math.Max(16, size * 0.26);
        TileMargin = new Thickness(look.TileSpacing / 2.0);
        Radius = look.TileCornerRadius;
        var glass = look.TileStyle == TileStyle.Glass;
        Fill = glass ? Glass.AddFill : Resource("Pd.AddTile");
        Stroke = glass ? Glass.AddStroke : Resource("Pd.BorderStrong");
        TextBrush = glass ? Glass.Text : Resource("Pd.TextTertiary");
        TextEffect = glass ? Glass.TextShadow : null;
    }

    private static Brush Resource(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public double Size { get; }
    public double GlyphSize { get; }
    public Thickness TileMargin { get; }
    public double Radius { get; }
    public Brush Fill { get; }
    public Brush Stroke { get; }
    public Brush TextBrush { get; }
    public System.Windows.Media.Effects.Effect? TextEffect { get; }
}

public sealed class PanelButtonViewModel : ObservableObject
{
    private bool _isRunning;
    private string _flash = "";
    private string? _progressText;
    private DispatcherTimer? _flashTimer;

    public PanelButtonViewModel(DeckButton model, double size, int index, string dataDirectory, PanelAppearance? look = null)
    {
        look ??= new PanelAppearance();
        Model = model;
        Size = size;
        Label = model.Label;
        ShowLabel = model.ShowLabel && !string.IsNullOrWhiteSpace(model.Label);
        var color = ColorHelper.Parse(model.Color, ColorHelper.Parse(DeckButton.DefaultColor, Colors.SteelBlue));
        Background = ColorHelper.Brush(color);
        Foreground = ColorHelper.Brush(ColorHelper.ContrastingForeground(color));

        var icon = model.Icon;
        switch (icon.Kind)
        {
            case IconKind.Image:
                Image = ImageLoader.Load(icon.ImagePath, dataDirectory, (int)Math.Ceiling(size * 2));
                if (Image is null) Glyph = "\uE7BA"; // image manquante
                break;
            case IconKind.Glyph when !string.IsNullOrEmpty(icon.Glyph):
                Glyph = icon.Glyph;
                break;
            case IconKind.Emoji when !string.IsNullOrEmpty(icon.Glyph):
                Glyph = icon.Glyph;
                GlyphFont = IconLibrary.EmojiFont;
                break;
            case IconKind.Brand:
                Geometry = IconLibrary.BrandGeometry(icon.Glyph);
                if (Geometry is null) Glyph = "\uE7BA"; // logo inconnu
                break;
            case IconKind.Text when !string.IsNullOrWhiteSpace(icon.Glyph):
                Glyph = icon.Glyph.Trim();
                GlyphFont = TextIconFont;
                GlyphWeight = FontWeights.SemiBold;
                break;
        }

        var iconScale = look.IconScale / 100.0;
        IsGlass = look.TileStyle == TileStyle.Glass;
        CornerRadius = new CornerRadius(look.TileCornerRadius);

        if (IsGlass)
        {
            // Verre : tuile translucide + reflet ; l'icône est posée sur une pastille à la couleur du bouton.
            TileBackground = Glass.TileFill;
            TileBorder = Glass.TileBorder;
            Sheen = Glass.Sheen;
            LabelForeground = Glass.Text;
            LabelEffect = Glass.TextShadow;
            var hasIcon = HasGlyph || HasGeometry;
            if (hasIcon)
            {
                BadgeSize = Math.Max(16, Math.Round(size * (ShowLabel ? 0.46 : 0.6) * iconScale));
                BadgeBrush = Glass.Badge(color);
                BadgeCorner = new CornerRadius(Math.Min(look.TileCornerRadius, BadgeSize * 0.3));
                BadgeEffect = Glass.BadgeShadow;
            }
            IconForeground = Brushes.White;
            var badge = double.IsNaN(BadgeSize) ? size * 0.46 * iconScale : BadgeSize;
            GlyphSize = Math.Max(8, Math.Round(badge * 0.56));
            GeometrySize = Math.Max(8, Math.Round(badge * 0.52));
            ImageSize = Math.Max(8, Math.Round(size * (ShowLabel ? 0.46 : 0.62) * iconScale));
        }
        else
        {
            TileBackground = Background;
            TileBorder = ColorHelper.Brush(Color.FromArgb(0x26, 0, 0, 0));
            LabelForeground = Foreground;
            IconForeground = Foreground;
            GlyphSize = Math.Max(8, Math.Round(size * (ShowLabel ? 0.30 : 0.42) * iconScale));
            ImageSize = Math.Max(8, Math.Round(size * (ShowLabel ? 0.42 : 0.62) * iconScale));
            GeometrySize = Math.Max(8, Math.Round(size * (ShowLabel ? 0.32 : 0.46) * iconScale));
        }

        if (icon.Kind == IconKind.Text && Glyph is { } text)
        {
            // Plus le texte est long, plus il est petit, pour tenir dans la pastille ou le bouton.
            var factor = text.Length switch { 1 => 0.95, 2 => 0.78, 3 => 0.6, 4 => 0.48, _ => 0.4 };
            GlyphSize = Math.Max(8, Math.Round(GlyphSize * factor));
        }

        LabelSize = Math.Clamp(Math.Round(Math.Clamp(size * 0.125, 11, 16) * look.LabelScale / 100.0), 8, 28);
        LabelMaxHeight = Math.Ceiling(LabelSize * 2.7);
        ShortcutHint = look.ShowShortcutNumbers && index < 9 ? (index + 1).ToString() : null;
        TileMargin = new Thickness(look.TileSpacing / 2.0);
        BaseToolTip = BuildToolTip(model, index);
    }

    public DeckButton Model { get; }
    public double Size { get; }
    public string Label { get; }
    public bool ShowLabel { get; }
    /// <summary>Symbole Fluent ou emoji (texte), selon <see cref="GlyphFont"/>.</summary>
    public string? Glyph { get; }
    public FontFamily GlyphFont { get; } = IconCatalog.Font;
    public FontWeight GlyphWeight { get; } = FontWeights.Normal;
    public static readonly FontFamily TextIconFont = new("Segoe UI Variable Display, Segoe UI");
    public bool HasGlyph => Glyph is not null && Image is null;
    public Geometry? Geometry { get; }
    public bool HasGeometry => Geometry is not null;
    public double GeometrySize { get; }
    public ImageSource? Image { get; }
    public bool HasImage => Image is not null;
    public double ImageSize { get; }
    /// <summary>Couleur du bouton (style plein) et couleur de contraste associée.</summary>
    public Brush Background { get; }
    public Brush Foreground { get; }
    public bool IsGlass { get; }
    public Brush TileBackground { get; }
    public Brush TileBorder { get; }
    public Brush? Sheen { get; }
    public Brush LabelForeground { get; }
    public System.Windows.Media.Effects.Effect? LabelEffect { get; }
    public Brush IconForeground { get; }
    /// <summary>Pastille d'icône (style verre) ; NaN = pas de pastille.</summary>
    public double BadgeSize { get; } = double.NaN;
    public Brush? BadgeBrush { get; }
    public CornerRadius BadgeCorner { get; }
    public System.Windows.Media.Effects.Effect? BadgeEffect { get; }
    public double GlyphSize { get; }
    public double LabelSize { get; }
    public double LabelMaxHeight { get; }
    public string? ShortcutHint { get; }
    public Thickness TileMargin { get; }
    public CornerRadius CornerRadius { get; }
    private string BaseToolTip { get; }

    public string ToolTip => IsRunning ? $"{BaseToolTip}\n\nEn cours{(_progressText is null ? "" : " : " + _progressText)} — cliquez pour annuler." : BaseToolTip;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(ToolTip));
        }
    }

    public string? ProgressText
    {
        get => _progressText;
        set
        {
            if (Set(ref _progressText, value)) OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>"", "ok" ou "error" : retour visuel bref après exécution.</summary>
    public string Flash
    {
        get => _flash;
        private set => Set(ref _flash, value);
    }

    public void ShowFlash(string kind)
    {
        Flash = kind;
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(kind == "error" ? 2500 : 900) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            Flash = "";
        };
        _flashTimer.Start();
    }

    private static string BuildToolTip(DeckButton b, int index)
    {
        var lines = new List<string> { string.IsNullOrWhiteSpace(b.Label) ? "(sans nom)" : b.Label };
        if (b.Actions.Count == 0) lines.Add("Aucune action — clic droit › Modifier");
        var n = 0;
        foreach (var a in b.Actions.Take(8))
        {
            n++;
            string d;
            try { d = a.Describe(); } catch { d = a.TypeId; }
            lines.Add($"{n}. {d}{(a.Enabled ? "" : " (désactivée)")}");
        }
        if (b.Actions.Count > 8) lines.Add($"… et {b.Actions.Count - 8} autre(s)");
        if (index < 9) lines.Add($"Raccourci : {index + 1}");
        return string.Join('\n', lines);
    }
}

public sealed class PanelViewModel : ObservableObject
{

    private readonly DeckRuntime _rt;
    private int _columns;
    private string? _statusText;
    private bool _statusIsError;
    private DispatcherTimer? _statusTimer;
    private string? _imageKey;

    public PanelViewModel(DeckRuntime runtime)
    {
        _rt = runtime;
        RunCommand = new RelayCommand(p =>
        {
            if (p is PanelButtonViewModel b) _ = _rt.RunAsync(b.Model, RunOrigin.Panel);
        });
        SelectProfileCommand = new RelayCommand(p =>
        {
            if (p is ProfileTab t) _rt.SwitchTo(t.Id);
        });
        EditCommand = new RelayCommand(() => EditorRequested?.Invoke(this, new EditorRequest(null, false)));
        EditButtonCommand = new RelayCommand(p =>
        {
            if (p is PanelButtonViewModel b) EditorRequested?.Invoke(this, new EditorRequest(b.Model.Id, false));
        });
        AddButtonCommand = new RelayCommand(() => EditorRequested?.Invoke(this, new EditorRequest(null, true)));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        _rt.ConfigChanged += (_, _) => Refresh();
        _rt.ActiveProfileChanged += (_, _) => Refresh();
        _rt.SettingsChanged += (_, _) => Refresh();
        _rt.RunStateChanged += OnRunStateChanged;
        Refresh();
    }

    public sealed record EditorRequest(Guid? ButtonId, bool AddNew);

    public event EventHandler<EditorRequest>? EditorRequested;
    public event EventHandler? CloseRequested;

    public ObservableCollection<ProfileTab> Profiles { get; } = [];
    public ObservableCollection<object> Tiles { get; } = [];

    public ICommand RunCommand { get; }
    public ICommand SelectProfileCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand EditButtonCommand { get; }
    public ICommand AddButtonCommand { get; }
    public ICommand CloseCommand { get; }

    public int Columns
    {
        get => _columns;
        private set => Set(ref _columns, value);
    }

    public bool IsEmpty => !Tiles.OfType<PanelButtonViewModel>().Any();

    /// <summary>Fond du panneau. Jamais totalement transparent (alpha ≥ 1) pour rester cliquable et déplaçable.</summary>
    public Brush PanelBackground { get; private set; } = Brushes.Transparent;
    public Brush? PanelBorder { get; private set; }
    public Brush? PanelImage { get; private set; }
    public Brush? PanelSheen { get; private set; }
    public Brush ChipBackground { get; private set; } = Brushes.Transparent;
    public Brush ChipBorder { get; private set; } = Brushes.Transparent;
    public Brush ChipForeground { get; private set; } = Brushes.Gray;
    public System.Windows.Media.Effects.Effect? TextShadow { get; private set; }
    /// <summary>Arrondi du panneau : 8 px avec le flou (rayon imposé par Windows 11, lissé par DWM), 16 px sinon.</summary>
    public CornerRadius PanelCorner { get; private set; } = new(8);

    /// <summary>Taille libre : la grille occupe la largeur disponible et les boutons se répartissent (colonne, rangée ou grille).</summary>
    public bool IsManualSize => _rt.Config.Settings.Panel.SizeMode == PanelSizeMode.Manual;

    /// <summary>Largeur de la grille : colonnes du profil (taille automatique) ou largeur disponible (NaN = étirée).</summary>
    public double TilesWidth { get; private set; } = double.NaN;
    /// <summary>Le pied n'apparaît que pour une erreur : panneau aussi compact que possible.</summary>
    public bool HasFooter => StatusIsError && !string.IsNullOrEmpty(StatusText);

    public bool HasSeveralProfiles => Profiles.Count > 1;

    private void RefreshAppearance()
    {
        var look = _rt.Config.Settings.Panel;
        var c = ColorHelper.Parse(look.BackgroundColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
        c.A = (byte)Math.Max(1, Math.Round(look.BackgroundOpacity * 255 / 100.0));
        PanelBackground = ColorHelper.Brush(c);
        PanelCorner = new CornerRadius(look.BlurBehind ? 8 : 16);
        var active = _rt.Config.ActiveProfile;
        TilesWidth = IsManualSize ? double.NaN : active.Columns * (active.ButtonSize + look.TileSpacing);
        OnPropertyChanged(nameof(PanelCorner));
        OnPropertyChanged(nameof(IsManualSize));
        OnPropertyChanged(nameof(TilesWidth));
        var glass = look.TileStyle == TileStyle.Glass;
        if (glass)
        {
            PanelBorder = look.BackgroundOpacity > 0 || look.BlurBehind ? Glass.PanelBorder : null;
            PanelSheen = look.BackgroundOpacity > 0 || look.BlurBehind ? Glass.PanelSheen : null;
            ChipBackground = Glass.ChipFill;
            ChipBorder = Glass.ChipBorder;
            ChipForeground = Glass.Text;
            TextShadow = Glass.TextShadow;
        }
        else
        {
            PanelBorder = look.BackgroundOpacity >= 15
                ? ColorHelper.Brush(Color.FromArgb((byte)Math.Min(60, look.BackgroundOpacity), 0x80, 0x80, 0x80))
                : null;
            PanelSheen = null;
            ChipBackground = Application.Current.TryFindResource("Pd.ChipBackground") as Brush ?? Brushes.Transparent;
            ChipBorder = Application.Current.TryFindResource("Pd.ChipBorder") as Brush ?? Brushes.Transparent;
            ChipForeground = Application.Current.TryFindResource("Pd.TextSecondary") as Brush ?? Brushes.Gray;
            TextShadow = null;
        }
        foreach (var n in new[] { nameof(PanelSheen), nameof(ChipBackground), nameof(ChipBorder), nameof(ChipForeground), nameof(TextShadow) })
            OnPropertyChanged(n);
        var imageKey = $"{look.BackgroundImage}|{look.BackgroundImageOpacity}";
        if (imageKey != _imageKey) // décodage seulement si l'image ou son opacité changent
        {
            _imageKey = imageKey;
            PanelImage = null;
            if (!string.IsNullOrWhiteSpace(look.BackgroundImage) && ImageLoader.Load(look.BackgroundImage, _rt.Paths.DataDirectory, 1600) is { } img)
            {
                var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill, Opacity = look.BackgroundImageOpacity / 100.0 };
                brush.Freeze();
                PanelImage = brush;
            }
        }
        OnPropertyChanged(nameof(PanelBackground));
        OnPropertyChanged(nameof(PanelBorder));
        OnPropertyChanged(nameof(PanelImage));
    }

    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value)) OnPropertyChanged(nameof(HasFooter));
        }
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set
        {
            if (Set(ref _statusIsError, value)) OnPropertyChanged(nameof(HasFooter));
        }
    }

    public void Refresh()
    {
        var cfg = _rt.Config;
        var active = cfg.ActiveProfile;
        Profiles.Clear();
        var i = 0;
        foreach (var p in cfg.Profiles) Profiles.Add(new ProfileTab(p.Id, p.Name, p.Id == active.Id, i++));
        OnPropertyChanged(nameof(HasSeveralProfiles));

        Tiles.Clear();
        var index = 0;
        foreach (var b in active.Buttons)
        {
            var vm = new PanelButtonViewModel(b, active.ButtonSize, index++, _rt.Paths.DataDirectory, _rt.Config.Settings.Panel)
            {
                IsRunning = _rt.IsRunning(b.Id),
            };
            Tiles.Add(vm);
        }
        // Tuile « Ajouter » seulement pour un profil vide (sinon : clic droit sur le fond, ou l'éditeur).
        if (active.Buttons.Count == 0) Tiles.Add(new AddTileViewModel(active.ButtonSize, _rt.Config.Settings.Panel));
        Columns = active.Columns;
        OnPropertyChanged(nameof(IsEmpty));
        RefreshAppearance();
    }

    private void OnRunStateChanged(object? sender, RunStateChangedEventArgs e)
    {
        var tile = Tiles.OfType<PanelButtonViewModel>().FirstOrDefault(t => t.Model.Id == e.Button.Id);
        if (tile is not null)
        {
            tile.IsRunning = e.IsRunning;
            tile.ProgressText = e.Progress is { } p ? $"étape {p.Index + 1}/{p.Count} — {p.Description}" : null;
        }
        if (e.IsRunning)
        {
            if (e.Progress is { Status: StepStatus.Running } pr && pr.Count > 1)
                SetStatus($"{e.Button.Label} : étape {pr.Index + 1}/{pr.Count}", false, persist: true);
            return;
        }
        if (e.Result is not { } r) return;
        switch (r.Status)
        {
            case SequenceStatus.Succeeded:
                tile?.ShowFlash("ok");
                SetStatus($"{e.Button.Label} : terminé", false);
                break;
            case SequenceStatus.Cancelled:
                SetStatus($"{e.Button.Label} : annulé", false);
                break;
            default:
                tile?.ShowFlash("error");
                SetStatus($"{e.Button.Label} : {r.FirstError}", true);
                break;
        }
    }

    public void SetStatus(string text, bool isError, bool persist = false)
    {
        StatusText = text;
        StatusIsError = isError;
        _statusTimer?.Stop();
        if (persist) return;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 12 : 4) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer?.Stop();
            StatusText = null;
        };
        _statusTimer.Start();
    }
}
