using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Core.Integrations;

public enum TeamSpeakClient
{
    [Label("TeamSpeak 3")] Ts3,
    [Label("TeamSpeak 5 / 6")] Ts6,
}

public enum TeamSpeakCommand
{
    [Label("Basculer le micro (muet / actif)")] ToggleMicrophone,
    [Label("Couper le micro")] MuteMicrophone,
    [Label("Réactiver le micro")] UnmuteMicrophone,
    [Label("Basculer le son (haut-parleurs)")] ToggleSpeakers,
    [Label("Couper le son (haut-parleurs)")] MuteSpeakers,
    [Label("Réactiver le son (haut-parleurs)")] UnmuteSpeakers,
    [Label("Basculer « absent »")] ToggleAway,
    [Label("Se connecter à un serveur")] Connect,
    [Label("Se déconnecter")] Disconnect,
    [Label("Aller dans un salon (TeamSpeak 3)")] SwitchChannel,
    [Label("Changer de pseudo (TeamSpeak 3)")] SetNickname,
    [Label("Bouton Remote App personnalisé (TeamSpeak 5/6)")] RemoteButton,
}

public sealed record TeamSpeakRequest(
    TeamSpeakClient Client, TeamSpeakCommand Command, string? Server = null, string? ServerPassword = null,
    string? Nickname = null, string? Channel = null, string? AwayMessage = null, string? ButtonName = null);

public interface ITeamSpeakService
{
    Task ExecuteAsync(TeamSpeakRequest request, CancellationToken ct);
}

[ActionType("teamspeak", "TeamSpeak", Category = "Communication", Glyph = "\uE720", Order = 80,
    Description = "Micro, son, absence, connexion et salons dans TeamSpeak 3, 5 ou 6 (à configurer dans l'onglet Intégrations).")]
public sealed class TeamSpeakAction : DeckAction
{
    [Field("Version de TeamSpeak", Kind = FieldKind.Choice, Order = 1)]
    public TeamSpeakClient Client { get; set; } = TeamSpeakClient.Ts3;

    [Field("Commande", Kind = FieldKind.Choice, Order = 2)]
    public TeamSpeakCommand Command { get; set; } = TeamSpeakCommand.ToggleMicrophone;

    [Field("Adresse du serveur", Order = 3, VisibleWhen = nameof(Command), VisibleValues = nameof(TeamSpeakCommand.Connect),
        Placeholder = "ts.exemple.fr ou ts.exemple.fr:9987")]
    public string? Server { get; set; }

    [Field("Mot de passe du serveur", Order = 4, VisibleWhen = nameof(Command), VisibleValues = nameof(TeamSpeakCommand.Connect),
        Hint = "Facultatif. Attention : enregistré en clair dans la configuration et inclus dans les exports.")]
    public string? ServerPassword { get; set; }

    [Field("Pseudo", Order = 5, VisibleWhen = nameof(Command), VisibleValues = "Connect,SetNickname", Hint = "Facultatif pour la connexion.")]
    public string? Nickname { get; set; }

    [Field("Salon", Order = 6, VisibleWhen = nameof(Command), VisibleValues = "Connect,SwitchChannel",
        Hint = "Nom exact du salon (pour la connexion : facultatif, chemin « Parent/Salon » accepté).")]
    public string? Channel { get; set; }

    [Field("Message d'absence", Order = 7, VisibleWhen = nameof(Command), VisibleValues = nameof(TeamSpeakCommand.ToggleAway), Hint = "Facultatif (TeamSpeak 3).")]
    public string? AwayMessage { get; set; }

    [Field("Nom du bouton", Order = 8, VisibleWhen = nameof(Command), VisibleValues = nameof(TeamSpeakCommand.RemoteButton),
        Placeholder = "ex. Musique", Hint = "Associez-le une fois dans TeamSpeak › Paramètres › Raccourcis, en appuyant sur ce bouton de ZogmaDeck.")]
    public string? ButtonName { get; set; }

