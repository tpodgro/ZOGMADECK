using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

/// <summary>
/// Une seule instance par session et par dossier de données : un second lancement demande simplement
/// à la première d'afficher le panneau. (Un dossier --data différent donne une instance séparée.)
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexBase = @"Local\ZogmaDeck.Instance";
    private const string EventBase = @"Local\ZogmaDeck.ShowPanel";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    private static string Suffix(string? key) => string.IsNullOrEmpty(key)
        ? ""
        : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.ToUpperInvariant())))[..16];

    /// <param name="key">Dossier de données explicite (--data), ou null pour l'emplacement par défaut.</param>
    public static SingleInstance? TryAcquire(string? key = null)
    {
        var mutex = new Mutex(true, MutexBase + Suffix(key), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, EventBase + Suffix(key)));
    }

    /// <summary>Attente passive (aucune interrogation) d'un signal d'une autre instance.</summary>
    public void ListenForActivation(Action onActivate) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);

    public static bool SignalExisting(string? key = null)
    {
        try
        {
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            using var e = EventWaitHandle.OpenExisting(EventBase + Suffix(key));
            return e.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
