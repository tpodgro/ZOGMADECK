using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Model;

namespace ZogmaDeck.Core.Storage;

public sealed record ResourceUse(string ProfileName, string ButtonLabel, ResourceSlot Slot);

public static class ConfigResources
{
    /// <summary>Ressources des profils et des réglages (image de fond du panneau).</summary>
    public static IEnumerable<ResourceUse> Enumerate(DeckConfig config)
    {
        var panel = config.Settings.Panel;
        if (!string.IsNullOrWhiteSpace(panel.BackgroundImage))
            yield return new("Réglages", "Fond du panneau", new ResourceSlot(ResourceKind.Image, "Image de fond",
                () => panel.BackgroundImage, v => panel.BackgroundImage = v));
        foreach (var use in Enumerate(config.Profiles)) yield return use;
    }

    /// <summary>Toutes les références de fichiers d'une configuration (icônes, sons, programmes, documents).</summary>
    public static IEnumerable<ResourceUse> Enumerate(IEnumerable<Profile> profiles)
    {
        foreach (var p in profiles)
        foreach (var b in p.Buttons)
        {
            var button = b;
            if (button.Icon.Kind == IconKind.Image && !string.IsNullOrWhiteSpace(button.Icon.ImagePath))
                yield return new(p.Name, b.Label, new ResourceSlot(ResourceKind.Image, "Image du bouton",
                    () => button.Icon.ImagePath, v => button.Icon.ImagePath = v));
            foreach (var a in b.Actions)
            foreach (var slot in a.GetResources())
                if (!string.IsNullOrWhiteSpace(slot.Path))
                    yield return new(p.Name, b.Label, slot);
        }
    }
}

public sealed record ExportOptions(IReadOnlyCollection<Guid>? ProfileIds, bool IncludeSettings);

public sealed record ExportReport(int ProfileCount, int ButtonCount, IReadOnlyList<string> BundledFiles, IReadOnlyList<string> MissingFiles);

public enum ImportMode
{
    /// <summary>Ajoute les profils importés à ceux existants.</summary>
    Merge,
    /// <summary>Remplace tous les profils (un instantané est créé avant).</summary>
    Replace,
}

public sealed record MissingResource(string ProfileName, string ButtonLabel, ResourceKind Kind, string Path)
{
    public string KindLabel => Kind switch
    {
        ResourceKind.Image => "Image",
        ResourceKind.Media => "Son",
        ResourceKind.Program => "Programme",
        _ => "Fichier/dossier",
    };
}

public sealed class ImportPreview
{
    public required DeckConfig Config { get; init; }
    public required bool IncludesSettings { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<MissingResource> Missing { get; init; }
    public required string SourcePath { get; init; }
    public DateTimeOffset? ExportedAt { get; init; }
    public int ButtonCount => Config.Profiles.Sum(p => p.Buttons.Count);
    /// <summary>Étapes qui exécutent du code (scripts) : signalées avant l'import, jamais exécutées par celui-ci.</summary>
    public int ScriptCount => Config.Profiles.SelectMany(p => p.Buttons).SelectMany(b => b.Actions).Count(a => a is ScriptAction);
    internal IReadOnlyDictionary<string, byte[]> PackageAssets { get; init; } = new Dictionary<string, byte[]>();
}

public sealed record ImportResult(IReadOnlyList<string> ImportedProfiles, int ButtonCount, int ExtractedFiles, bool SettingsImported);

/// <summary>
/// Format d'échange .zogmadeck : archive zip contenant manifest.json, config.json et assets/ (images et sons).
/// Le mot de passe OBS n'est jamais dans config.json (stocké à part, chiffré), il ne peut donc pas être exporté.
/// L'import ne fait que modifier la configuration : aucune action n'est jamais exécutée.
/// </summary>
public static class DeckPackage
{
    public const string Extension = ".zogmadeck";
    public const int FormatVersion = 1;
    private const long MaxConfigBytes = 20L * 1024 * 1024;
    private const long MaxAssetBytes = 200L * 1024 * 1024;
    private const long MaxTotalBytes = 1024L * 1024 * 1024;
    private const int MaxEntries = 5000;

