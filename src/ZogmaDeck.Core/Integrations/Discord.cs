using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Core.Integrations;

public enum DiscordCommand
{
    [Label("Basculer le micro (muet / actif)")] ToggleMute,
    [Label("Couper le micro")] Mute,
    [Label("Réactiver le micro")] Unmute,
    [Label("Basculer la sourdine (casque)")] ToggleDeafen,
    [Label("Activer la sourdine")] Deafen,
    [Label("Désactiver la sourdine")] Undeafen,
    [Label("Rejoindre un salon vocal")] JoinVoiceChannel,
    [Label("Quitter le salon vocal")] LeaveVoiceChannel,
    [Label("Régler le volume du micro")] SetInputVolume,
    [Label("Régler le volume du son")] SetOutputVolume,
}

public sealed record DiscordRequest(DiscordCommand Command, string? ChannelId = null, int Volume = 100);

public interface IDiscordService
{
    Task ExecuteAsync(DiscordRequest request, CancellationToken ct);
}

[ActionType("discord", "Discord", Category = "Communication", Glyph = "\uE8BD", Order = 81,
    Description = "Micro, sourdine, salons vocaux et volumes dans l'application Discord (à configurer dans l'onglet Intégrations).")]
public sealed class DiscordAction : DeckAction
{
    [Field("Commande", Kind = FieldKind.Choice, Order = 1)]
    public DiscordCommand Command { get; set; } = DiscordCommand.ToggleMute;

    [Field("Identifiant du salon vocal", Order = 2, VisibleWhen = nameof(Command), VisibleValues = nameof(DiscordCommand.JoinVoiceChannel),
        Placeholder = "123456789012345678",
        Hint = "Discord › Paramètres › Avancés › Mode développeur, puis clic droit sur le salon › Copier l'identifiant.")]
    public string? ChannelId { get; set; }

    [Field("Volume", Kind = FieldKind.Slider, Order = 3, Min = 0, Max = 200, Unit = "%", VisibleWhen = nameof(Command),
        VisibleValues = "SetInputVolume,SetOutputVolume", Hint = "Micro : 0 à 100 %. Son : 0 à 200 %.")]
    public int Volume { get; set; } = 100;

    public override string Describe() => Command switch
    {
        DiscordCommand.JoinVoiceChannel => $"Discord : rejoindre le salon {Quote(ChannelId)}",
        DiscordCommand.SetInputVolume or DiscordCommand.SetOutputVolume => $"Discord : {EnumLabels.Get(Command).ToLowerInvariant()} à {Volume} %",
        _ => "Discord : " + EnumLabels.Get(Command).ToLowerInvariant(),
    };

    public override IEnumerable<string> Validate()
    {
        if (Command == DiscordCommand.JoinVoiceChannel && (string.IsNullOrWhiteSpace(ChannelId) || !ChannelId.Trim().All(char.IsAsciiDigit)))
            yield return "Indiquez l'identifiant numérique du salon vocal.";
        if (Command == DiscordCommand.SetInputVolume && Volume is < 0 or > 100) yield return "Le volume du micro va de 0 à 100 %.";
        if (Command == DiscordCommand.SetOutputVolume && Volume is < 0 or > 200) yield return "Le volume du son va de 0 à 200 %.";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        foreach (var problem in Validate()) throw new ActionException(problem);
        return ctx.Discord.ExecuteAsync(new DiscordRequest(Command, ChannelId?.Trim(), Volume), ct);
    }
}

internal sealed class UnavailableDiscordService : IDiscordService
{
    public static readonly UnavailableDiscordService Instance = new();
    public Task ExecuteAsync(DiscordRequest request, CancellationToken ct) => throw new ActionException("Discord n'est pas disponible.");
}

/// <summary>Jetons OAuth2 de Discord, conservés chiffrés par l'application (jamais dans config.json).</summary>
public sealed record DiscordTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public interface IDiscordCredentials
{
    string? ClientId { get; }
    string? ClientSecret { get; }
    DiscordTokens? Tokens { get; set; }
}

