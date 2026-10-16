using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ZogmaDeck.Core.Obs;

namespace ZogmaDeck.Tests;

public class ObsProtocolTests
{
    [Fact]
    public void Authentication_MatchesOfficialDocumentationExample()
    {
        // Exemple de la documentation obs-websocket 5 (vérifié indépendamment en Python).
        var auth = ObsProtocol.ComputeAuthentication("supersecretpassword",
            "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");
        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", auth);
    }

    [Fact]
    public void Authentication_HandlesUnicodePasswords()
    {
        Assert.Equal("oGCkxKWaa99IBugF9yoC+kPyTX572vbKgkTtPPt97Go=", ObsProtocol.ComputeAuthentication("mot de passe é€", "sel", "defi"));
    }

    [Fact]
    public void Identify_HasExpectedShape_AndNoEventSubscriptions()
    {
        var msg = JsonNode.Parse(ObsProtocol.BuildIdentify("abc"))!;
        Assert.Equal(1, msg["op"]!.GetValue<int>());
        Assert.Equal(1, msg["d"]!["rpcVersion"]!.GetValue<int>());
        Assert.Equal("abc", msg["d"]!["authentication"]!.GetValue<string>());
        Assert.Equal(0, msg["d"]!["eventSubscriptions"]!.GetValue<int>()); // aucun événement : pas de trafic inutile

        var noAuth = JsonNode.Parse(ObsProtocol.BuildIdentify(null))!;
        Assert.Null(noAuth["d"]!["authentication"]);
    }

    [Fact]
    public void Request_HasExpectedShape()
    {
        var msg = JsonNode.Parse(ObsProtocol.BuildRequest("SetCurrentProgramScene", "id1", new JsonObject { ["sceneName"] = "A" }))!;
        Assert.Equal(6, msg["op"]!.GetValue<int>());
        Assert.Equal("SetCurrentProgramScene", msg["d"]!["requestType"]!.GetValue<string>());
        Assert.Equal("id1", msg["d"]!["requestId"]!.GetValue<string>());
        Assert.Equal("A", msg["d"]!["requestData"]!["sceneName"]!.GetValue<string>());
    }

    [Fact]
    public void Parse_RejectsGarbage()
    {
        Assert.Throws<ObsException>(() => ObsProtocol.Parse("not json"));
        Assert.Throws<ObsException>(() => ObsProtocol.Parse("""{"d":{}}"""));
        Assert.Equal(ObsOpCode.Hello, ObsProtocol.Parse("""{"op":0,"d":{"rpcVersion":1}}""").Op);
    }

    [Fact]
    public void ErrorMessages_AreFrenchAndSpecific()
    {
        Assert.Contains("Mot de passe OBS incorrect", ObsProtocol.DescribeCloseCode(4009, null));
        Assert.Contains("obs-websocket 5", ObsProtocol.DescribeCloseCode(4010, null));
        Assert.Contains("introuvable", ObsProtocol.DescribeRequestStatus("SetCurrentProgramScene", 600, "No source was found"));
        Assert.Contains("déjà active", ObsProtocol.DescribeRequestStatus("StartRecord", 500, null));
    }
}

/// <summary>Faux serveur OBS en mémoire, piloté par le test.</summary>
internal sealed class ScriptedObsServer : IObsSocketFactory
{
    public string? Password { get; init; }
    public bool RefuseConnection { get; init; }
    public Func<string, JsonObject?, (bool Ok, int Code, string? Comment, JsonObject? Data)?> Handler { get; set; } =
        (_, _) => (true, 100, null, null);
    public List<string> Requests { get; } = [];
    public int Connections { get; private set; }
    public FakeSocket? Current { get; private set; }

