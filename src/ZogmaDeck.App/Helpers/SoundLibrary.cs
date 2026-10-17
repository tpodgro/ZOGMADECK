using System.IO;
using System.Windows;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Helpers;

/// <summary>Son de la bibliothèque intégrée (sons originaux de synthèse, tools/generate-sounds.py, domaine public).</summary>
public sealed record LibrarySound(string File, string Title, string Category)
{
    public string Display => $"{Category} — {Title}";
}

/// <summary>
/// Bibliothèque de sons livrée avec ZogmaDeck. Un son choisi est copié dans assets/sounds du dossier de données :
/// la configuration reste autonome et s'exporte avec ses médias comme un fichier ajouté par l'utilisateur.
/// </summary>
public static class SoundLibrary
{
    public const string AssetsFolder = "assets/sounds";

    /// <summary>Banques de sons gratuites en ligne (licences variables : vérifiez celle de chaque son).</summary>
    public static IReadOnlyList<(string Name, string Url)> OnlineSources { get; } =
    [
        ("Pixabay — effets sonores", "https://pixabay.com/sound-effects/"),
        ("Freesound", "https://freesound.org/"),
    ];

    private static readonly Lazy<IReadOnlyList<LibrarySound>> Catalog = new(Load);

    public static IReadOnlyList<LibrarySound> Sounds => Catalog.Value;

    private static IReadOnlyList<LibrarySound> Load()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Sounds/sounds.tsv"));
            if (info is null) return [];
            using var reader = new StreamReader(info.Stream);
            var list = new List<LibrarySound>();
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith('#') || line.Length == 0) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 3) list.Add(new LibrarySound(parts[0], parts[1], parts[2]));
            }
            return list.OrderBy(s => s.Category, StringComparer.CurrentCulture).ThenBy(s => s.Title, StringComparer.CurrentCulture).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Log.Warn("Bibliothèque de sons illisible", ex);
            return [];
        }
    }

    /// <summary>Copie le son dans le dossier de données et retourne son chemin relatif (« assets/sounds/xxx.wav »).</summary>
    public static string Install(LibrarySound sound, string dataDirectory)
    {
        var relative = $"{AssetsFolder}/{sound.File}";
        var target = Path.Combine(dataDirectory, "assets", "sounds", sound.File);
        Extract(sound, target);
        return relative;
    }

    /// <summary>Copie temporaire pour l'écoute (le son n'est ajouté aux données que s'il est choisi).</summary>
    public static string ExtractForPreview(LibrarySound sound)
    {
        var target = Path.Combine(Path.GetTempPath(), "ZogmaDeck", "sounds", sound.File);
        Extract(sound, target);
        return target;
    }

    private static void Extract(LibrarySound sound, string target)
    {
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Sounds/{sound.File}"))
                   ?? throw new FileNotFoundException("Son introuvable dans la bibliothèque.", sound.File);
        var tmp = target + ".tmp";
        using (var src = info.Stream)
        using (var dst = File.Create(tmp))
            src.CopyTo(dst);
        File.Move(tmp, target, overwrite: true);
    }
}
