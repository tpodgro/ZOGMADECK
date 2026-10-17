using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ZogmaDeck.Core.Diagnostics;
using static ZogmaDeck.Services.PackagedStartupTask;

namespace ZogmaDeck.Services;

/// <summary>Mise à jour disponible : version (si connue) et page où l'installer.</summary>
public sealed record UpdateInfo(string? Version, string Url, bool FromStore)
{
    public string MenuText => FromStore
        ? "Mettre à jour ZogmaDeck (Microsoft Store)…"
        : $"Mettre à jour vers la version {Version}…";
}

/// <summary>
/// Recherche des mises à jour, au plus deux fois par jour (aucune scrutation fréquente) et désactivable :
/// version Microsoft Store : le Store lui-même (Windows.Services.Store) ; installateur et version portable : la dernière
/// version publiée sur GitHub (seule information envoyée : la requête HTTP elle-même).
/// </summary>
public sealed class UpdateService
{
    public const string StoreProductId = "9PJJ1JLZDF4Z";
    public const string StorePage = "ms-windows-store://pdp/?ProductId=" + StoreProductId;
    public const string ReleasesApi = "https://api.github.com/repos/tpodgro/ZOGMADECK/releases/latest";
    public const string ReleasesPage = "https://github.com/tpodgro/ZOGMADECK/releases/latest";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(90), Interval = TimeSpan.FromHours(12);

    private readonly Func<bool> _enabled;
    private readonly Func<IntPtr> _window;
    private readonly DispatcherTimer _timer;
    private readonly HttpMessageHandler? _http;

    public UpdateService(Func<bool> enabled, Func<IntPtr> window, HttpMessageHandler? http = null)
    {
        _enabled = enabled;
        _window = window;
        _http = http;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (_enabled()) await CheckAsync(CancellationToken.None);
        };
    }

    public static Version CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(0, 0, 0);

    public UpdateInfo? Available { get; private set; }
    public DateTime? LastCheck { get; private set; }
    public string? LastResult { get; private set; }

    /// <summary>Nouvelle mise à jour détectée (pas à chaque vérification).</summary>
    public event EventHandler<UpdateInfo>? Found;
    public event EventHandler? Checked;

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        var previous = Available;
        try
        {
            Available = PackageInfo.IsPackaged ? await CheckStoreAsync() : await CheckGitHubAsync(ct);
            LastResult = Available is null
                ? $"ZogmaDeck {CurrentVersion.ToString(3)} est à jour."
                : Available.FromStore ? "Une mise à jour est disponible dans le Microsoft Store." : $"La version {Available.Version} est disponible.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or COMException or TimeoutException
                                       or ArgumentException or InvalidOperationException)
        {
            LastResult = "Vérification impossible : " + (ex is TaskCanceledException ? "délai dépassé." : ex.Message);
            Log.Warn("Recherche de mise à jour impossible", ex);
        }
        LastCheck = DateTime.Now;
        if (Available is not null && Available != previous)
        {
            Log.Info($"Mise à jour disponible : {Available.Version ?? "Microsoft Store"}");
            Found?.Invoke(this, Available);
        }
        Checked?.Invoke(this, EventArgs.Empty);
        return Available;
    }

    // ---------- GitHub (installateur, version portable) ----------
    private async Task<UpdateInfo?> CheckGitHubAsync(CancellationToken ct)
    {
        using var client = _http is null ? new HttpClient() : new HttpClient(_http, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"ZogmaDeck/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.GetAsync(ReleasesApi, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // aucune version publiée
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(ct), CurrentVersion);
    }

    /// <summary>Réponse de l'API « latest release » de GitHub ; null si la version publiée n'est pas plus récente.</summary>
    internal static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= current) return null;
        var url = r.TryGetProperty("html_url", out var u) && u.GetString() is { } s && s.StartsWith("https://github.com/", StringComparison.Ordinal)
            ? s
            : ReleasesPage;
        return new UpdateInfo(latest.ToString(3), url, FromStore: false);
    }

    // ---------- Microsoft Store (version empaquetée) ----------
    private static readonly Guid IidStoreContextStatics = new("9c06ee5f-15c0-4e72-9330-d6191cebd19c");
    private static readonly Guid IidInitializeWithWindow = new("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1");
    private const int StaticsGetDefault = 6;                     // IStoreContextStatics : GetDefault, GetForUser
    private const int ContextGetAppAndOptionalUpdates = 23;     // IStoreContext (voir Windows.Services.winmd)
    private const int VectorViewGetSize = 7;                     // IVectorView<T> : GetAt, get_Size…
    private const int InitializeWithWindowInitialize = 3;        // IInitializeWithWindow (IUnknown + Initialize)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeFn(IntPtr self, IntPtr hwnd);

    /// <summary>Interroge le Store (Windows.Services.Store.StoreContext) sur un thread de travail.</summary>
    private Task<UpdateInfo?> CheckStoreAsync()
    {
        var hwnd = _window();
        return Task.Run(() =>
        {
            var classId = IntPtr.Zero;
            var statics = IntPtr.Zero;
            var context = IntPtr.Zero;
            var op = IntPtr.Zero;
            var list = IntPtr.Zero;
            try
            {
                const string className = "Windows.Services.Store.StoreContext";
                Check(WindowsCreateString(className, className.Length, out classId));
                var iid = IidStoreContextStatics;
                Check(RoGetActivationFactory(classId, ref iid, out statics));
                Check(Invoke<OutPtr>(statics, StaticsGetDefault)(statics, out context));
                // Application de bureau : le contexte Store doit être rattaché à une fenêtre.
                var iidInit = IidInitializeWithWindow;
                if (Marshal.QueryInterface(context, in iidInit, out var init) == 0)
                {
                    try { Check(Marshal.GetDelegateForFunctionPointer<InitializeFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(init), InitializeWithWindowInitialize * IntPtr.Size))(init, hwnd)); }
                    finally { Marshal.Release(init); }
                }
                Check(Invoke<OutPtr>(context, ContextGetAppAndOptionalUpdates)(context, out op));
                Wait(op, TimeSpan.FromSeconds(30));
                Check(Invoke<OutPtr>(op, 8)(op, out list)); // IAsyncOperation<T>.GetResults
                Check(Invoke<OutInt>(list, VectorViewGetSize)(list, out var count));
                return count > 0 ? new UpdateInfo(null, StorePage, FromStore: true) : null;
            }
            finally
            {
                foreach (var p in new[] { list, op, context, statics }) if (p != IntPtr.Zero) Marshal.Release(p);
                if (classId != IntPtr.Zero) WindowsDeleteString(classId);
            }
        });
    }
}
