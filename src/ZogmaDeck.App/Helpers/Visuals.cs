using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Storage;

namespace ZogmaDeck.Helpers;

public sealed record IconGlyph(string Glyph, string Name);

/// <summary>Symboles de la police Segoe Fluent Icons (fournie avec Windows 11 ; Segoe MDL2 Assets en secours sur Windows 10).</summary>
public static class IconCatalog
{
    public const string FontFamilyName = "Segoe Fluent Icons, Segoe MDL2 Assets";

    public static readonly FontFamily Font = new(FontFamilyName);

    public static readonly IReadOnlyList<IconGlyph> Glyphs =
    [
        new("\uE768", "Lecture"), new("\uE769", "Pause"), new("\uE71A", "Arrêt"), new("\uE892", "Précédent"),
        new("\uE893", "Suivant"), new("\uE7C8", "Enregistrer"), new("\uE93E", "Diffusion"), new("\uE714", "Vidéo"),
        new("\uE722", "Appareil photo"), new("\uE7F4", "Écran"), new("\uE720", "Micro"), new("\uEC54", "Micro coupé"),
        new("\uE767", "Volume"), new("\uE74F", "Muet"), new("\uE993", "Volume bas"), new("\uE995", "Volume haut"),
        new("\uE8D6", "Musique"), new("\uE7F6", "Casque"), new("\uE8B7", "Dossier"), new("\uE8E5", "Ouvrir"),
        new("\uE8A5", "Document"), new("\uE7AC", "Application"), new("\uE774", "Web"), new("\uE715", "Courrier"),
        new("\uE8BD", "Message"), new("\uE8C8", "Copier"), new("\uE77F", "Coller"), new("\uE8C6", "Couper"),
        new("\uE70F", "Modifier"), new("\uE765", "Clavier"), new("\uE8EF", "Calculatrice"), new("\uE9D9", "Diagnostic"),
        new("\uE713", "Réglages"), new("\uE721", "Recherche"), new("\uE80F", "Accueil"), new("\uE8AB", "Changer"),
        new("\uE916", "Chronomètre"), new("\uE823", "Horloge"), new("\uE787", "Calendrier"), new("\uE945", "Éclair"),
        new("\uE734", "Favori"), new("\uE7E8", "Alimentation"), new("\uE72E", "Verrou"), new("\uE77B", "Contact"),
        new("\uE716", "Personnes"), new("\uE8F1", "Bibliothèque"), new("\uE7BA", "Attention"), new("\uE946", "Info"),
        new("\uE73E", "Valider"), new("\uE711", "Annuler"), new("\uE710", "Ajouter"), new("\uE738", "Retirer"),
        new("\uE72C", "Actualiser"), new("\uE7A7", "Défaire"), new("\uE7A6", "Rétablir"), new("\uE896", "Télécharger"),
        new("\uE898", "Envoyer"), new("\uE71B", "Lien"), new("\uE7EF", "Lumière"), new("\uE706", "Luminosité"),
        new("\uE790", "Couleurs"), new("\uE7FC", "Jeu"), new("\uE80A", "Grille"), new("\uE90F", "Outils"),
    ];
}

public static class ColorHelper
{
    public static readonly IReadOnlyList<string> Palette =
    [
        "#2F6FDB", "#5B5FC7", "#6B46C1", "#8E44AD", "#C2185B", "#C42B1C", "#D35400", "#C27C0E",
        "#D69E2E", "#2E7D32", "#0F7B6C", "#2C7A7B", "#0E7490", "#4A5568", "#1F2937", "#7F8C8D",
    ];

    public static Color Parse(string? hex, Color fallback)
    {
        if (!ConfigValidator.IsValidColor(hex)) return fallback;
        var v = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex!.Length == 7
            ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Texte blanc ou quasi noir selon la luminance relative (WCAG) du fond.</summary>
    public static Color ContrastingForeground(Color bg)
    {
        static double Lin(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var l = 0.2126 * Lin(bg.R) + 0.7152 * Lin(bg.G) + 0.0722 * Lin(bg.B);
        return (1.05) / (l + 0.05) >= (l + 0.05) / 0.05 ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
    }
}

public static class ImageLoader
{
    /// <summary>Charge une image sans verrouiller le fichier. Retourne null si absente ou illisible.</summary>
    public static ImageSource? Load(string? path, string dataDirectory, int decodeWidth)
    {
        var full = ResourcePaths.Resolve(path, dataDirectory);
        if (full.Length == 0 || !File.Exists(full)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(full);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Image illisible : {full}");
            return null;
        }
    }

    /// <summary>Copie une image dans le dossier assets (nom basé sur le contenu) et retourne le chemin relatif à stocker.</summary>
    public static string ImportToAssets(string sourcePath, string assetsDirectory)
    {
        Directory.CreateDirectory(assetsDirectory);
        using var s = File.OpenRead(sourcePath);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s))[..20];
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var name = hash + (ext.Length is > 1 and <= 6 ? ext : ".img");
        var dest = Path.Combine(assetsDirectory, name);
        if (!File.Exists(dest)) File.Copy(sourcePath, dest);
        return "assets/" + name;
    }
}