    public override string Describe() => Command switch
    {
        TeamSpeakCommand.Connect => $"TeamSpeak : connexion à {Quote(Server)}",
        TeamSpeakCommand.SwitchChannel => $"TeamSpeak : salon {Quote(Channel)}",
        TeamSpeakCommand.SetNickname => $"TeamSpeak : pseudo {Quote(Nickname)}",
        TeamSpeakCommand.RemoteButton => $"TeamSpeak : bouton {Quote(ButtonName)}",
        _ => "TeamSpeak : " + EnumLabels.Get(Command).ToLowerInvariant(),
    };

    public override IEnumerable<string> Validate()
    {
        switch (Command)
        {
            case TeamSpeakCommand.Connect when string.IsNullOrWhiteSpace(Server):
                yield return "Indiquez l'adresse du serveur.";
                break;
            case TeamSpeakCommand.SwitchChannel when string.IsNullOrWhiteSpace(Channel):
                yield return "Indiquez le nom du salon.";
                break;
            case TeamSpeakCommand.SetNickname when string.IsNullOrWhiteSpace(Nickname):
                yield return "Indiquez le pseudo.";
                break;
            case TeamSpeakCommand.RemoteButton when string.IsNullOrWhiteSpace(ButtonName):
                yield return "Indiquez le nom du bouton.";
                break;
        }
        if (Client == TeamSpeakClient.Ts6 && Command is TeamSpeakCommand.SwitchChannel or TeamSpeakCommand.SetNickname)
            yield return "TeamSpeak 5/6 ne permet pas cette commande : utilisez un bouton Remote App associé à l'action voulue dans TeamSpeak.";
        if (Client == TeamSpeakClient.Ts3 && Command == TeamSpeakCommand.RemoteButton)
            yield return "Les boutons Remote App n'existent que dans TeamSpeak 5/6.";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        foreach (var problem in Validate()) throw new ActionException(problem);
        return ctx.TeamSpeak.ExecuteAsync(new TeamSpeakRequest(Client, Command, Server, ServerPassword, Nickname, Channel, AwayMessage, ButtonName), ct);
    }
}

/// <summary>Le client TeamSpeak n'est pas joignable (pas lancé, module désactivé).</summary>
public sealed class TeamSpeakUnavailableException(string message, Exception? inner = null) : ActionException(message, inner);

/// <summary>Choisit le client (TeamSpeak 3 ou 5/6). La connexion passe par le lien ts3server:// si TeamSpeak 3 n'est pas lancé.</summary>
public sealed class TeamSpeakService(Ts3ClientQuery ts3, Ts6RemoteApps ts6, IShellService shell) : ITeamSpeakService
{
    public async Task ExecuteAsync(TeamSpeakRequest r, CancellationToken ct)
    {
        if (r.Command == TeamSpeakCommand.Connect)
        {
            var link = TeamSpeakLinks.Connect(r.Server ?? "", r.ServerPassword, r.Nickname, r.Channel);
            if (r.Client == TeamSpeakClient.Ts6)
            {
                await shell.OpenAsync(link, ct);
                return;
            }
            try
            {
                await ts3.ExecuteAsync(r, ct);
            }
            catch (TeamSpeakUnavailableException)
            {
                await shell.OpenAsync(link, ct); // lance TeamSpeak 3 et se connecte
            }
            return;
        }
        if (r.Client == TeamSpeakClient.Ts6) await ts6.ExecuteAsync(r, ct);
        else await ts3.ExecuteAsync(r, ct);
    }
}

internal sealed class UnavailableTeamSpeakService : ITeamSpeakService
{
    public static readonly UnavailableTeamSpeakService Instance = new();
    public Task ExecuteAsync(TeamSpeakRequest request, CancellationToken ct) => throw new ActionException("TeamSpeak n'est pas disponible.");
}