    public static ExportReport Export(DeckConfig source, string zipPath, ExportOptions options, string dataDirectory)
    {
        var clone = ConfigSerializer.DeepClone(source);
        if (options.ProfileIds is { Count: > 0 } ids)
            clone.Profiles = clone.Profiles.Where(p => ids.Contains(p.Id)).ToList();
        if (clone.Profiles.Count == 0) throw new InvalidOperationException("Aucun profil à exporter.");
        clone.ActiveProfileId = clone.Profiles[0].Id;
        clone.Settings = options.IncludeSettings ? clone.Settings : new AppSettings();
        clone.Settings.StartWithWindows = false; // propre à chaque poste

        var bundled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // entrée zip -> fichier source
        var byFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // fichier source -> entrée zip
        var missing = new List<string>();

        foreach (var use in ConfigResources.Enumerate(clone))
        {
            if (!use.Slot.IsBundled) continue;
            var full = ResourcePaths.Resolve(use.Slot.Path, dataDirectory);
            if (!File.Exists(full))
            {
                missing.Add($"{use.ProfileName} › {use.ButtonLabel} : {use.Slot.Path}");
                continue;
            }
            if (!byFile.TryGetValue(full, out var entry))
            {
                entry = "assets/" + HashFile(full) + SafeExtension(full);
                byFile[full] = entry;
                bundled[entry] = full;
            }
            use.Slot.Path = ResourcePaths.PackagePrefix + entry;
        }

        var manifest = new JsonObject
        {
            ["format"] = "zogmadeck",
            ["formatVersion"] = FormatVersion,
            ["exportedAt"] = DateTimeOffset.Now.ToString("o"),
            ["includesSettings"] = options.IncludeSettings,
            ["profiles"] = new JsonArray(clone.Profiles.Select(p => (JsonNode)JsonValue.Create(p.Name)!).ToArray()),
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(zipPath) + ".tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteText(zip, "manifest.json", manifest.ToJsonString(JsonDefaults.Options));
            WriteText(zip, "config.json", ConfigSerializer.Serialize(clone));
            foreach (var (entry, file) in bundled)
                zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
        }
        File.Move(tmp, zipPath, overwrite: true);

        return new(clone.Profiles.Count, clone.Profiles.Sum(p => p.Buttons.Count),
            bundled.Values.Select(Path.GetFileName).ToList()!, missing);
    }

