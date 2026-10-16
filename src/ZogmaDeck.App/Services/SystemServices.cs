using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Services;

/// <summary>
/// Emplacements des données. Mode portable si un fichier « ZogmaDeck.portable » (ou l'ancien « PocketDeck.portable »)
/// est à côté de l'exécutable. Au premier lancement, les données de l'ancien nom (PocketDeck) sont reprises.
/// </summary>
public sealed class AppPaths
{
    public const string PortableMarker = "ZogmaDeck.portable";
    public const string LegacyPortableMarker = "PocketDeck.portable";
    public const string LegacyName = "PocketDeck";

    public AppPaths(string? overrideDataDir = null)
    {
        ExecutablePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ZogmaDeck.exe");
        var exeDir = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;
        IsPortable = overrideDataDir is null &&
                     (File.Exists(Path.Combine(exeDir, PortableMarker)) || File.Exists(Path.Combine(exeDir, LegacyPortableMarker)));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        DataDirectory = overrideDataDir
                        ?? (IsPortable ? Path.Combine(exeDir, "data") : Path.Combine(appData, "ZogmaDeck"));
        if (overrideDataDir is null && !IsPortable)
            MigratedFrom = MigrateLegacyData(Path.Combine(appData, LegacyName), DataDirectory);
        Directory.CreateDirectory(DataDirectory);
    }

    /// <summary>Dossier de l'ancien nom d'où les données ont été reprises à ce lancement, sinon null.</summary>
    public string? MigratedFrom { get; }

    /// <summary>
    /// Copie (sans rien supprimer) les données de PocketDeck vers ZogmaDeck si ZogmaDeck n'a encore aucune configuration :
    /// configuration, sauvegardes, images/sons et mot de passe chiffré. Les journaux ne sont pas repris.
    /// </summary>
    internal static string? MigrateLegacyData(string legacyDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir) || File.Exists(Path.Combine(newDir, "config.json"))) return null;
            if (!File.Exists(Path.Combine(legacyDir, "config.json")) && !File.Exists(Path.Combine(legacyDir, "config.json.bak"))) return null;
            Directory.CreateDirectory(newDir);
            foreach (var file in Directory.EnumerateFiles(legacyDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacyDir, file);
                if (relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(newDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
            return legacyDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // l'application démarrera avec une configuration neuve
        }
    }

    public string ExecutablePath { get; }
    public bool IsPortable { get; }
    public string DataDirectory { get; }
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string AssetsDirectory => Path.Combine(DataDirectory, "assets");
    public string SecretsPath => Path.Combine(DataDirectory, "secrets.dat");
}

/// <summary>Secrets chiffrés avec DPAPI (lié au compte Windows). Fichier séparé de config.json : jamais exporté, jamais journalisé.</summary>
public sealed class SecretStore(string path)
{
    private static readonly byte[] Entropy = "PocketDeck.Secrets.v1"u8.ToArray();
    public const string ObsPasswordKey = "obsPassword";

    public string? Get(string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (map is null || !map.TryGetValue(key, out var b64)) return null;
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException)
        {
            Log.Warn("Secret illisible (créé sur un autre compte ou un autre PC ?) : il doit être ressaisi.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            Log.Warn("Fichier de secrets illisible.");
            return null;
        }
    }

    public bool Has(string key) => !string.IsNullOrEmpty(Get(key));

    public void Set(string key, string? value)
    {
        Dictionary<string, string> map;
        try
        {
            map = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            map = [];
        }

        if (string.IsNullOrEmpty(value)) map.Remove(key);
        else
            map[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(map));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Démarrage avec Windows via HKCU\…\Run (aucun droit administrateur nécessaire).</summary>
public sealed class StartupService(string executablePath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZogmaDeck";

    private string Command => $"\"{executablePath}\" --tray";

    /// <summary>Supprime l'entrée de démarrage de l'ancien nom (PocketDeck). Retourne true si elle existait.</summary>
    public bool RemoveLegacyEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(AppPaths.LegacyName) is null) return false;
            key.DeleteValue(AppPaths.LegacyName, false);
            Log.Info("Ancienne entrée de démarrage PocketDeck supprimée.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Equals(Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <returns>Message d'erreur, ou null en cas de succès.</returns>
    public string? SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, false);
            Log.Info($"Démarrage avec Windows : {(enabled ? "activé" : "désactivé")}");
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn("Modification du démarrage automatique impossible", ex);
            return "Impossible de modifier le démarrage automatique : " + ex.Message;
        }
    }
}