/// <summary>Lien ts3server:// (TeamSpeak 3, 5 et 6) pour se connecter à un serveur.</summary>
public static class TeamSpeakLinks
{
    public static string Connect(string server, string? password, string? nickname, string? channel)
    {
        var s = server.Trim();
        string host = s, port = "";
        var colon = s.LastIndexOf(':');
        if (colon > 0 && s.IndexOf(':') == colon && int.TryParse(s[(colon + 1)..], out _)) // IPv6 sans port : laissé tel quel
        {
            host = s[..colon];
            port = s[(colon + 1)..];
        }
        var query = new List<string>();
        if (port.Length > 0) query.Add("port=" + port);
        if (!string.IsNullOrWhiteSpace(nickname)) query.Add("nickname=" + Uri.EscapeDataString(nickname.Trim()));
        if (!string.IsNullOrWhiteSpace(password)) query.Add("password=" + Uri.EscapeDataString(password));
        if (!string.IsNullOrWhiteSpace(channel)) query.Add("channel=" + Uri.EscapeDataString(channel.Trim()));
        return $"ts3server://{host}" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }
}

/// <summary>
/// Client de l'interface ClientQuery de TeamSpeak 3 (plugin « ClientQuery », TCP local 127.0.0.1:25639, authentification
/// par clé API). Une connexion par commande : simple, et sans état à resynchroniser si TeamSpeak redémarre.
/// </summary>
public sealed class Ts3ClientQuery(Func<string?> apiKey, int port = 25639)
{
    public const int DefaultPort = 25639;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task ExecuteAsync(TeamSpeakRequest r, CancellationToken ct)
    {
        await using var session = await OpenAsync(ct);
        switch (r.Command)
        {
            case TeamSpeakCommand.ToggleMicrophone:
            case TeamSpeakCommand.MuteMicrophone:
            case TeamSpeakCommand.UnmuteMicrophone:
                await SetFlagAsync(session, "client_input_muted", r.Command switch
                {
                    TeamSpeakCommand.MuteMicrophone => true,
                    TeamSpeakCommand.UnmuteMicrophone => false,
                    _ => null,
                }, ct);
                break;
            case TeamSpeakCommand.ToggleSpeakers:
            case TeamSpeakCommand.MuteSpeakers:
            case TeamSpeakCommand.UnmuteSpeakers:
                await SetFlagAsync(session, "client_output_muted", r.Command switch
                {
                    TeamSpeakCommand.MuteSpeakers => true,
                    TeamSpeakCommand.UnmuteSpeakers => false,
                    _ => null,
                }, ct);
                break;
            case TeamSpeakCommand.ToggleAway:
            {
                var away = !await GetFlagAsync(session, "client_away", ct);
                var cmd = "clientupdate client_away=" + (away ? "1" : "0");
                if (away && !string.IsNullOrWhiteSpace(r.AwayMessage)) cmd += " client_away_message=" + Escape(r.AwayMessage.Trim());
                await session.CommandAsync(cmd, ct);
                break;
            }
            case TeamSpeakCommand.Connect:
            {
                var cmd = "connect address=" + Escape(r.Server!.Trim());
                if (!string.IsNullOrWhiteSpace(r.ServerPassword)) cmd += " password=" + Escape(r.ServerPassword);
                if (!string.IsNullOrWhiteSpace(r.Nickname)) cmd += " nickname=" + Escape(r.Nickname.Trim());
                if (!string.IsNullOrWhiteSpace(r.Channel)) cmd += " channel=" + Escape(r.Channel.Trim());
                await session.CommandAsync(cmd, ct);
                break;
            }
            case TeamSpeakCommand.Disconnect:
                await session.CommandAsync("disconnect", ct);
                break;
            case TeamSpeakCommand.SetNickname:
                await RequireConnectedAsync(session, ct);
                await session.CommandAsync("clientupdate client_nickname=" + Escape(r.Nickname!.Trim()), ct);
                break;
            case TeamSpeakCommand.SwitchChannel:
            {
                var clid = await RequireConnectedAsync(session, ct);
                var list = await session.CommandAsync("channellist", ct);
                var wanted = r.Channel!.Trim();
                var cid = ParseItems(list)
                    .FirstOrDefault(i => string.Equals(i.GetValueOrDefault("channel_name"), wanted, StringComparison.OrdinalIgnoreCase))
                    ?.GetValueOrDefault("cid")
                    ?? throw new ActionException($"Salon TeamSpeak introuvable : « {wanted} ».");
                await session.CommandAsync($"clientmove cid={cid} clid={clid}", ct);
                break;
            }
            default:
                throw new ActionException("Commande non disponible avec TeamSpeak 3.");
        }
    }

