using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Core.Model;

/// <summary>Racine de la configuration persistée (config.json).</summary>
public sealed class DeckConfig
{
    /// <summary>Version actuelle du schéma. À incrémenter avec une étape de migration dans <see cref="Storage.ConfigMigrator"/>.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public AppSettings Settings { get; set; } = new();
    public List<Profile> Profiles { get; set; } = [];
    public Guid ActiveProfileId { get; set; }

    public Profile? FindProfile(Guid id) => Profiles.FirstOrDefault(p => p.Id == id);

    public Profile ActiveProfile => FindProfile(ActiveProfileId) ?? Profiles[0];
}

public enum ThemePreference
{
    [Label("Selon Windows")] System,
    [Label("Clair")] Light,
    [Label("Sombre")] Dark,
}

public sealed class AppSettings
{
    public const string DefaultHotkey = "Ctrl+Alt+Space";

    public string Hotkey { get; set; } = DefaultHotkey;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool AlwaysOnTop { get; set; }
    public bool StartWithWindows { get; set; }
    public bool HideAfterAction { get; set; }
    public bool HideOnDeactivate { get; set; }
    /// <summary>Rendu WPF par le GPU (prend effet au redémarrage). Désactivé par défaut.</summary>
    public bool HardwareAcceleration { get; set; }
    public ObsSettings Obs { get; set; } = new();
    public PanelAppearance Panel { get; set; } = new();
}

public enum PanelPositionMode
{
    [Label("Centré sur l'écran actif")] Centered,
    [Label("Position mémorisée (déplacée à la souris)")] Remembered,
}

public enum PanelSizeMode
{
    [Label("Automatique (selon le nombre de colonnes du profil)")] Auto,
    [Label("Libre (redimensionné à la souris)")] Manual,
}

public enum TileStyle
{
    [Label("Verre (translucide, pastille colorée)")] Glass,
    [Label("Couleur pleine")] Solid,
}

/// <summary>Apparence et placement du panneau rapide. Par défaut : verre dépoli (blanc à 30 % + flou de l'arrière-plan).</summary>
public sealed class PanelAppearance
{
    public const string DefaultBackground = "#FFFFFF";
    public const int DefaultOpacity = 20;
    public const double MinPanelSize = 60, MaxPanelSize = 4000;

    /// <summary>Couleur du voile (#RRGGBB), blanc par défaut.</summary>
    public string BackgroundColor { get; set; } = DefaultBackground;
    /// <summary>Opacité du voile, 0 (transparent) à 100 ; 20 par défaut.</summary>
    public int BackgroundOpacity { get; set; } = DefaultOpacity;
    /// <summary>Flou de ce qui se trouve derrière le panneau (verre dépoli).</summary>
    public bool BlurBehind { get; set; } = true;
    public TileStyle TileStyle { get; set; } = TileStyle.Glass;
    /// <summary>Image de fond facultative (copiée dans assets/).</summary>
    public string? BackgroundImage { get; set; }
    public int BackgroundImageOpacity { get; set; } = 100;
    /// <summary>Taille des icônes dans les boutons, en % de la taille normale (40–200).</summary>
    public int IconScale { get; set; } = 100;
    /// <summary>Taille des libellés, en % (60–180).</summary>
    public int LabelScale { get; set; } = 100;
    /// <summary>Espace entre les boutons, en pixels (0–32).</summary>
    public int TileSpacing { get; set; } = 8;
    /// <summary>Arrondi des boutons, en pixels (0–40).</summary>
    public int TileCornerRadius { get; set; } = 12;
    /// <summary>Auto : la largeur suit les colonnes du profil. Manual : taille choisie à la souris, les boutons se répartissent.</summary>
    public PanelSizeMode SizeMode { get; set; } = PanelSizeMode.Auto;
    /// <summary>Taille libre du panneau, en unités indépendantes du DPI.</summary>
    public double PanelWidth { get; set; } = 440;
    public double PanelHeight { get; set; } = 420;
    public bool ShowShortcutNumbers { get; set; } = true;
    public PanelPositionMode Position { get; set; } = PanelPositionMode.Centered;
    /// <summary>Centre mémorisé du panneau, en fraction de la zone de travail de l'écran (0–1).</summary>
    public double PositionX { get; set; } = 0.5;
    public double PositionY { get; set; } = 0.5;
}

/// <summary>Paramètres OBS non secrets. Le mot de passe est stocké à part, chiffré (DPAPI), jamais dans config.json.</summary>
public sealed class ObsSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4455;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public bool ConnectOnStartup { get; set; }
}

public sealed class Profile
{
    public const int MinColumns = 1, MaxColumns = 12, DefaultColumns = 4;
    public const int MinButtonSize = 48, MaxButtonSize = 200, DefaultButtonSize = 92;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Profil";
    public int Columns { get; set; } = DefaultColumns;
    public int ButtonSize { get; set; } = DefaultButtonSize;
    public List<DeckButton> Buttons { get; set; } = [];
}

public enum IconKind
{
    None,
    /// <summary>Symbole de la police Segoe Fluent Icons.</summary>
    Glyph,
    Image,
    /// <summary>Emoji de Windows (police Segoe UI Emoji).</summary>
    Emoji,
    /// <summary>Logo d'application ou de service (catalogue intégré, identifiant dans <see cref="ButtonIcon.Glyph"/>).</summary>
    Brand,
}

public sealed class ButtonIcon
{
    public IconKind Kind { get; set; } = IconKind.None;
    /// <summary>Selon <see cref="Kind"/> : caractère Segoe Fluent Icons, emoji, ou identifiant de logo.</summary>
    public string? Glyph { get; set; }
    /// <summary>Chemin absolu, ou relatif au dossier de données (ex. "assets/xxx.png").</summary>
    public string? ImagePath { get; set; }

    public static ButtonIcon FromGlyph(string glyph) => new() { Kind = IconKind.Glyph, Glyph = glyph };
}

public enum HideBehavior
{
    [Label("Selon le réglage général")] Default,
    [Label("Masquer le panneau")] Hide,
    [Label("Garder le panneau ouvert")] KeepOpen,
}

public sealed class DeckButton
{
    public const string DefaultColor = "#2F6FDB";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = "Bouton";
    public bool ShowLabel { get; set; } = true;
    public ButtonIcon Icon { get; set; } = new();
    public string Color { get; set; } = DefaultColor;
    public HideBehavior HideAfterRun { get; set; } = HideBehavior.Default;
    public List<DeckAction> Actions { get; set; } = [];
}