    public Task<IObsSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        if (RefuseConnection) throw new SocketException((int)SocketError.ConnectionRefused);
        Connections++;
        Current = new FakeSocket(this);
        return Task.FromResult<IObsSocket>(Current);
    }

    internal sealed class FakeSocket : IObsSocket
    {
        private readonly ScriptedObsServer _server;
        private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
        private const string Salt = "c2VsLWRlLXRlc3Q=", Challenge = "ZGVmaS1kZS10ZXN0";

        public FakeSocket(ScriptedObsServer server)
        {
            _server = server;
            var hello = new JsonObject { ["obsStudioVersion"] = "31.0.0", ["obsWebSocketVersion"] = "5.5.0", ["rpcVersion"] = 1 };
            if (server.Password is not null) hello["authentication"] = new JsonObject { ["challenge"] = Challenge, ["salt"] = Salt };
            Push(new JsonObject { ["op"] = 0, ["d"] = hello });
        }

        public int? CloseStatus { get; private set; }
        public string? CloseDescription { get; private set; }

        private void Push(JsonObject msg) => _toClient.Writer.TryWrite(msg.ToJsonString());

        public void ServerClose(int code)
        {
            CloseStatus = code;
            _toClient.Writer.TryWrite(null);
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            if (CloseStatus is not null) throw new WebSocketException("fermé");
            var msg = JsonNode.Parse(text)!;
            var op = msg["op"]!.GetValue<int>();
            var d = msg["d"]!.AsObject();
            if (op == 1)
            {
                if (_server.Password is { } pw)
                {
                    var expected = ObsProtocol.ComputeAuthentication(pw, Salt, Challenge);
                    if (d["authentication"]?.GetValue<string>() != expected)
                    {
                        ServerClose(4009);
                        return Task.CompletedTask;
                    }
                }
                Push(new JsonObject { ["op"] = 2, ["d"] = new JsonObject { ["negotiatedRpcVersion"] = 1 } });
            }
            else if (op == 6)
            {
                var type = d["requestType"]!.GetValue<string>();
                _server.Requests.Add(type);
                var reply = _server.Handler(type, d["requestData"] as JsonObject);
                if (reply is not { } r) return Task.CompletedTask; // pas de réponse : simule un OBS figé
                var resp = new JsonObject
                {
                    ["requestType"] = type,
                    ["requestId"] = d["requestId"]!.GetValue<string>(),
                    ["requestStatus"] = new JsonObject { ["result"] = r.Ok, ["code"] = r.Code, ["comment"] = r.Comment },
                };
                if (r.Data is not null) resp["responseData"] = r.Data;
                Push(new JsonObject { ["op"] = 7, ["d"] = resp });
            }
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _toClient.Reader.ReadAsync(ct);

        public Task CloseAsync(CancellationToken ct)
        {
            CloseStatus ??= 1000;
            _toClient.Writer.TryWrite(null);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class ObsClientTests
{
    private static ObsClient Client(ScriptedObsServer server, string? password = null, double timeoutSeconds = 5) =>
        new(server, () => new ObsEndpoint("localhost", 4455, TimeSpan.FromSeconds(timeoutSeconds)), () => password);

    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Fact]
    public async Task ConnectsWithoutPassword_AndSendsRequest()
    {
        var server = new ScriptedObsServer { Handler = (_, _) => (true, 100, null, new JsonObject { ["outputActive"] = true }) };
        await using var client = Client(server);
        var data = await client.SendRequestAsync("ToggleRecord", null, Timeout);
        Assert.Equal(ObsConnectionState.Connected, client.State);
        Assert.Equal("31.0.0", client.ObsVersion);
        Assert.True(data.GetProperty("outputActive").GetBoolean());
        Assert.Equal(["ToggleRecord"], server.Requests);
    }

    [Fact]
    public async Task ConnectsWithCorrectPassword()
    {
        var server = new ScriptedObsServer { Password = "secret" };
        await using var client = Client(server, "secret");
        await client.ConnectAsync(Timeout);
        Assert.Equal(ObsConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task WrongPassword_GivesClearError()
    {
        var server = new ScriptedObsServer { Password = "secret" };
        await using var client = Client(server, "faux");
        var ex = await Assert.ThrowsAsync<ObsException>(() => client.ConnectAsync(Timeout));
        Assert.Contains("Mot de passe OBS incorrect", ex.Message);
        Assert.Equal(ObsConnectionState.Failed, client.State);
        Assert.Equal(ex.Message, client.LastError);
    }

    [Fact]
    public async Task MissingPassword_GivesClearError()
    {
        var server = new ScriptedObsServer { Password = "secret" };
        await using var client = Client(server, null);
        var ex = await Assert.ThrowsAsync<ObsException>(() => client.ConnectAsync(Timeout));
        Assert.Contains("demande un mot de passe", ex.Message);
    }

    [Fact]
    public async Task ServerUnavailable_GivesClearError()
    {
        var server = new ScriptedObsServer { RefuseConnection = true };
        await using var client = Client(server);
        var ex = await Assert.ThrowsAsync<ObsException>(() => client.SendRequestAsync("StartRecord", null, Timeout));
        Assert.Contains("Impossible de joindre OBS", ex.Message);
        Assert.Equal(ObsConnectionState.Failed, client.State);
    }

    [Fact]
    public async Task FailedRequestStatus_IsTranslated()
    {
        var server = new ScriptedObsServer { Handler = (_, _) => (false, 600, "No source was found by the name of `Inconnue`.", null) };
        await using var client = Client(server);
        var ex = await Assert.ThrowsAsync<ObsException>(() =>
            client.SendRequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = "Inconnue" }, Timeout));
        Assert.Contains("introuvable", ex.Message);
        Assert.Equal(ObsConnectionState.Connected, client.State); // une requête en erreur ne coupe pas la connexion
    }

    [Fact]
    public async Task UnansweredRequest_TimesOut()
    {
        var server = new ScriptedObsServer { Handler = (_, _) => null };
        await using var client = Client(server, timeoutSeconds: 0.3);
        var ex = await Assert.ThrowsAsync<ObsException>(() => client.SendRequestAsync("StartStream", null, Timeout));
        Assert.Contains("n'a pas répondu", ex.Message);
    }

    [Fact]
    public async Task ServerDisconnect_FailsPendingRequest_ThenReconnectsOnNextRequest()
    {
        var server = new ScriptedObsServer();
        server.Handler = (type, _) =>
        {
            if (type != "Freeze") return (true, 100, null, null);
            server.Current!.ServerClose(1001);
            return null;
        };
        await using var client = Client(server);
        await client.ConnectAsync(Timeout);

        var ex = await Assert.ThrowsAsync<ObsException>(() => client.SendRequestAsync("Freeze", null, Timeout));
        Assert.Contains("OBS", ex.Message);
        await WaitUntil(() => client.State == ObsConnectionState.Disconnected);

        await client.SendRequestAsync("GetVersion", null, Timeout);
        Assert.Equal(2, server.Connections);
        Assert.Equal(ObsConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task SceneList_IsReturnedInObsOrder()
    {
        var server = new ScriptedObsServer
        {
            Handler = (_, _) => (true, 100, null, new JsonObject
            {
                ["scenes"] = new JsonArray(
                    new JsonObject { ["sceneName"] = "Bas", ["sceneIndex"] = 0 },
                    new JsonObject { ["sceneName"] = "Haut", ["sceneIndex"] = 1 }),
            }),
        };
        await using var client = Client(server);
        Assert.Equal(["Haut", "Bas"], await client.GetSceneNamesAsync(Timeout));
    }

    [Fact]
    public async Task StateChanged_IsRaised()
    {
        var states = new List<ObsConnectionState>();
        var server = new ScriptedObsServer();
        await using var client = Client(server);
        client.StateChanged += (_, _) => states.Add(client.State);
        await client.ConnectAsync(Timeout);
        await client.DisconnectAsync();
        Assert.Equal([ObsConnectionState.Connecting, ObsConnectionState.Connected, ObsConnectionState.Disconnected], states);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }
}

/// <summary>Test d'intégration réseau : vrai serveur WebSocket local (TcpListener + WebSocket.CreateFromStream), sans droits administrateur.</summary>
public class ObsWebSocketIntegrationTests
{
    [Fact]
    public async Task RealWebSocket_HandshakeAuthAndRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string password = "p@ss é";
        var received = new List<string>();

        var serverTask = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(cts.Token);
            var stream = tcp.GetStream();
            var protocol = await AcceptUpgradeAsync(stream, cts.Token);
            Assert.Equal("obsws.json", protocol);
            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = "obsws.json" });

            await SendAsync(ws, """{"op":0,"d":{"obsStudioVersion":"31.1.0","obsWebSocketVersion":"5.6.0","rpcVersion":1,"authentication":{"challenge":"Y2hhbA==","salt":"c2Fs"}}}""", cts.Token);
            var identify = JsonNode.Parse(await ReceiveAsync(ws, cts.Token))!;
            received.Add(identify.ToJsonString());
            if (identify["d"]!["authentication"]!.GetValue<string>() != ObsProtocol.ComputeAuthentication(password, "c2Fs", "Y2hhbA=="))
            {
                await ws.CloseAsync((WebSocketCloseStatus)4009, "Authentication failed.", cts.Token);
                return;
            }
            await SendAsync(ws, """{"op":2,"d":{"negotiatedRpcVersion":1}}""", cts.Token);

            var request = JsonNode.Parse(await ReceiveAsync(ws, cts.Token))!;
            received.Add(request.ToJsonString());
            var id = request["d"]!["requestId"]!.GetValue<string>();
            await SendAsync(ws, new JsonObject
            {
                ["op"] = 7,
                ["d"] = new JsonObject
                {
                    ["requestType"] = "SetCurrentProgramScene",
                    ["requestId"] = id,
                    ["requestStatus"] = new JsonObject { ["result"] = true, ["code"] = 100 },
                },
            }.ToJsonString(), cts.Token);

            // Fermeture côté OBS (ex. OBS quitté)
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            try { await ReceiveAsync(ws, cts.Token); } catch (WebSocketException) { }
        }, cts.Token);

        await using var client = new ObsClient(new ClientWebSocketFactory(),
            () => new ObsEndpoint("127.0.0.1", port, TimeSpan.FromSeconds(5)), () => password);
        await client.SendRequestAsync("SetCurrentProgramScene", new JsonObject { ["sceneName"] = "Jeu" }, cts.Token);
        Assert.Equal("31.1.0", client.ObsVersion);

        await serverTask;
        for (var i = 0; i < 100 && client.State != ObsConnectionState.Disconnected; i++) await Task.Delay(20);
        Assert.Equal(ObsConnectionState.Disconnected, client.State);
        Assert.Contains("\"eventSubscriptions\":0", received[0]);
        Assert.Contains("\"sceneName\":\"Jeu\"", received[1]);
    }

    [Fact]
    public async Task RealWebSocket_ClosedPort_ReportsUnavailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // port libre : connexion refusée

        await using var client = new ObsClient(new ClientWebSocketFactory(),
            () => new ObsEndpoint("127.0.0.1", port, TimeSpan.FromSeconds(2)), () => null);
        var ex = await Assert.ThrowsAsync<ObsException>(() => client.ConnectAsync(CancellationToken.None));
        Assert.Contains("Impossible de joindre OBS", ex.Message);
    }

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
        headers.TryGetValue("Sec-WebSocket-Protocol", out var protocol);
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: obsws.json\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        return protocol;
    }

    private static Task SendAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("fermé");
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