    /// <summary>Vérifie la clé API (bouton « Tester » des réglages).</summary>
    public async Task<string> TestAsync(CancellationToken ct)
    {
        await using var session = await OpenAsync(ct);
        var who = await session.CommandAsync("whoami", ct, allowError: true);
        return who.ErrorId == 0 ? "Connexion à TeamSpeak 3 réussie (connecté à un serveur)." : "Connexion à TeamSpeak 3 réussie (pas connecté à un serveur).";
    }

    private async Task<Session> OpenAsync(CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ActionException("Clé API TeamSpeak 3 manquante : collez-la dans l'éditeur › Intégrations (TeamSpeak › Outils › Options › Modules › ClientQuery › Paramètres).");
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await tcp.ConnectAsync("127.0.0.1", port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TeamSpeakUnavailableException("TeamSpeak 3 ne répond pas : lancez-le et vérifiez que le module ClientQuery est activé (Outils › Options › Modules).", ex);
        }
        var session = new Session(tcp);
        try
        {
            await session.ReadGreetingAsync(timeout.Token);
            var auth = await session.CommandAsync("auth apikey=" + Escape(key.Trim()), ct, allowError: true);
            if (auth.ErrorId != 0)
                throw new ActionException("Clé API TeamSpeak 3 refusée : copiez-la à nouveau depuis TeamSpeak (Outils › Options › Modules › ClientQuery › Paramètres).");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> RequireConnectedAsync(Session s, CancellationToken ct)
    {
        var who = await s.CommandAsync("whoami", ct, allowError: true);
        if (who.ErrorId != 0) throw new ActionException("TeamSpeak 3 n'est connecté à aucun serveur.");
        return ParseItems(who).FirstOrDefault()?.GetValueOrDefault("clid") ?? throw new ActionException("Réponse inattendue de TeamSpeak 3 (whoami).");
    }

    private static async Task<bool> GetFlagAsync(Session s, string variable, CancellationToken ct)
    {
        var clid = await RequireConnectedAsync(s, ct);
        var r = await s.CommandAsync($"clientvariable clid={clid} {variable}", ct);
        return ParseItems(r).FirstOrDefault()?.GetValueOrDefault(variable) == "1";
    }

    private static async Task SetFlagAsync(Session s, string variable, bool? value, CancellationToken ct)
    {
        var target = value ?? !await GetFlagAsync(s, variable, ct);
        await s.CommandAsync($"clientupdate {variable}={(target ? 1 : 0)}", ct);
    }

    // ---------- Protocole ----------
    public sealed record Reply(int ErrorId, string Message, IReadOnlyList<string> Lines);

    /// <summary>Lignes de données en éléments « clé=valeur » (éléments séparés par « | »).</summary>
    internal static List<Dictionary<string, string>> ParseItems(Reply reply) =>
        reply.Lines.SelectMany(l => l.Split('|')).Select(item =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in item.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) d[part] = "";
                else d[part[..eq]] = Unescape(part[(eq + 1)..]);
            }
            return d;
        }).ToList();

    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => @"\\",
                '/' => @"\/",
                ' ' => @"\s",
                '|' => @"\p",
                '\a' => @"\a",
                '\b' => @"\b",
                '\f' => @"\f",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                '\v' => @"\v",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    public static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var n = value[++i];
            sb.Append(n switch
            {
                's' => ' ', 'p' => '|', '/' => '/', '\\' => '\\', 'a' => '\a', 'b' => '\b', 'f' => '\f',
                'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', _ => n,
            });
        }
        return sb.ToString();
    }

    private sealed class Session(TcpClient tcp) : IAsyncDisposable
    {
        private readonly NetworkStream _stream = tcp.GetStream();
        private readonly StringBuilder _buffer = new();
        private readonly byte[] _chunk = new byte[4096];

        public async Task ReadGreetingAsync(CancellationToken ct)
        {
            // « TS3 Client », message d'accueil, puis « selected schandlerid=N ».
            var first = await ReadLineAsync(ct);
            if (!first.StartsWith("TS3 Client", StringComparison.Ordinal))
                throw new ActionException("Le port 25639 ne correspond pas au ClientQuery de TeamSpeak 3.");
            while (true)
            {
                var line = await ReadLineAsync(ct);
                if (line.StartsWith("selected", StringComparison.Ordinal)) return;
            }
        }

        public async Task<Reply> CommandAsync(string command, CancellationToken ct, bool allowError = false)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var bytes = Encoding.UTF8.GetBytes(command + "\n");
            await _stream.WriteAsync(bytes, timeout.Token);
            var lines = new List<string>();
            while (true)
            {
                var line = await ReadLineAsync(timeout.Token);
                if (line.StartsWith("notify", StringComparison.Ordinal)) continue; // événements non demandés
                if (!line.StartsWith("error ", StringComparison.Ordinal))
                {
                    lines.Add(line);
                    continue;
                }
                var fields = ParseItems(new Reply(0, "", [line[6..]])).FirstOrDefault() ?? [];
                var id = int.TryParse(fields.GetValueOrDefault("id"), out var n) ? n : -1;
                var msg = fields.GetValueOrDefault("msg") ?? "";
                if (id != 0 && !allowError)
                    throw new ActionException(id == 1794
                        ? "TeamSpeak 3 n'est connecté à aucun serveur."
                        : $"TeamSpeak 3 a refusé la commande : {msg} (code {id}).");
                return new Reply(id, msg, lines);
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                var text = _buffer.ToString();
                var nl = text.IndexOf('\n');
                if (nl >= 0)
                {
                    _buffer.Remove(0, nl + 1);
                    var line = text[..nl].Trim('\r', ' ');
                    if (line.Length == 0) continue;
                    return line;
                }
                var read = await _stream.ReadAsync(_chunk, ct);
                if (read == 0) throw new ActionException("TeamSpeak 3 a fermé la connexion ClientQuery.");
                _buffer.Append(Encoding.UTF8.GetString(_chunk, 0, read));
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(500);
                await _stream.WriteAsync("quit\n"u8.ToArray(), cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
            tcp.Dispose();
        }
    }
}