/// <summary>
/// RPC local de Discord (tube nommé discord-ipc-N, documentation Discord « RPC »). Réservé par Discord aux applications
/// approuvées et à leurs testeurs : l'utilisateur crée sa propre application sur le portail développeur de Discord et
/// renseigne son identifiant et son secret. La connexion reste ouverte tant que ZogmaDeck tourne, car Discord rétablit
/// les réglages vocaux quand l'application qui les a modifiés se déconnecte.
/// </summary>
public sealed class DiscordRpcClient(IDiscordCredentials credentials, HttpMessageHandler? http = null, string pipePrefix = "discord-ipc-")
    : IDiscordService, IAsyncDisposable
{
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const string TokenEndpoint = "https://discord.com/api/oauth2/token";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private bool _authenticated;

    public bool IsConnected => _pipe?.IsConnected == true && _authenticated;

    public async Task ExecuteAsync(DiscordRequest r, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureReadyAsync(authorizeIfNeeded: false, ct);
            switch (r.Command)
            {
                case DiscordCommand.ToggleMute:
                case DiscordCommand.ToggleDeafen:
                {
                    var settings = await SendAsync("GET_VOICE_SETTINGS", null, ct);
                    var field = r.Command == DiscordCommand.ToggleMute ? "mute" : "deaf";
                    var current = settings[field]?.GetValue<bool>() ?? false;
                    await SendAsync("SET_VOICE_SETTINGS", new JsonObject { [field] = !current }, ct);
                    break;
                }
                case DiscordCommand.Mute or DiscordCommand.Unmute:
                    await SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = r.Command == DiscordCommand.Mute }, ct);
                    break;
                case DiscordCommand.Deafen or DiscordCommand.Undeafen:
                    await SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = r.Command == DiscordCommand.Deafen }, ct);
                    break;
                case DiscordCommand.SetInputVolume:
                    await SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["input"] = new JsonObject { ["volume"] = Math.Clamp(r.Volume, 0, 100) } }, ct);
                    break;
                case DiscordCommand.SetOutputVolume:
                    await SendAsync("SET_VOICE_SETTINGS", new JsonObject { ["output"] = new JsonObject { ["volume"] = Math.Clamp(r.Volume, 0, 200) } }, ct);
                    break;
                case DiscordCommand.JoinVoiceChannel:
                    await SendAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = r.ChannelId, ["force"] = true }, ct);
                    break;
                case DiscordCommand.LeaveVoiceChannel:
                    await SendAsync("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null }, ct);
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Autorisation initiale (bouton des réglages) : Discord affiche une demande à accepter.</summary>
    public async Task<string> AuthorizeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            credentials.Tokens = null;
            await DisconnectAsync();
            await EnsureReadyAsync(authorizeIfNeeded: true, ct);
            return "ZogmaDeck est autorisé dans Discord.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ForgetAsync()
    {
        credentials.Tokens = null;
        await DisconnectAsync();
    }

    private async Task EnsureReadyAsync(bool authorizeIfNeeded, CancellationToken ct)
    {
        if (IsConnected) return;
        await DisconnectAsync();
        var clientId = credentials.ClientId?.Trim();
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(credentials.ClientSecret))
            throw new ActionException("Discord n'est pas configuré : éditeur › Intégrations › Discord (identifiant et secret de votre application Discord).");
        if (credentials.Tokens is null && !authorizeIfNeeded)
            throw new ActionException("ZogmaDeck n'est pas encore autorisé dans Discord : éditeur › Intégrations › « Autoriser dans Discord ».");

        await ConnectPipeAsync(clientId, ct);

        var tokens = credentials.Tokens;
        if (tokens is not null && tokens.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
            tokens = await TryRefreshAsync(tokens, ct);
        if (tokens is null)
        {
            if (!authorizeIfNeeded) throw new ActionException("L'autorisation Discord a expiré : éditeur › Intégrations › « Autoriser dans Discord ».");
            // Discord affiche la demande d'autorisation ; on laisse 2 minutes à l'utilisateur pour l'accepter.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            var auth = await SendAsync("AUTHORIZE", new JsonObject
            {
                ["client_id"] = clientId,
                ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)s).ToArray()),
            }, wait.Token, timeout: TimeSpan.FromMinutes(2));
            var code = auth["code"]?.GetValue<string>() ?? throw new ActionException("Discord n'a pas renvoyé de code d'autorisation.");
            tokens = await ExchangeAsync(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code }, ct);
        }
        credentials.Tokens = tokens;
        try
        {
            await SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = tokens.AccessToken }, ct);
        }
        catch (ActionException) when (!authorizeIfNeeded)
        {
            credentials.Tokens = null;
            throw new ActionException("Discord a refusé l'autorisation enregistrée : éditeur › Intégrations › « Autoriser dans Discord ».");
        }
        _authenticated = true;
    }

    private async Task ConnectPipeAsync(string clientId, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", pipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync();
                continue;
            }
            _pipe = pipe;
            await WriteFrameAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            var (op, ready) = await ReadFrameAsync(timeout.Token);
            if (op == 2 || ready?["evt"]?.GetValue<string>() != "READY")
            {
                await DisconnectAsync();
                throw new ActionException("Discord a refusé la connexion : " + (ready?["message"]?.GetValue<string>() ?? "identifiant d'application invalide ?"));
            }
            _readerCts = new CancellationTokenSource();
            _ = ReadLoopAsync(pipe, _readerCts.Token);
            return;
        }
        throw new ActionException("Discord ne répond pas : lancez l'application Discord (le navigateur ne suffit pas).");
    }

    private async Task<JsonObject> SendAsync(string cmd, JsonObject? args, CancellationToken ct, TimeSpan? timeout = null)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            var payload = new JsonObject { ["cmd"] = cmd, ["nonce"] = nonce, ["args"] = args ?? new JsonObject() };
            await WriteFrameAsync(1, payload, ct);
            var reply = await tcs.Task.WaitAsync(timeout ?? RequestTimeout, ct);
            if (reply["evt"]?.GetValue<string>() == "ERROR")
            {
                var code = reply["data"]?["code"]?.GetValue<int>() ?? 0;
                var message = reply["data"]?["message"]?.GetValue<string>() ?? "erreur";
                throw new ActionException(code switch
                {
                    5000 or 5001 or 5002 => "Discord : action impossible (salon introuvable ou accès refusé).",
                    5003 => "Discord : vous êtes déjà dans un salon vocal.",
                    4006 => "Discord : autorisation manquante.",
                    _ => $"Discord a refusé la commande : {message} (code {code}).",
                });
            }
            return reply["data"] as JsonObject ?? new JsonObject();
        }
        catch (TimeoutException)
        {
            throw new ActionException(cmd == "AUTHORIZE"
                ? "Autorisation Discord non acceptée à temps."
                : "Discord ne répond pas.");
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (op, payload) = await ReadFrameAsync(ct);
                if (op == 2) break; // fermeture
                if (op == 3) // ping
                {
                    await WriteFrameAsync(4, payload ?? new JsonObject(), ct);
                    continue;
                }
                if (payload?["nonce"]?.GetValue<string>() is { } nonce && _pending.TryGetValue(nonce, out var tcs)) tcs.TrySetResult(payload);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException) { }
        _authenticated = false;
        foreach (var p in _pending.Values) p.TrySetException(new ActionException("Connexion à Discord perdue."));
    }

    private async Task WriteFrameAsync(int op, JsonObject payload, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        var pipe = _pipe ?? throw new ActionException("Discord n'est pas connecté.");
        await pipe.WriteAsync(frame, ct);
        await pipe.FlushAsync(ct);
    }

    private async Task<(int Op, JsonObject? Payload)> ReadFrameAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("Tube fermé.");
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 16 * 1024 * 1024) throw new IOException("Trame Discord invalide.");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        return (op, length == 0 ? null : JsonNode.Parse(body) as JsonObject);
    }

    private async Task<DiscordTokens?> TryRefreshAsync(DiscordTokens tokens, CancellationToken ct)
    {
        try
        {
            return await ExchangeAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = tokens.RefreshToken }, ct);
        }
        catch (ActionException)
        {
            return null;
        }
    }

    /// <summary>Échange OAuth2 (code ou jeton de renouvellement) auprès de discord.com : seule connexion Internet de l'intégration.</summary>
    private async Task<DiscordTokens> ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = credentials.ClientId!.Trim();
        form["client_secret"] = credentials.ClientSecret!.Trim();
        using var client = http is null ? new HttpClient() : new HttpClient(http, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(20);
        var (status, body) = await PostAsync(client, form, ct);
        // Certaines applications Discord exigent l'adresse de redirection déclarée sur le portail (http://localhost conseillé).
        if (status == 400 && body.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase) && !form.ContainsKey("redirect_uri"))
        {
            form["redirect_uri"] = "http://localhost";
            (status, body) = await PostAsync(client, form, ct);
        }
        if (status is < 200 or >= 300)
        {
            var error = TryGet(body, "error_description") ?? TryGet(body, "error") ?? $"HTTP {status}";
            throw new ActionException($"Discord a refusé l'autorisation : {error}. Vérifiez l'identifiant et le secret de l'application.");
        }
        var json = JsonNode.Parse(body)!;
        return new DiscordTokens(
            json["access_token"]!.GetValue<string>(),
            json["refresh_token"]?.GetValue<string>() ?? "",
            DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3600));
    }

    private static async Task<(int Status, string Body)> PostAsync(HttpClient client, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var response = await client.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException ex)
        {
            throw new ActionException("discord.com est injoignable (connexion Internet ?) : " + ex.Message, ex);
        }
    }

    private static string? TryGet(string json, string name)
    {
        try { return JsonNode.Parse(json)?[name]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }

    private async Task DisconnectAsync()
    {
        _authenticated = false;
        _readerCts?.Cancel();
        _readerCts = null;
        if (_pipe is { } p)
        {
            _pipe = null;
            await p.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
