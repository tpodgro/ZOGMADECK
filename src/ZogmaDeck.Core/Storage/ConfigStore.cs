using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Model;

namespace ZogmaDeck.Core.Storage;

public sealed record ConfigLoadResult(DeckConfig? Config, bool IsFirstRun, IReadOnlyList<string> Warnings, bool RecoveredFromBackup);

/// <summary>
/// Persistance de config.json : écriture atomique (fichier temporaire + remplacement), copie .bak de la version précédente,
/// instantanés quotidiens dans backups/, récupération automatique en cas de fichier corrompu.
/// </summary>
public sealed class ConfigStore
{
    private const int MaxSnapshots = 10;
    private readonly Lock _writeLock = new();

    public ConfigStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
    public string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public string BackupPath => ConfigPath + ".bak";
    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
    public string AssetsDirectory => Path.Combine(DataDirectory, "assets");

    public ConfigLoadResult Load()
    {
        var warnings = new List<string>();
        var hasPrimary = File.Exists(ConfigPath);
        var hasBackup = File.Exists(BackupPath);
        if (!hasPrimary && !hasBackup) return new(null, true, warnings, false);

        if (hasPrimary)
        {
            try
            {
                var cfg = ConfigSerializer.Deserialize(File.ReadAllText(ConfigPath), warnings);
                if (cfg.SchemaVersion > DeckConfig.CurrentSchemaVersion || warnings.Count > 0)
                    CreateSnapshot("avant-correction");
                return new(cfg, false, warnings, false);
            }
            catch (Exception ex) when (ex is ConfigFormatException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Configuration principale illisible", ex);
                var kept = QuarantineCorruptFile();
                warnings.Add($"La configuration était illisible ({ex.Message}). Copie conservée : {kept}");
            }
        }

        if (hasBackup)
        {
            try
            {
                var cfg = ConfigSerializer.Deserialize(File.ReadAllText(BackupPath), warnings);
                warnings.Add("La configuration précédente (sauvegarde .bak) a été restaurée.");
                return new(cfg, false, warnings, true);
            }
            catch (Exception ex) when (ex is ConfigFormatException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Sauvegarde .bak illisible", ex);
                warnings.Add("La sauvegarde .bak est également illisible : configuration réinitialisée.");
            }
        }
        return new(null, false, warnings, false);
    }

    public void Save(DeckConfig config) => WriteJson(ConfigSerializer.Serialize(config));

    /// <summary>Écrit le JSON de façon atomique. Sûr depuis n'importe quel thread.</summary>
    public void WriteJson(string json)
    {
        lock (_writeLock)
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = ConfigPath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(ConfigPath))
                File.Replace(tmp, ConfigPath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(tmp, ConfigPath);

            TryDailySnapshot();
        }
    }

    /// <summary>Copie horodatée de la configuration actuelle dans backups/. Retourne le chemin, ou null s'il n'y a rien à copier.</summary>
    public string? CreateSnapshot(string reason)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            Directory.CreateDirectory(BackupsDirectory);
            var path = Path.Combine(BackupsDirectory, $"config-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.json");
            File.Copy(ConfigPath, path, overwrite: true);
            Prune();
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Instantané de configuration impossible", ex);
            return null;
        }
    }

    public IReadOnlyList<FileInfo> ListSnapshots() =>
        Directory.Exists(BackupsDirectory)
            ? new DirectoryInfo(BackupsDirectory).GetFiles("config-*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];

    private void TryDailySnapshot()
    {
        try
        {
            Directory.CreateDirectory(BackupsDirectory);
            var today = Path.Combine(BackupsDirectory, $"config-{DateTime.Now:yyyyMMdd}-quotidien.json");
            if (!File.Exists(today))
            {
                File.Copy(ConfigPath, today);
                Prune();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Instantané quotidien impossible", ex);
        }
    }

    private void Prune()
    {
        foreach (var f in ListSnapshots().Skip(MaxSnapshots))
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string QuarantineCorruptFile()
    {
        try
        {
            Directory.CreateDirectory(BackupsDirectory);
            var dest = Path.Combine(BackupsDirectory, $"config-{DateTime.Now:yyyyMMdd-HHmmss}-corrompu.json");
            File.Copy(ConfigPath, dest, overwrite: true);
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ConfigPath;
        }
    }
}

public static class ResourcePaths
{
    public const string PackagePrefix = "package:";

    /// <summary>Résout un chemin stocké : variables d'environnement, puis chemin relatif au dossier de données.</summary>
    public static string Resolve(string? path, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (Path.IsPathFullyQualified(p) || string.IsNullOrEmpty(dataDirectory)) return p;
        if (p.IndexOfAny(['\\', '/']) < 0 && !File.Exists(Path.Combine(dataDirectory, p))) return p;
        return Path.GetFullPath(Path.Combine(dataDirectory, p));
    }

    /// <summary>Vrai si le fichier ou le dossier existe.</summary>
    public static bool Exists(string? path, string dataDirectory)
    {
        var full = Resolve(path, dataDirectory);
        return full.Length > 0 && (File.Exists(full) || Directory.Exists(full));
    }
}