/// <summary>
/// API « Remote Apps » de TeamSpeak 5/6 (WebSocket local, port 5899 par défaut). À la première connexion, TeamSpeak
/// demande à l'utilisateur d'autoriser ZogmaDeck et fournit une clé, conservée chiffrée. Les commandes sont des appuis sur
/// des boutons que l'utilisateur associe une fois aux actions voulues dans les raccourcis de TeamSpeak.
/// </summary>
public sealed class Ts6RemoteApps(Func<string?> apiKey, Action<string> saveApiKey, Func<ClientWebSocket>? socketFactory = null, int port = 5899)
{
    public const string Identifier = "zogmadeck";
    public const int DefaultPort = 5899;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Identifiant du bouton envoyé à TeamSpeak pour une commande (ex. « zogmadeck.ToggleMicrophone »).</summary>
    public static string ButtonId(TeamSpeakCommand command, string? custom = null) =>
        $"{Identifier}.{(command == TeamSpeakCommand.RemoteButton ? Sanitize(custom ?? "") : command.ToString())}";

    private static string Sanitize(string name) =>
        new(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    public async Task ExecuteAsync(TeamSpeakRequest r, CancellationToken ct)
    {
        if (r.Command is TeamSpeakCommand.SwitchChannel or TeamSpeakCommand.SetNickname)
            throw new ActionException("TeamSpeak 5/6 ne permet pas cette commande.");
        using var ws = await ConnectAndAuthAsync(pairing: false, ct);
        await PressAsync(ws, ButtonId(r.Command, r.ButtonName), ct);
        await CloseQuietlyAsync(ws);
    }

    /// <summary>
    /// Demande l'autorisation à TeamSpeak (l'utilisateur doit accepter dans le client) et enregistre la clé obtenue.
    /// </summary>
    public async Task<string> PairAsync(TimeSpan wait, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        using var ws = await ConnectAndAuthAsync(pairing: true, timeout.Token);
        await CloseQuietlyAsync(ws);
        return "ZogmaDeck est autorisé dans TeamSpeak.";
    }

    /// <summary>Envoie un appui puis un relâchement (TeamSpeak attend les deux, comme pour une vraie touche).</summary>
    private static async Task PressAsync(ClientWebSocket ws, string button, CancellationToken ct)
    {
        foreach (var state in new[] { true, false })
        {
            var msg = new JsonObject { ["type"] = "buttonPress", ["payload"] = new JsonObject { ["button"] = button, ["state"] = state } };
            await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct);
            if (state) await Task.Delay(60, ct);
        }
    }

