using System.IO;
using System.Windows.Media;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Services;

/// <summary>Lecture audio via MediaPlayer (Media Foundation de Windows), sans dépendance externe.</summary>
public sealed class AudioService : IAudioService
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private readonly List<MediaPlayer> _active = [];

    public int ActiveCount => _active.Count;

    public async Task PlayAsync(string path, double volume, bool waitForEnd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ActionException("Aucun fichier audio n'est défini.");
        if (!File.Exists(path)) throw new ActionException($"Fichier audio introuvable : {path}");
        ct.ThrowIfCancellationRequested();

        var name = Path.GetFileName(path);
        var player = new MediaPlayer { Volume = Math.Clamp(volume, 0, 1) };
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, e) =>
        {
            var ex = new ActionException($"Lecture impossible de « {name} » : {e.ErrorException?.Message ?? "format non pris en charge"}");
            opened.TrySetException(ex);
            ended.TrySetException(ex);
            Release(player);
        };
        player.MediaEnded += (_, _) =>
        {
            ended.TrySetResult();
            Release(player);
        };

        _active.Add(player);
        player.Open(new Uri(Path.GetFullPath(path)));
        player.Play();

        try
        {
            await opened.Task.WaitAsync(OpenTimeout, ct);
        }
        catch (TimeoutException)
        {
            Release(player);
            throw new ActionException($"Le fichier « {name} » ne s'ouvre pas (format non pris en charge ?).");
        }
        catch (OperationCanceledException)
        {
            Release(player);
            throw;
        }

        if (!waitForEnd) return;
        try
        {
            await ended.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Release(player);
            throw;
        }
    }

    public void StopAll()
    {
        if (_active.Count > 0) Log.Info($"Arrêt de {_active.Count} son(s).");
        foreach (var p in _active.ToList()) Release(p);
    }

    private void Release(MediaPlayer p)
    {
        if (!_active.Remove(p)) return;
        try
        {
            p.Stop();
            p.Close();
        }
        catch (InvalidOperationException) { }
    }
}
