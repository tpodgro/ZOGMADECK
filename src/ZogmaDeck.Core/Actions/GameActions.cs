using System.Text;
using System.Text.Json;

namespace ZogmaDeck.Core.Actions;

public enum GamePlatform
{
    [Label("Steam")] Steam,
    [Label("Epic Games")] Epic,
    [Label("GOG Galaxy")] Gog,
    [Label("Ubisoft Connect")] Ubisoft,
    [Label("Battle.net")] BattleNet,
}

/// <summary>Jeu installé détecté sur le poste (identifiant propre à la plateforme).</summary>
public sealed record InstalledGame(GamePlatform Platform, string Id, string Name)
{
    public string Display => $"{Name}  ({Id})";
}

/// <summary>Lancement d'un jeu, fourni par l'application (détection et chemins des lanceurs propres à Windows).</summary>
public interface IGameService
{
    Task LaunchAsync(GamePlatform platform, string gameId, CancellationToken ct);
}

internal sealed class UnavailableGameService : IGameService
{
    public static readonly UnavailableGameService Instance = new();

    public Task LaunchAsync(GamePlatform platform, string gameId, CancellationToken ct) =>
        throw new ActionException("Le lancement de jeux n'est pas disponible.");
}

[ActionType("launchGame", "Lancer un jeu", Category = "Jeux", Glyph = "\uE7FC", Order = 70,
    Description = "Lance un jeu installé via Steam, Epic Games, GOG Galaxy, Ubisoft Connect ou Battle.net.")]
public sealed class LaunchGameAction : DeckAction
{
    [Field("Plateforme", Kind = FieldKind.Choice, Order = 1)]
    public GamePlatform Platform { get; set; } = GamePlatform.Steam;

    [Field("Jeu", Kind = FieldKind.Game, Order = 2, DependsOn = nameof(Platform),
        Hint = "Choisissez dans la liste des jeux installés, ou saisissez l'identifiant (Steam : numéro de l'application ; Battle.net : code du jeu, ex. WoW).")]
    public string GameId { get; set; } = "";

    /// <summary>Nom du jeu au moment du choix, pour l'affichage.</summary>
    public string? GameName { get; set; }

    public override string Describe() =>
        $"Lancer {Quote(string.IsNullOrWhiteSpace(GameName) ? GameId : GameName)} ({EnumLabels.Get(Platform)})";

    public override IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(GameId)) yield return "Choisissez le jeu à lancer.";
        else if (Platform == GamePlatform.Steam && !GameUris.IsSteamAppId(GameId)) yield return "L'identifiant Steam est un nombre (ex. 730).";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        foreach (var problem in Validate()) throw new ActionException(problem);
        return ctx.Games.LaunchAsync(Platform, GameId.Trim(), ct);
    }
}

public enum SteamCommand
{
    [Label("Ouvrir la bibliothèque")] Library,
    [Label("Ouvrir la liste d'amis")] Friends,
    [Label("Statut : en ligne")] StatusOnline,
    [Label("Statut : absent")] StatusAway,
    [Label("Statut : invisible")] StatusInvisible,
    [Label("Statut : hors ligne")] StatusOffline,
    [Label("Ouvrir Big Picture")] BigPicture,
    [Label("Ouvrir les téléchargements")] Downloads,
    [Label("Ouvrir les captures d'écran")] Screenshots,
    [Label("Ouvrir le magasin")] Store,
    [Label("Page du magasin d'un jeu")] StorePage,
    [Label("Ouvrir les paramètres")] Settings,
    [Label("Quitter Steam")] Exit,
}

[ActionType("steam", "Steam", Category = "Jeux", Glyph = "\uE7FC", Order = 71,
    Description = "Commandes du client Steam : bibliothèque, amis, statut, Big Picture, magasin…")]
public sealed class SteamAction : DeckAction
{
    [Field("Commande", Kind = FieldKind.Choice, Order = 1)]
    public SteamCommand Command { get; set; } = SteamCommand.Friends;

    [Field("Identifiant du jeu", Order = 2, VisibleWhen = nameof(Command), VisibleValues = nameof(SteamCommand.StorePage),
        Placeholder = "730", Hint = "Numéro visible dans l'adresse de la page du jeu (store.steampowered.com/app/<numéro>).")]
    public string? AppId { get; set; }

    public override string Describe() => "Steam : " + EnumLabels.Get(Command).ToLowerInvariant();

    public override IEnumerable<string> Validate()
    {
        if (Command == SteamCommand.StorePage && !GameUris.IsSteamAppId(AppId)) yield return "Indiquez le numéro du jeu dans le magasin Steam.";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        foreach (var problem in Validate()) throw new ActionException(problem);
        return ctx.Shell.OpenAsync(GameUris.Steam(Command, AppId), ct);
    }
}

/// <summary>Adresses des lanceurs de jeux (protocoles enregistrés par Steam, Epic, Ubisoft…).</summary>
public static class GameUris
{
    public static bool IsSteamAppId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Trim().All(char.IsAsciiDigit) && id.Trim().Length <= 12;

    public static string SteamRun(string appId) => $"steam://rungameid/{appId.Trim()}";

    public static string Steam(SteamCommand command, string? appId = null) => command switch
    {
        SteamCommand.Library => "steam://open/games",
        SteamCommand.Friends => "steam://open/friends",
        SteamCommand.StatusOnline => "steam://friends/status/online",
        SteamCommand.StatusAway => "steam://friends/status/away",
        SteamCommand.StatusInvisible => "steam://friends/status/invisible",
        SteamCommand.StatusOffline => "steam://friends/status/offline",
        SteamCommand.BigPicture => "steam://open/bigpicture",
        SteamCommand.Downloads => "steam://open/downloads",
        SteamCommand.Screenshots => "steam://open/screenshots",
        SteamCommand.Store => "steam://store",
        SteamCommand.StorePage => $"steam://store/{appId?.Trim()}",
        SteamCommand.Settings => "steam://open/settings",
        SteamCommand.Exit => "steam://exit",
        _ => throw new ActionException("Commande Steam inconnue."),
    };