    /// <summary>Lit et valide un fichier .zogmadeck (ou un config.json brut) sans rien modifier.</summary>
    public static ImportPreview ReadPackage(string path, string dataDirectory)
    {
        var warnings = new List<string>();
        string configJson;
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var includesSettings = true;
        DateTimeOffset? exportedAt = null;

        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(path).Length > MaxConfigBytes) throw new ConfigFormatException("Fichier de configuration trop volumineux.");
            configJson = File.ReadAllText(path);
        }
        else
        {
            using var zip = OpenZip(path);
            if (zip.Entries.Count > MaxEntries) throw new ConfigFormatException("Archive invalide : trop de fichiers.");
            long total = 0;
            string? cfg = null;
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue; // dossier
                total += e.Length;
                if (total > MaxTotalBytes) throw new ConfigFormatException("Archive invalide : contenu trop volumineux.");
                if (name == "config.json")
                {
                    if (e.Length > MaxConfigBytes) throw new ConfigFormatException("config.json trop volumineux.");
                    cfg = ReadText(e);
                }
                else if (name == "manifest.json")
                {
                    if (e.Length > MaxConfigBytes) continue;
                    try
                    {
                        var m = JsonNode.Parse(ReadText(e), documentOptions: JsonDefaults.DocumentOptions) as JsonObject;
                        if (m?["format"]?.GetValue<string>() is not ("zogmadeck" or "pocketdeck")) // pocketdeck : ancien nom
                            warnings.Add("Manifeste non reconnu : import tenté quand même.");
                        if (m?["includesSettings"] is JsonValue inc && inc.TryGetValue<bool>(out var b)) includesSettings = b;
                        if (m?["exportedAt"] is JsonValue d && DateTimeOffset.TryParse(d.GetValue<string>(), out var dt)) exportedAt = dt;
                        if (m?["formatVersion"] is JsonValue fv && fv.TryGetValue<int>(out var ver) && ver > FormatVersion)
                            warnings.Add("Archive créée par une version plus récente de ZogmaDeck.");
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        warnings.Add("Manifeste illisible : ignoré.");
                    }
                }
                else if (IsSafeAssetEntry(name))
                {
                    if (e.Length > MaxAssetBytes)
                    {
                        warnings.Add($"Fichier ignoré (trop volumineux) : {name}");
                        continue;
                    }
                    assets[name] = ReadBounded(e, MaxAssetBytes);
                }
                else
                {
                    warnings.Add($"Élément inattendu ignoré dans l'archive : {name}");
                }
            }
            configJson = cfg ?? throw new ConfigFormatException("Archive invalide : config.json absent.");
        }

        var config = ConfigSerializer.Deserialize(configJson, warnings);
        var missing = new List<MissingResource>();
        foreach (var use in ConfigResources.Enumerate(config))
        {
            var p = use.Slot.Path!;
            var ok = p.StartsWith(ResourcePaths.PackagePrefix, StringComparison.Ordinal)
                ? assets.ContainsKey(p[ResourcePaths.PackagePrefix.Length..])
                : ResourcePaths.Exists(p, dataDirectory) || (use.Slot.Kind == ResourceKind.Program && IsBareProgramName(p));
            if (!ok) missing.Add(new(use.ProfileName, use.ButtonLabel, use.Slot.Kind, p));
        }

        return new ImportPreview
        {
            Config = config,
            IncludesSettings = includesSettings,
            Warnings = warnings,
            Missing = missing,
            SourcePath = path,
            ExportedAt = exportedAt,
            PackageAssets = assets,
        };
    }

    /// <summary>Applique un import à la configuration cible. Ne lance aucune action.</summary>
    public static ImportResult Apply(ImportPreview preview, DeckConfig target, ImportMode mode, bool importSettings, string dataDirectory)
    {
        // Copie de travail : la prévisualisation reste réutilisable.
        var incoming = ConfigSerializer.DeepClone(preview.Config);
        var assetsDir = Path.Combine(dataDirectory, "assets");
        var extracted = 0;

        foreach (var use in ConfigResources.Enumerate(incoming))
        {
            var p = use.Slot.Path!;
            if (!p.StartsWith(ResourcePaths.PackagePrefix, StringComparison.Ordinal)) continue;
            var entry = p[ResourcePaths.PackagePrefix.Length..];
            var fileName = Path.GetFileName(entry);
            if (preview.PackageAssets.TryGetValue(entry, out var bytes))
            {
                Directory.CreateDirectory(assetsDir);
                var dest = Path.Combine(assetsDir, fileName);
                if (!File.Exists(dest) || new FileInfo(dest).Length != bytes.Length)
                {
                    File.WriteAllBytes(dest, bytes);
                    extracted++;
                }
            }
            use.Slot.Path = "assets/" + fileName;
        }

        if (mode == ImportMode.Replace)
        {
            target.Profiles = incoming.Profiles;
            target.ActiveProfileId = incoming.Profiles[0].Id;
        }
        else
        {
            MergeProfiles(target, incoming.Profiles);
        }

        var settingsImported = false;
        if (importSettings && preview.IncludesSettings)
        {
            var s = incoming.Settings;
            target.Settings.Hotkey = s.Hotkey;
            target.Settings.Theme = s.Theme;
            target.Settings.AlwaysOnTop = s.AlwaysOnTop;
            target.Settings.HideAfterAction = s.HideAfterAction;
            target.Settings.HideOnDeactivate = s.HideOnDeactivate;
            target.Settings.HardwareAcceleration = s.HardwareAcceleration;
            target.Settings.AccentColor = s.AccentColor;
            target.Settings.Panel = s.Panel; // apparence complète, image de fond déjà extraite
            target.Settings.Obs.Host = s.Obs.Host;
            target.Settings.Obs.Port = s.Obs.Port;
            target.Settings.Obs.RequestTimeoutSeconds = s.Obs.RequestTimeoutSeconds;
            settingsImported = true;
        }

        ConfigValidator.Normalize(target);
        return new(incoming.Profiles.Select(p => p.Name).ToList(), incoming.Profiles.Sum(p => p.Buttons.Count), extracted, settingsImported);
    }

    private static void MergeProfiles(DeckConfig target, List<Profile> incoming)
    {
        var usedProfileIds = target.Profiles.Select(p => p.Id).ToHashSet();
        var usedButtonIds = target.Profiles.SelectMany(p => p.Buttons).Select(b => b.Id).ToHashSet();
        var usedNames = target.Profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var idMap = new Dictionary<Guid, Guid>();

        foreach (var p in incoming)
        {
            if (usedProfileIds.Contains(p.Id))
            {
                var newId = Guid.NewGuid();
                idMap[p.Id] = newId;
                p.Id = newId;
            }
            usedProfileIds.Add(p.Id);
            p.Name = UniqueName(p.Name, usedNames);
            usedNames.Add(p.Name);
            foreach (var b in p.Buttons)
            {
                if (!usedButtonIds.Add(b.Id))
                {
                    b.Id = Guid.NewGuid();
                    usedButtonIds.Add(b.Id);
                }
            }
        }

        // Les boutons « changer de profil » pointent vers les nouveaux identifiants.
        foreach (var a in incoming.SelectMany(p => p.Buttons).SelectMany(b => b.Actions).OfType<SwitchProfileAction>())
            if (a.ProfileId is { } old && idMap.TryGetValue(old, out var mapped))
                a.ProfileId = mapped;

        target.Profiles.AddRange(incoming);
    }

    public static string UniqueName(string name, ISet<string> used)
    {
        if (!used.Contains(name)) return name;
        var candidate = $"{name} (importé)";
        for (var i = 2; used.Contains(candidate); i++) candidate = $"{name} (importé {i})";
        return candidate;
    }

    private static bool IsBareProgramName(string p) => p.IndexOfAny(['\\', '/', ':']) < 0;

    private static bool IsSafeAssetEntry(string name)
    {
        if (!name.StartsWith("assets/", StringComparison.Ordinal)) return false;
        var file = name["assets/".Length..];
        return file.Length is > 0 and < 128 && file.IndexOfAny(['/', '\\', ':']) < 0 && file != "." && file != ".."
               && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static ZipArchive OpenZip(string path)
    {
        try { return ZipFile.OpenRead(path); }
        catch (InvalidDataException ex) { throw new ConfigFormatException("Ce fichier n'est pas une archive ZogmaDeck valide.", ex); }
    }

    private static string HashFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s))[..20];
    }

    private static string SafeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length is > 1 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : ".bin";
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(new UTF8Encoding(false).GetBytes(text));
    }

    private static string ReadText(ZipArchiveEntry e) => new UTF8Encoding(false).GetString(ReadBounded(e, MaxConfigBytes));

    /// <summary>Lit une entrée en refusant de décompresser au-delà de <paramref name="max"/> octets, quelle que soit la taille déclarée.</summary>
    private static byte[] ReadBounded(ZipArchiveEntry e, long max)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > max) throw new ConfigFormatException($"Archive invalide : « {e.FullName} » dépasse la taille autorisée.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
