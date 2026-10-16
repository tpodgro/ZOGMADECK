using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Core.Obs;

public enum ObsConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

public sealed record ObsEndpoint(string Host, int Port, TimeSpan RequestTimeout)
{
    public Uri ToUri()
    {
        var host = Host.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        if (!Uri.TryCreate($"ws://{host}:{Port}", UriKind.Absolute, out var uri))
            throw new ObsException($"Adresse OBS invalide : « {Host}:{Port} ».");
        return uri;
    }

    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>Transport de messages texte (abstrait pour les tests).</summary>
public interface IObsSocket : IAsyncDisposable
{
    Task SendAsync(string text, CancellationToken ct);
    /// <summary>Retourne null quand la connexion est fermée (voir <see cref="CloseStatus"/>).</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
    int? CloseStatus { get; }
    string? CloseDescription { get; }
    Task CloseAsync(CancellationToken ct);
}

public interface IObsSocketFactory
{
    Task<IObsSocket> ConnectAsync(Uri uri, CancellationToken ct);
}

public sealed class ClientWebSocketFactory : IObsSocketFactory
{
    public async Task<IObsSocket> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol(ObsProtocol.SubProtocol);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            return new ClientWebSocketObsSocket(ws);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private sealed class ClientWebSocketObsSocket(ClientWebSocket ws) : IObsSocket
    {
        private const int MaxMessageBytes = 16 * 1024 * 1024;

        public int? CloseStatus => (int?)ws.CloseStatus;
        public string? CloseDescription => ws.CloseStatusDescription;

        public Task SendAsync(string text, CancellationToken ct) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var ms = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult r;
                try
                {
                    r = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                }
                catch (WebSocketException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
                    catch (WebSocketException) { }
                    return null;
                }
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > MaxMessageBytes) throw new ObsException("Message OBS trop volumineux.");
                if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }

        public async Task CloseAsync(CancellationToken ct)
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            ws.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Client obs-websocket 5.x. Se connecte à la demande (première requête ou bouton « Tester »), sans interrogation périodique :
/// une seule boucle de réception attend les messages, et la déconnexion est détectée par la fermeture du socket.
/// </summary>
public sealed class ObsClient(IObsSocketFactory factory, Func<ObsEndpoint> endpoint, Func<string?> passwordProvider)
    : IObsService, IAsyncDisposable
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private IObsSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public ObsConnectionState State { get; private set; } = ObsConnectionState.Disconnected;
    public string? LastError { get; private set; }
    public string? ObsVersion { get; private set; }
    public string? EndpointDescription { get; private set; }

    /// <summary>Déclenché sur un thread quelconque.</summary>
    public event EventHandler? StateChanged;

