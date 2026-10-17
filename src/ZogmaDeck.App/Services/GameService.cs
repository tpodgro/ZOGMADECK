using System.IO;
using Microsoft.Win32;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Services;

/// <summary>
/// Jeux installés et lancement via les clients Steam, Epic Games, GOG Galaxy, Ubisoft Connect et Battle.net. Lecture seule
/// des fichiers et du registre des lanceurs ; aucune connexion réseau.
/// </summary>
public sealed class GameService(ShellService shell) : IGameService
{
    public IReadOnlyList<InstalledGame> GetInstalledGames(GamePlatform platform)
    {
        try
        {
            var games = platform switch
            {
                GamePlatform.Steam => SteamGames(),
                GamePlatform.Epic => EpicGames(),
                GamePlatform.Gog => GogGames(),
                GamePlatform.Ubisoft => UbisoftGames(),
                GamePlatform.BattleNet => GameUris.BattleNetGames.ToList(),
                _ => [],
            };
            return games.DistinctBy(g => g.Id).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn($"Détection des jeux {platform} impossible", ex);
            return [];
        }
    }

    public async Task LaunchAsync(GamePlatform platform, string gameId, CancellationToken ct)
    {
        switch (platform)
        {
            case GamePlatform.Steam:
                RequireProtocol("steam", "Steam");
                await shell.OpenAsync(GameUris.SteamRun(gameId), ct);
                break;
            case GamePlatform.Epic:
                RequireProtocol("com.epicgames.launcher", "Epic Games Launcher");
                await shell.OpenAsync(GameUris.EpicLaunch(gameId), ct);
                break;
            case GamePlatform.Ubisoft:
                RequireProtocol("uplay", "Ubisoft Connect");
                await shell.OpenAsync(GameUris.UbisoftLaunch(gameId), ct);
                break;
            case GamePlatform.Gog:
                await LaunchGogAsync(gameId, ct);
                break;
            case GamePlatform.BattleNet:
                var bnet = BattleNetExe() ?? throw new ActionException("Battle.net n'est pas installé sur ce poste.");
                await shell.LaunchAsync(bnet, $"--exec=\"launch {gameId}\"", null, LaunchWindowStyle.Normal, ct);
                break;
        }
    }

    private static void RequireProtocol(string scheme, string client)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(scheme);
        if (key is null) throw new ActionException($"{client} n'est pas installé sur ce poste (protocole {scheme}:// absent).");
    }

    // ---------- Steam ----------
    public static string? SteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string is { Length: > 0 } p ? Path.GetFullPath(p) : null;
    }

    private static List<InstalledGame> SteamGames()
    {
        var steam = SteamPath();
        if (steam is null) return [];
        var libraries = new List<string> { steam };
        foreach (var vdf in new[] { Path.Combine(steam, "steamapps", "libraryfolders.vdf"), Path.Combine(steam, "config", "libraryfolders.vdf") })
        {
            if (File.Exists(vdf)) libraries.AddRange(GameManifests.SteamLibraryFolders(File.ReadAllText(vdf)));
        }
        var games = new List<InstalledGame>();
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                if (GameManifests.SteamGameFromManifest(File.ReadAllText(acf)) is { } g) games.Add(g);
            }
        }
        return games;
    }

    // ---------- Epic ----------
    private static List<InstalledGame> EpicGames()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.item")
            .Select(f => GameManifests.EpicGameFromManifest(File.ReadAllText(f)))
            .OfType<InstalledGame>()
            .ToList();
    }

    // ---------- GOG ----------
    private const string GogGamesKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";

    private static List<InstalledGame> GogGames()
    {
        using var root = Registry.LocalMachine.OpenSubKey(GogGamesKey);
        if (root is null) return [];
        var list = new List<InstalledGame>();
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("gameName") is string name && name.Length > 0) list.Add(new InstalledGame(GamePlatform.Gog, id, name));
        }
        return list;
    }

    private async Task LaunchGogAsync(string id, CancellationToken ct)
    {
        using var game = Registry.LocalMachine.OpenSubKey($@"{GogGamesKey}\{id}");
        using var galaxy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths");
        var client = galaxy?.GetValue("client") as string;
        var galaxyExe = client is null ? null : Path.Combine(client, "GalaxyClient.exe");
        var gamePath = game?.GetValue("path") as string;
        if (galaxyExe is not null && File.Exists(galaxyExe))
        {
            var args = $"/command=runGame /gameId={id}" + (string.IsNullOrEmpty(gamePath) ? "" : $" /path=\"{gamePath}\"");
            await shell.LaunchAsync(galaxyExe, args, null, LaunchWindowStyle.Normal, ct);
            return;
        }
        // Sans GOG Galaxy : exécutable du jeu indiqué par l'installateur GOG.
        if (game?.GetValue("exe") is string exe && File.Exists(exe))
        {
            await shell.LaunchAsync(exe, game.GetValue("launchParam") as string, game.GetValue("workingDir") as string ?? gamePath,
                LaunchWindowStyle.Normal, ct);
            return;
        }
        throw new ActionException($"Jeu GOG introuvable sur ce poste (identifiant {id}).");
    }

    // ---------- Ubisoft ----------
    private static List<InstalledGame> UbisoftGames()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (root is null) return [];
        var list = new List<InstalledGame>();
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is string dir && dir.Length > 0)
                list.Add(new InstalledGame(GamePlatform.Ubisoft, id, Path.GetFileName(dir.TrimEnd('\\', '/'))));
        }
        return list;
    }

    // ---------- Battle.net ----------
    private static string? BattleNetExe()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battle.net", "Battle.net.exe"),
        };
        using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net");
        if (uninstall?.GetValue("InstallLocation") is string loc) candidates.Insert(0, Path.Combine(loc, "Battle.net.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