    private async Task<ClientWebSocket> ConnectAndAuthAsync(bool pairing, CancellationToken ct)
    {
        var key = apiKey() ?? "";
        if (!pairing && key.Length == 0)
            throw new ActionException("ZogmaDeck n'est pas encore autorisé dans TeamSpeak 5/6 : éditeur › Intégrations › « Autoriser dans TeamSpeak ».");
        var ws = socketFactory?.Invoke() ?? new ClientWebSocket();
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), connectTimeout.Token);
            }
            var auth = new JsonObject
            {
                ["type"] = "auth",
                ["payload"] = new JsonObject
                {
                    ["identifier"] = Identifier,
                    ["version"] = "1",
                    ["name"] = "ZogmaDeck",
                    ["description"] = "Panneau de boutons : micro, son, connexion…",
                    ["content"] = new JsonObject { ["apiKey"] = key },
                },
            };
            await ws.SendAsync(Encoding.UTF8.GetBytes(auth.ToJsonString()), WebSocketMessageType.Text, true, ct);

            // Réponse d'authentification ; pendant l'appairage, elle n'arrive qu'après acceptation dans TeamSpeak.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (!pairing) wait.CancelAfter(RequestTimeout);
            while (true)
            {
                var json = await ReceiveAsync(ws, wait.Token);
                if (json is null) throw new ActionException("TeamSpeak a refusé ou fermé la connexion (autorisation refusée ?).");
                var node = JsonNode.Parse(json);
                if (node?["type"]?.GetValue<string>() != "auth") continue; // autres événements ignorés
                var code = node["status"]?["code"]?.GetValue<int>() ?? 0;
                if (code != 0)
                {
                    if (!pairing) saveApiKey(""); // clé révoquée dans TeamSpeak
                    throw new ActionException($"TeamSpeak a refusé l'autorisation : {node["status"]?["message"]?.GetValue<string>() ?? "refus"}.");
                }
                if (node["payload"]?["apiKey"]?.GetValue<string>() is { Length: > 0 } newKey && newKey != key) saveApiKey(newKey);
                return ws;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TeamSpeakUnavailableException("TeamSpeak 5/6 ne répond pas : lancez-le et activez Paramètres › Remote Apps (port 5899).", ex);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 4 * 1024 * 1024) throw new ActionException("Message TeamSpeak trop volumineux.");
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket ws)
    {
        try
        {
            using var cts = new CancellationTokenSource(1000);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }
}
