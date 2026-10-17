using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Integrations;

namespace ZogmaDeck.Tests;

/// <summary>Faux client TeamSpeak 3 : vrai serveur TCP local qui parle le protocole ClientQuery.</summary>
internal sealed class FakeTs3Server : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public List<string> Commands { get; } = [];
    public bool InputMuted { get; set; }
    public bool Connected { get; set; } = true;
    public string ApiKey { get; set; } = "CLE-TEST";

    public FakeTs3Server()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(tcp));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        using var _ = tcp;
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        async Task Send(string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text.Replace("\n", "\n\r")));
        await Send("TS3 Client\nWelcome to the TeamSpeak 3 ClientQuery interface, type \"help\" for a list of commands.\nselected schandlerid=1\n");
        var authed = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (Commands) Commands.Add(line);
            const string ok = "error id=0 msg=ok\n";
            if (line == "quit") return;
            if (line.StartsWith("auth apikey="))
            {
                authed = line == "auth apikey=" + ApiKey;
                await Send(authed ? ok : "error id=256 msg=invalid\\sapikey\n");
                continue;
            }
            if (!authed)
            {
                await Send("error id=1796 msg=currently\\snot\\spossible\n");
                continue;
            }
            if (line == "whoami") await Send(Connected ? "clid=5 cid=1\n" + ok : "error id=1794 msg=not\\sconnected\n");
            else if (line == "clientvariable clid=5 client_input_muted") await Send($"clid=5 client_input_muted={(InputMuted ? 1 : 0)}\n" + ok);
            else if (line.StartsWith("clientupdate client_input_muted="))
            {
                InputMuted = line.EndsWith('1');
                await Send(ok);
            }
            else if (line == "channellist") await Send("cid=1 pid=0 channel_order=0 channel_name=Accueil|cid=7 pid=0 channel_order=1 channel_name=Salle\\sde\\sjeu\n" + ok);
            else await Send(ok);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class TeamSpeak3Tests
{
    [Fact]
    public async Task ToggleMicrophone_ReadsStateThenInvertsIt()
    {
        using var server = new FakeTs3Server();
        var ts3 = new Ts3ClientQuery(() => "CLE-TEST", server.Port);
        await ts3.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.ToggleMicrophone), CancellationToken.None);
        Assert.True(server.InputMuted);
        await ts3.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.ToggleMicrophone), CancellationToken.None);
        Assert.False(server.InputMuted);
        Assert.Contains("auth apikey=CLE-TEST", server.Commands);
        Assert.Contains("clientvariable clid=5 client_input_muted", server.Commands);
    }

    [Fact]
    public async Task SwitchChannel_FindsChannelByEscapedName()
    {
        using var server = new FakeTs3Server();
        var ts3 = new Ts3ClientQuery(() => "CLE-TEST", server.Port);
        await ts3.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.SwitchChannel, Channel: "salle de jeu"), CancellationToken.None);
        Assert.Contains("clientmove cid=7 clid=5", server.Commands);

        var missing = await Assert.ThrowsAsync<ActionException>(() =>
            ts3.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.SwitchChannel, Channel: "Inconnu"), CancellationToken.None));
        Assert.Contains("introuvable", missing.Message);
    }

    [Fact]
    public async Task Connect_EscapesValues_AndNicknameRequiresServer()
    {
        using var server = new FakeTs3Server();
        var ts3 = new Ts3ClientQuery(() => "CLE-TEST", server.Port);
        await ts3.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.Connect, Server: "ts.exemple.fr:9987",
            ServerPassword: "mot de|passe", Nickname: "Zog Ma"), CancellationToken.None);
        Assert.Contains(@"connect address=ts.exemple.fr:9987 password=mot\sde\ppasse nickname=Zog\sMa", server.Commands);

        server.Connected = false;
        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            ts3.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.SetNickname, Nickname: "X"), CancellationToken.None));
        Assert.Contains("aucun serveur", ex.Message);
    }

    [Fact]
    public async Task WrongKey_AndClosedPort_GiveClearErrors()
    {
        using var server = new FakeTs3Server();
        var wrong = await Assert.ThrowsAsync<ActionException>(() =>
            new Ts3ClientQuery(() => "MAUVAISE", server.Port).ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.MuteMicrophone), CancellationToken.None));
        Assert.Contains("refusée", wrong.Message);

        var noKey = await Assert.ThrowsAsync<ActionException>(() =>
            new Ts3ClientQuery(() => null, server.Port).ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.MuteMicrophone), CancellationToken.None));
        Assert.Contains("manquante", noKey.Message);

        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await Assert.ThrowsAsync<TeamSpeakUnavailableException>(() =>
            new Ts3ClientQuery(() => "CLE", port).ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.MuteMicrophone), CancellationToken.None));
    }

    [Fact]
    public async Task Connect_FallsBackToLink_WhenTeamSpeak3IsNotRunning()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var shell = new FakeServices();
        var service = new TeamSpeakService(new Ts3ClientQuery(() => "CLE", port), new Ts6RemoteApps(() => null, _ => { }), shell);
        await service.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts3, TeamSpeakCommand.Connect, Server: "ts.exemple.fr", Nickname: "Zog"), CancellationToken.None);
        Assert.Contains("open:ts3server://ts.exemple.fr?nickname=Zog", shell.Calls);
    }

    [Theory]
    [InlineData("ts.exemple.fr", null, null, null, "ts3server://ts.exemple.fr")]
    [InlineData("ts.exemple.fr:9988", "secret é", "Zog Ma", "Lobby/Jeux", "ts3server://ts.exemple.fr?port=9988&nickname=Zog%20Ma&password=secret%20%C3%A9&channel=Lobby%2FJeux")]
    public void ConnectLink_IsBuilt(string server, string? pwd, string? nick, string? channel, string expected) =>
        Assert.Equal(expected, TeamSpeakLinks.Connect(server, pwd, nick, channel));

    [Theory]
    [InlineData("a b|c/d\\e", @"a\sb\pc\/d\\e")]
    [InlineData("ligne\nsuivante", @"ligne\nsuivante")]
    public void Escape_RoundTrips(string raw, string escaped)
    {
        Assert.Equal(escaped, Ts3ClientQuery.Escape(raw));
        Assert.Equal(raw, Ts3ClientQuery.Unescape(escaped));
    }

    [Fact]
    public void Validation_RejectsCommandsNotSupportedByVersion()
    {
        Assert.Contains("TeamSpeak 5/6 ne permet pas", new TeamSpeakAction { Client = TeamSpeakClient.Ts6, Command = TeamSpeakCommand.SwitchChannel, Channel = "x" }.Validate().Single());
        Assert.Contains("n'existent que", new TeamSpeakAction { Client = TeamSpeakClient.Ts3, Command = TeamSpeakCommand.RemoteButton, ButtonName = "x" }.Validate().Single());
        Assert.Empty(new TeamSpeakAction { Client = TeamSpeakClient.Ts6, Command = TeamSpeakCommand.ToggleMicrophone }.Validate());
    }
}