    /// <summary>Epic : identifiant « espace:article:application » (manifeste) ou nom d'application seul.</summary>
    public static string EpicLaunch(string id) =>
        $"com.epicgames.launcher://apps/{Uri.EscapeDataString(id.Trim())}?action=launch&silent=true";

    public static string UbisoftLaunch(string id) => $"uplay://launch/{Uri.EscapeDataString(id.Trim())}/0";

    /// <summary>Codes Battle.net courants (argument « --exec=launch CODE » du client).</summary>
    public static IReadOnlyList<InstalledGame> BattleNetGames { get; } =
    [
        new(GamePlatform.BattleNet, "WoW", "World of Warcraft"),
        new(GamePlatform.BattleNet, "Pro", "Overwatch 2"),
        new(GamePlatform.BattleNet, "Fen", "Diablo IV"),
        new(GamePlatform.BattleNet, "D3", "Diablo III"),
        new(GamePlatform.BattleNet, "OSI", "Diablo II: Resurrected"),
        new(GamePlatform.BattleNet, "WTCG", "Hearthstone"),
        new(GamePlatform.BattleNet, "Hero", "Heroes of the Storm"),
        new(GamePlatform.BattleNet, "S2", "StarCraft II"),
        new(GamePlatform.BattleNet, "S1", "StarCraft Remastered"),
        new(GamePlatform.BattleNet, "W3", "Warcraft III: Reforged"),
    ];
}

/// <summary>Lecture des fichiers des lanceurs (sans dépendance Windows : testable).</summary>
public static class GameManifests
{
    /// <summary>Applications Steam qui ne sont pas des jeux (redistribuables, outils, couches de compatibilité).</summary>
    private static readonly HashSet<string> SteamNonGames = ["228980", "1070560", "1391110", "1628350", "1493710", "2180100", "1007"];

    /// <summary>Lit le format texte KeyValues de Valve (.vdf / .acf) en dictionnaires imbriqués.</summary>
    public static Dictionary<string, object> ParseVdf(string text)
    {
        var pos = 0;
        return ParseObject(text, ref pos, topLevel: true);
    }

    private static Dictionary<string, object> ParseObject(string s, ref int pos, bool topLevel)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) return result;
            if (s[pos] == '}')
            {
                pos++;
                if (!topLevel) return result;
                continue;
            }
            var key = ReadToken(s, ref pos);
            if (key is null) return result;
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '{')
            {
                pos++;
                result[key] = ParseObject(s, ref pos, topLevel: false);
            }
            else
            {
                result[key] = ReadToken(s, ref pos) ?? "";
            }
        }
    }

    private static void SkipWhitespace(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            if (char.IsWhiteSpace(s[pos])) pos++;
            else if (s[pos] == '/' && pos + 1 < s.Length && s[pos + 1] == '/') // commentaire
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
            }
            else break;
        }
    }

    private static string? ReadToken(string s, ref int pos)
    {
        if (pos >= s.Length) return null;
        var sb = new StringBuilder();
        if (s[pos] == '"')
        {
            pos++;
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    pos++;
                    sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', _ => s[pos] });
                }
                else sb.Append(s[pos]);
                pos++;
            }
            pos++; // guillemet fermant
            return sb.ToString();
        }
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}')) sb.Append(s[pos++]);
        return sb.ToString();
    }

    /// <summary>Dossiers des bibliothèques Steam déclarés dans libraryfolders.vdf.</summary>
    public static IReadOnlyList<string> SteamLibraryFolders(string libraryFoldersVdf)
    {
        var root = ParseVdf(libraryFoldersVdf);
        if ((root.GetValueOrDefault("libraryfolders") ?? root.GetValueOrDefault("LibraryFolders")) is not Dictionary<string, object> folders) return [];
        var list = new List<string>();
        foreach (var value in folders.Values)
        {
            // Format actuel : { "path" "…" } ; ancien format : "1" "D:\\SteamLibrary"
            if (value is Dictionary<string, object> d && d.GetValueOrDefault("path") is string p) list.Add(p);
            else if (value is string s && (s.Contains('\\') || s.Contains('/'))) list.Add(s);
        }
        return list;
    }

    /// <summary>Jeu décrit par un appmanifest_*.acf (null si ce n'est pas un jeu).</summary>
    public static InstalledGame? SteamGameFromManifest(string acf)
    {
        if (ParseVdf(acf).GetValueOrDefault("AppState") is not Dictionary<string, object> app) return null;
        if (app.GetValueOrDefault("appid") is not string id || app.GetValueOrDefault("name") is not string name) return null;
        if (SteamNonGames.Contains(id) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) return null;
        return new InstalledGame(GamePlatform.Steam, id, name);
    }

    /// <summary>Jeu décrit par un manifeste Epic (.item, JSON).</summary>
    public static InstalledGame? EpicGameFromManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
            var app = Get("AppName");
            var name = Get("DisplayName") ?? app;
            if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(name)) return null;
            var ns = Get("CatalogNamespace");
            var item = Get("CatalogItemId");
            var id = !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(item) ? $"{ns}:{item}:{app}" : app;
            return new InstalledGame(GamePlatform.Epic, id, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
