using System.Globalization;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Core.Model;

namespace ZogmaDeck.Core.Storage;

/// <summary>Corrige les valeurs invalides ou manquantes d'une configuration chargée, et décrit chaque correction.</summary>
public static class ConfigValidator
{
    public static List<string> Normalize(DeckConfig config)
    {
        var w = new List<string>();
        config.SchemaVersion = Math.Max(config.SchemaVersion, DeckConfig.CurrentSchemaVersion);

        config.Settings ??= new AppSettings();
        var s = config.Settings;
        if (!KeyChord.TryParse(s.Hotkey, out var hk, out var err) || !hk.IsSingleKey)
        {
            if (err.Length == 0) err = "une seule touche principale est possible";
            w.Add($"Raccourci global invalide ({err}) : retour à {AppSettings.DefaultHotkey}.");
            s.Hotkey = AppSettings.DefaultHotkey;
        }
        if (!Enum.IsDefined(s.Theme)) s.Theme = ThemePreference.System;
        s.Obs ??= new ObsSettings();
        if (string.IsNullOrWhiteSpace(s.Obs.Host)) s.Obs.Host = "localhost";
        if (s.Obs.Port is < 1 or > 65535)
        {
            w.Add($"Port OBS invalide ({s.Obs.Port}) : retour à 4455.");
            s.Obs.Port = 4455;
        }
        s.Obs.RequestTimeoutSeconds = Math.Clamp(s.Obs.RequestTimeoutSeconds, 1, 120);

        s.Panel ??= new PanelAppearance();
        var pa = s.Panel;
        if (!IsValidColor(pa.BackgroundColor)) pa.BackgroundColor = PanelAppearance.DefaultBackground;
        pa.BackgroundOpacity = Math.Clamp(pa.BackgroundOpacity, 0, 100);
        pa.BackgroundImageOpacity = Math.Clamp(pa.BackgroundImageOpacity, 0, 100);
        pa.IconScale = Math.Clamp(pa.IconScale, 40, 200);
        pa.LabelScale = Math.Clamp(pa.LabelScale, 60, 180);
        pa.TileSpacing = Math.Clamp(pa.TileSpacing, 0, 32);
        pa.TileCornerRadius = Math.Clamp(pa.TileCornerRadius, 0, 40);
        if (!Enum.IsDefined(pa.Position)) pa.Position = PanelPositionMode.Centered;
        if (!Enum.IsDefined(pa.TileStyle)) pa.TileStyle = TileStyle.Glass;
        if (!Enum.IsDefined(pa.SizeMode)) pa.SizeMode = PanelSizeMode.Auto;
        pa.PanelWidth = double.IsFinite(pa.PanelWidth) ? Math.Clamp(pa.PanelWidth, PanelAppearance.MinPanelSize, PanelAppearance.MaxPanelSize) : 440;
        pa.PanelHeight = double.IsFinite(pa.PanelHeight) ? Math.Clamp(pa.PanelHeight, PanelAppearance.MinPanelSize, PanelAppearance.MaxPanelSize) : 420;
        pa.PositionX = double.IsFinite(pa.PositionX) ? Math.Clamp(pa.PositionX, 0, 1) : 0.5;
        pa.PositionY = double.IsFinite(pa.PositionY) ? Math.Clamp(pa.PositionY, 0, 1) : 0.5;

        config.Profiles ??= [];
        config.Profiles.RemoveAll(p => p is null);
        if (config.Profiles.Count == 0)
        {
            w.Add("Aucun profil : un profil vide a été créé.");
            config.Profiles.Add(new Profile { Name = "Profil" });
        }

        var profileIds = new HashSet<Guid>();
        var buttonIds = new HashSet<Guid>();
        var index = 0;
        foreach (var p in config.Profiles)
        {
            index++;
            if (p.Id == Guid.Empty || !profileIds.Add(p.Id))
            {
                p.Id = Guid.NewGuid();
                profileIds.Add(p.Id);
                w.Add($"Identifiant de profil dupliqué ou manquant corrigé (profil {index}).");
            }
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = $"Profil {index}";
            p.Columns = Clamp(p.Columns, Profile.MinColumns, Profile.MaxColumns, Profile.DefaultColumns);
            p.ButtonSize = Clamp(p.ButtonSize, Profile.MinButtonSize, Profile.MaxButtonSize, Profile.DefaultButtonSize);
            p.Buttons ??= [];
            p.Buttons.RemoveAll(b => b is null);
            foreach (var b in p.Buttons)
            {
                if (b.Id == Guid.Empty || !buttonIds.Add(b.Id))
                {
                    b.Id = Guid.NewGuid();
                    buttonIds.Add(b.Id);
                }
                b.Label ??= "";
                b.Icon ??= new ButtonIcon();
                if (!Enum.IsDefined(b.Icon.Kind)) b.Icon.Kind = IconKind.None;
                if (!Enum.IsDefined(b.HideAfterRun)) b.HideAfterRun = HideBehavior.Default;
                if (!IsValidColor(b.Color))
                {
                    if (!string.IsNullOrEmpty(b.Color)) w.Add($"Couleur invalide « {b.Color} » sur « {b.Label} » : couleur par défaut appliquée.");
                    b.Color = DeckButton.DefaultColor;
                }
                b.Actions ??= [];
                b.Actions.RemoveAll(a => a is null);
                foreach (var a in b.Actions)
                    if (!Enum.IsDefined(a.OnError)) a.OnError = ErrorPolicy.Stop;
            }
        }

        if (config.FindProfile(config.ActiveProfileId) is null) config.ActiveProfileId = config.Profiles[0].Id;
        return w;
    }

    private static int Clamp(int value, int min, int max, int fallback) => value == 0 ? fallback : Math.Clamp(value, min, max);

    /// <summary>Accepte #RRGGBB ou #AARRGGBB.</summary>
    public static bool IsValidColor(string? color) =>
        color is { Length: 7 or 9 } && color[0] == '#' &&
        uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
}