public class TeamSpeak6Tests
{
    private static async Task<string?> AcceptUpgradeAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) throw new IOException("connexion fermée");
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
        var headers = sb.ToString().Split("\r\n").Skip(1).Where(l => l.Contains(':'))
            .ToDictionary(l => l[..l.IndexOf(':')].Trim(), l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        return null;
    }

    private static async Task<string?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task PairThenPressButton_OverRealWebSocket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var received = new List<JsonNode>();
        string? storedKey = null;

        var server = Task.Run(async () =>
        {
            for (var connection = 0; connection < 2; connection++)
            {
                using var tcp = await listener.AcceptTcpClientAsync(cts.Token);
                var stream = tcp.GetStream();
                await AcceptUpgradeAsync(stream, cts.Token);
                using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                while (await ReceiveAsync(ws, cts.Token) is { } text)
                {
                    var msg = JsonNode.Parse(text)!;
                    lock (received) received.Add(msg);
                    if (msg["type"]!.GetValue<string>() == "auth")
                    {
                        var reply = new JsonObject
                        {
                            ["type"] = "auth",
                            ["status"] = new JsonObject { ["code"] = 0, ["message"] = "ok" },
                            ["payload"] = new JsonObject { ["apiKey"] = "cle-ts6", ["connections"] = new JsonArray() },
                        };
                        // Un événement non sollicité avant la réponse : doit être ignoré par le client.
                        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"clientMoved","payload":{}}"""), WebSocketMessageType.Text, true, cts.Token);
                        await ws.SendAsync(Encoding.UTF8.GetBytes(reply.ToJsonString()), WebSocketMessageType.Text, true, cts.Token);
                    }
                }
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); } catch (WebSocketException) { }
            }
        }, cts.Token);

        var ts6 = new Ts6RemoteApps(() => storedKey, k => storedKey = k, port: port);
        await Assert.ThrowsAsync<ActionException>(() =>
            ts6.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts6, TeamSpeakCommand.ToggleMicrophone), cts.Token)); // pas encore autorisé
        await ts6.PairAsync(TimeSpan.FromSeconds(10), cts.Token);
        Assert.Equal("cle-ts6", storedKey);

        await ts6.ExecuteAsync(new TeamSpeakRequest(TeamSpeakClient.Ts6, TeamSpeakCommand.ToggleMicrophone), cts.Token);
        await server;

        Assert.Equal("", received[0]["payload"]!["content"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("zogmadeck", received[0]["payload"]!["identifier"]!.GetValue<string>());
        Assert.Equal("cle-ts6", received[1]["payload"]!["content"]!["apiKey"]!.GetValue<string>());
        var presses = received.Where(m => m["type"]!.GetValue<string>() == "buttonPress").ToList();
        Assert.Equal(2, presses.Count);
        Assert.All(presses, p => Assert.Equal("zogmadeck.ToggleMicrophone", p["payload"]!["button"]!.GetValue<string>()));
        Assert.True(presses[0]["payload"]!["state"]!.GetValue<bool>());
        Assert.False(presses[1]["payload"]!["state"]!.GetValue<bool>());
    }

    [Fact]
    public void CustomButtonIds_AreSanitized() =>
        Assert.Equal("zogmadeck.Musique_lounge", Ts6RemoteApps.ButtonId(TeamSpeakCommand.RemoteButton, " Musique lounge "));
}

public class DiscordTests
{
    private sealed class MemoryCredentials : IDiscordCredentials
    {
        public string? ClientId { get; set; } = "123456789";
        public string? ClientSecret { get; set; } = "secret";
        public DiscordTokens? Tokens { get; set; }
    }

    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://discord.com/api/oauth2/token", request.RequestUri!.ToString());
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"jeton-acces","refresh_token":"jeton-renouvellement","expires_in":604800,"token_type":"Bearer"}"""),
            };
        }
    }

    /// <summary>Faux client Discord : tube nommé avec trames (opcode, longueur, JSON) comme l'IPC officiel.</summary>
    private static async Task RunFakeDiscordAsync(string pipeName, List<JsonObject> commands, bool[] voice, CancellationToken ct)
    {
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct);

        async Task<(int, JsonObject)> Read()
        {
            var header = new byte[8];
            await pipe.ReadExactlyAsync(header, ct);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
            await pipe.ReadExactlyAsync(body, ct);
            return (BinaryPrimitives.ReadInt32LittleEndian(header), (JsonObject)JsonNode.Parse(body)!);
        }

        async Task Write(int op, JsonObject payload)
        {
            var json = Encoding.UTF8.GetBytes(payload.ToJsonString());
            var frame = new byte[8 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, op);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
            json.CopyTo(frame, 8);
            await pipe.WriteAsync(frame, ct);
        }

        var (op, hello) = await Read();
        Assert.Equal(0, op);
        Assert.Equal("123456789", hello["client_id"]!.GetValue<string>());
        await Write(1, new JsonObject { ["cmd"] = "DISPATCH", ["evt"] = "READY", ["data"] = new JsonObject { ["v"] = 1 } });
        try
        {
            while (true)
            {
                var (_, cmd) = await Read();
                lock (commands) commands.Add(cmd);
                var name = cmd["cmd"]!.GetValue<string>();
                var data = name switch
                {
                    "AUTHORIZE" => new JsonObject { ["code"] = "code-autorisation" },
                    "AUTHENTICATE" => new JsonObject { ["application"] = new JsonObject { ["name"] = "ZogmaDeck" } },
                    "GET_VOICE_SETTINGS" => new JsonObject { ["mute"] = voice[0], ["deaf"] = voice[1] },
                    _ => new JsonObject(),
                };
                if (name == "SET_VOICE_SETTINGS" && cmd["args"]!["mute"] is { } m) voice[0] = m.GetValue<bool>();
                await Write(1, new JsonObject { ["cmd"] = name, ["nonce"] = cmd["nonce"]!.GetValue<string>(), ["evt"] = null, ["data"] = data });
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) { }
    }

    [Fact]
    public async Task AuthorizeThenToggleMute_OverRealNamedPipe()
    {
        var prefix = $"zogmadeck-test-{Guid.NewGuid():N}-";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var commands = new List<JsonObject>();
        var voice = new[] { false, false };
        var server = RunFakeDiscordAsync(prefix + "0", commands, voice, cts.Token);
        var credentials = new MemoryCredentials();
        var http = new FakeTokenEndpoint();
        await using var discord = new DiscordRpcClient(credentials, http, prefix);

        var notYet = await Assert.ThrowsAsync<ActionException>(() => discord.ExecuteAsync(new DiscordRequest(DiscordCommand.ToggleMute), cts.Token));
        Assert.Contains("pas encore autorisé", notYet.Message);

        await discord.AuthorizeAsync(cts.Token);
        Assert.Equal("jeton-acces", credentials.Tokens!.AccessToken);
        Assert.Contains("grant_type=authorization_code", http.Bodies[0]);
        Assert.Contains("code=code-autorisation", http.Bodies[0]);
        Assert.Contains("client_secret=secret", http.Bodies[0]);

        await discord.ExecuteAsync(new DiscordRequest(DiscordCommand.ToggleMute), cts.Token);
        Assert.True(voice[0]);
        await discord.ExecuteAsync(new DiscordRequest(DiscordCommand.ToggleMute), cts.Token);
        Assert.False(voice[0]);
        await discord.ExecuteAsync(new DiscordRequest(DiscordCommand.SetOutputVolume, Volume: 150), cts.Token);
        Assert.True(discord.IsConnected); // la connexion reste ouverte (sinon Discord annule le réglage)

        var names = commands.Select(c => c["cmd"]!.GetValue<string>()).ToList();
        Assert.Equal(["AUTHORIZE", "AUTHENTICATE", "GET_VOICE_SETTINGS", "SET_VOICE_SETTINGS", "GET_VOICE_SETTINGS", "SET_VOICE_SETTINGS", "SET_VOICE_SETTINGS"], names);
        Assert.Contains("rpc.voice.write", commands[0]["args"]!["scopes"]!.ToJsonString());
        Assert.Equal(150, commands[^1]["args"]!["output"]!["volume"]!.GetValue<int>());
        cts.Cancel();
        await server;
    }

    [Fact]
    public async Task DiscordNotRunning_GivesClearError()
    {
        var discord = new DiscordRpcClient(new MemoryCredentials { Tokens = new DiscordTokens("a", "r", DateTimeOffset.UtcNow.AddDays(1)) },
            new FakeTokenEndpoint(), $"zogmadeck-absent-{Guid.NewGuid():N}-");
        var ex = await Assert.ThrowsAsync<ActionException>(() => discord.ExecuteAsync(new DiscordRequest(DiscordCommand.Mute), CancellationToken.None));
        Assert.Contains("lancez l'application Discord", ex.Message);
    }

    [Fact]
    public void Validation_ChecksChannelAndVolumes()
    {
        Assert.NotEmpty(new DiscordAction { Command = DiscordCommand.JoinVoiceChannel, ChannelId = "abc" }.Validate());
        Assert.Empty(new DiscordAction { Command = DiscordCommand.JoinVoiceChannel, ChannelId = "199737254929760257" }.Validate());
        Assert.NotEmpty(new DiscordAction { Command = DiscordCommand.SetInputVolume, Volume = 150 }.Validate());
        Assert.Empty(new DiscordAction { Command = DiscordCommand.SetOutputVolume, Volume = 150 }.Validate());
    }
}