    private void SetState(ObsConnectionState state, string? error = null)
    {
        State = state;
        LastError = error;
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn("Erreur d'un abonné à l'état OBS", ex); }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ObsConnectionState.Connected && _socket is not null) return;
            await TearDownAsync().ConfigureAwait(false);

            var ep = endpoint();
            EndpointDescription = ep.ToString();
            var uri = ep.ToUri();
            SetState(ObsConnectionState.Connecting);
            Log.Info($"Connexion à OBS ({ep})…");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeout);
            IObsSocket socket;
            try
            {
                socket = await factory.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ObsException($"OBS ne répond pas sur {ep} (délai dépassé). OBS est-il lancé, avec le serveur WebSocket activé ?");
            }
            catch (Exception ex) when (ex is WebSocketException or SocketException or HttpRequestException or IOException)
            {
                throw new ObsException($"Impossible de joindre OBS sur {ep}. Lancez OBS et activez le serveur WebSocket (Outils › Paramètres du serveur WebSocket).", ex);
            }

            try
            {
                var helloText = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false)
                                ?? throw new ObsException(ObsProtocol.DescribeCloseCode(socket.CloseStatus, socket.CloseDescription));
                var hello = ObsProtocol.Parse(helloText);
                if (hello.Op != ObsOpCode.Hello) throw new ObsException("Réponse inattendue : ce serveur n'est pas obs-websocket 5.");
                if (hello.Data.TryGetProperty("rpcVersion", out var rpc) && rpc.GetInt32() < ObsProtocol.RpcVersion)
                    throw new ObsException(ObsProtocol.DescribeCloseCode(4010, null));
                ObsVersion = hello.Data.TryGetProperty("obsStudioVersion", out var v) ? v.GetString() : null;

                string? auth = null;
                if (ObsProtocol.GetAuthChallenge(hello.Data) is { } challenge)
                {
                    var password = passwordProvider();
                    if (string.IsNullOrEmpty(password))
                        throw new ObsException("OBS demande un mot de passe, mais aucun n'est enregistré dans ZogmaDeck : saisissez-le dans l'éditeur › onglet OBS (même mot de passe que dans OBS › Outils › Paramètres du serveur WebSocket).");
                    auth = ObsProtocol.ComputeAuthentication(password, challenge.Salt, challenge.Challenge);
                }

                await socket.SendAsync(ObsProtocol.BuildIdentify(auth), timeout.Token).ConfigureAwait(false);
                var identifiedText = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false)
                                     ?? throw new ObsException(ObsProtocol.DescribeCloseCode(socket.CloseStatus, socket.CloseDescription));
                if (ObsProtocol.Parse(identifiedText).Op != ObsOpCode.Identified)
                    throw new ObsException("OBS n'a pas confirmé l'identification.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await socket.DisposeAsync().ConfigureAwait(false);
                throw new ObsException($"OBS ({ep}) n'a pas terminé la poignée de main dans le délai.");
            }
            catch
            {
                await socket.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _socket = socket;
            _loopCts = new CancellationTokenSource();
            _loop = Task.Run(() => ReceiveLoopAsync(socket, _loopCts.Token), CancellationToken.None);
            Log.Info($"Connecté à OBS {ObsVersion} ({ep}).");
            SetState(ObsConnectionState.Connected);
        }
        catch (ObsException ex)
        {
            Log.Warn("Connexion OBS : " + ex.Message);
            SetState(ObsConnectionState.Failed, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            SetState(ObsConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Connexion OBS : erreur inattendue", ex);
            var msg = "Erreur de connexion à OBS : " + ex.Message;
            SetState(ObsConnectionState.Failed, msg);
            throw new ObsException(msg, ex);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(IObsSocket socket, CancellationToken ct)
    {
        string? closeMessage = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var text = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                if (text is null)
                {
                    closeMessage = ObsProtocol.DescribeCloseCode(socket.CloseStatus, socket.CloseDescription);
                    break;
                }
                ObsMessage msg;
                try { msg = ObsProtocol.Parse(text); }
                catch (ObsException) { continue; }
                if (msg.Op == ObsOpCode.RequestResponse) HandleResponse(msg.Data);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            closeMessage = "Connexion à OBS perdue : " + ex.Message;
        }

        if (ct.IsCancellationRequested) return;
        Log.Warn("OBS : " + closeMessage);
        FailPending(new ObsException(closeMessage ?? "Connexion à OBS perdue."));
        if (ReferenceEquals(_socket, socket))
        {
            _socket = null;
            await socket.DisposeAsync().ConfigureAwait(false);
            SetState(ObsConnectionState.Disconnected, closeMessage);
        }
    }

    private void HandleResponse(JsonElement d)
    {
        if (!d.TryGetProperty("requestId", out var idEl) || idEl.GetString() is not { } id) return;
        if (!_pending.TryRemove(id, out var tcs)) return;
        var type = d.TryGetProperty("requestType", out var t) ? t.GetString() ?? "?" : "?";
        if (!d.TryGetProperty("requestStatus", out var status))
        {
            tcs.TrySetException(new ObsException($"Réponse OBS « {type} » incomplète."));
            return;
        }
        var ok = status.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.True;
        if (ok)
        {
            tcs.TrySetResult(d.TryGetProperty("responseData", out var data) ? data.Clone() : default);
            return;
        }
        var code = status.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        var comment = status.TryGetProperty("comment", out var cm) ? cm.GetString() : null;
        tcs.TrySetException(new ObsException(ObsProtocol.DescribeRequestStatus(type, code, comment)));
    }

    public async Task<JsonElement> SendRequestAsync(string requestType, JsonObject? requestData, CancellationToken ct)
    {
        if (State != ObsConnectionState.Connected || _socket is null) await ConnectAsync(ct).ConfigureAwait(false);
        var socket = _socket ?? throw new ObsException("Non connecté à OBS.");

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(ObsProtocol.BuildRequest(requestType, id, requestData), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                throw new ObsException("Connexion à OBS perdue pendant l'envoi.", ex);
            }
            finally
            {
                _sendLock.Release();
            }

            var timeout = endpoint().RequestTimeout;
            try
            {
                return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ObsException($"OBS n'a pas répondu à « {requestType} » en {timeout.TotalSeconds:0} s.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<IReadOnlyList<string>> GetSceneNamesAsync(CancellationToken ct)
    {
        var data = await SendRequestAsync("GetSceneList", null, ct).ConfigureAwait(false);
        var names = ExtractNames(data, "scenes", "sceneName");
        names.Reverse(); // OBS renvoie la liste de bas en haut
        return names;
    }

    public async Task<IReadOnlyList<string>> GetInputNamesAsync(CancellationToken ct)
    {
        var data = await SendRequestAsync("GetInputList", null, ct).ConfigureAwait(false);
        return ExtractNames(data, "inputs", "inputName");
    }

    private static List<string> ExtractNames(JsonElement data, string arrayName, string field)
    {
        var list = new List<string>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(arrayName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.TryGetProperty(field, out var n) && n.GetString() is { Length: > 0 } s)
                    list.Add(s);
        return list;
    }

    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAsync().ConfigureAwait(false);
            SetState(ObsConnectionState.Disconnected);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task TearDownAsync()
    {
        var socket = _socket;
        _socket = null;
        _loopCts?.Cancel();
        if (socket is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            await socket.DisposeAsync().ConfigureAwait(false);
        }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* déjà journalisé */ }
        }
        _loop = null;
        _loopCts?.Dispose();
        _loopCts = null;
        FailPending(new ObsException("Connexion à OBS fermée."));
    }

    private void FailPending(Exception ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(ex);
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync().ConfigureAwait(false);
        _connectLock.Dispose();
        _sendLock.Dispose();
    }
}
