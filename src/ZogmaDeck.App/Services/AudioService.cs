using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Services;

/// <summary>
/// Lecture audio via MediaPlayer (Media Foundation de Windows), sans dépendance externe. Gère les extraits (début/fin)
/// et le comportement au nouvel appui (rejouer, arrêter, superposer) par source.
/// </summary>
public sealed class AudioService : IAudioService
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private readonly List<Playback> _active = [];

    private sealed class Playback(MediaPlayer player, object? owner)
    {
        public MediaPlayer Player { get; } = player;
        public object? Owner { get; } = owner;
        public DispatcherTimer? EndTimer { get; set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public int ActiveCount => _active.Count;

    public bool IsPlaying(object owner) => _active.Any(p => ReferenceEquals(p.Owner, owner));

    public async Task PlayAsync(SoundRequest request, CancellationToken ct)
    {
        var path = request.Path;
        if (string.IsNullOrWhiteSpace(path)) throw new ActionException("Aucun fichier audio n'est défini.");
        if (!File.Exists(path)) throw new ActionException($"Fichier audio introuvable : {path}");
        ct.ThrowIfCancellationRequested();

        // Nouvel appui pendant la lecture : on arrête d'abord les sons de la même source (sauf superposition).
        if (request.Owner is { } owner && request.Repeat != SoundRepeatMode.Overlap)
        {
            var running = _active.Where(p => ReferenceEquals(p.Owner, owner)).ToList();
            foreach (var p in running) Release(p);
            if (running.Count > 0 && request.Repeat == SoundRepeatMode.Stop) return;
        }

        var name = Path.GetFileName(path);
        var player = new MediaPlayer { Volume = Math.Clamp(request.Volume, 0, 1) };
        var playback = new Playback(player, request.Owner);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        player.MediaOpened += (_, _) => opened.TrySetResult();
        player.MediaFailed += (_, e) =>
        {
            var ex = new ActionException($"Lecture impossible de « {name} » : {e.ErrorException?.Message ?? "format non pris en charge"}");
            opened.TrySetException(ex);
            playback.Ended.TrySetException(ex);
            Release(playback);
        };
        player.MediaEnded += (_, _) => Release(playback);

        _active.Add(playback);
        player.Open(new Uri(Path.GetFullPath(path)));

        try
        {
            await opened.Task.WaitAsync(OpenTimeout, ct);
        }
        catch (TimeoutException)
        {
            Release(playback);
            throw new ActionException($"Le fichier « {name} » ne s'ouvre pas (format non pris en charge ?).");
        }
        catch (OperationCanceledException)
        {
            Release(playback);
            throw;
        }
        if (!_active.Contains(playback)) return; // arrêté pendant l'ouverture

        var duration = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : (TimeSpan?)null;
        if (request.Start > TimeSpan.Zero)
        {
            if (duration is { } total && request.Start >= total)
            {
                Release(playback);
                throw new ActionException($"Le début de l'extrait ({TimeFormat.Format(request.Start.TotalSeconds)}) dépasse la durée de « {name} » ({TimeFormat.Format(total.TotalSeconds)}).");
            }
            player.Position = request.Start;
        }
        if (request.End is { } end && end > request.Start && (duration is null || end < duration))
        {
            // Une seule minuterie, déclenchée à la fin de l'extrait (pas de scrutation de la position).
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = end - request.Start };
            timer.Tick += (_, _) => Release(playback);
            playback.EndTimer = timer;
        }
        player.Play();
        playback.EndTimer?.Start();

        if (!request.WaitForEnd) return;
        try
        {
            await playback.Ended.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Release(playback);
            throw;
        }
    }

    /// <summary>Durée d'un fichier audio (null si illisible), pour aider à choisir un extrait.</summary>
    public static async Task<TimeSpan?> GetDurationAsync(string path)
    {
        var player = new MediaPlayer { Volume = 0 };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, _) => opened.TrySetResult(false);
        try
        {
            player.Open(new Uri(Path.GetFullPath(path)));
            var ok = await opened.Task.WaitAsync(OpenTimeout);
            return ok && player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            player.Close();
        }
    }

    public void Stop(object owner)
    {
        foreach (var p in _active.Where(p => ReferenceEquals(p.Owner, owner)).ToList()) Release(p);
    }

    public void StopAll()
    {
        if (_active.Count > 0) Log.Info($"Arrêt de {_active.Count} son(s).");
        foreach (var p in _active.ToList()) Release(p);
    }

    private void Release(Playback p)
    {
        if (!_active.Remove(p)) return;
        p.EndTimer?.Stop();
        try
        {
            p.Player.Stop();
            p.Player.Close();
        }
        catch (InvalidOperationException) { }
        // Une étape qui attend la fin du son continue quand il est arrêté (nouvel appui, « Arrêter les sons »…).
        p.Ended.TrySetResult();
    }
}
