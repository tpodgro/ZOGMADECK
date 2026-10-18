using System.IO;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Services;

/// <summary>
/// Lecture des sons (Media Foundation + waveOut, voir <see cref="MediaFoundationAudio"/>) : extraits (début/fin),
/// volume exact dès le début et modifiable en direct, comportement au nouvel appui (rejouer, arrêter, superposer) par source.
/// </summary>
public sealed class AudioService : IAudioService
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    private readonly List<Entry> _active = [];

    private sealed record Entry(MediaFoundationAudio.Playback Playback, object? Owner);

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

        var playback = new MediaFoundationAudio.Playback(Path.GetFullPath(path), request.Volume, request.Start, request.End);
        var entry = new Entry(playback, request.Owner);
        _active.Add(entry);
        _ = RemoveWhenEndedAsync(entry);

        try
        {
            await playback.Started.WaitAsync(OpenTimeout, ct);
        }
        catch (TimeoutException)
        {
            Release(entry);
            throw new ActionException($"Le fichier « {Path.GetFileName(path)} » ne s'ouvre pas (format non pris en charge ?).");
        }
        catch
        {
            Release(entry);
            throw;
        }

        if (!request.WaitForEnd) return;
        try
        {
            await playback.Ended.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Release(entry);
            throw;
        }
    }

    private async Task RemoveWhenEndedAsync(Entry entry)
    {
        try { await entry.Playback.Ended; }
        catch (ActionException) { /* signalée par PlayAsync */ }
        _active.Remove(entry);
    }

    /// <summary>Durée d'un fichier audio (null si illisible), pour régler un extrait.</summary>
    public static Task<TimeSpan?> GetDurationAsync(string path) => Task.Run(() => MediaFoundationAudio.GetDuration(Path.GetFullPath(path)));

    /// <summary>Change le volume des sons en cours d'une source (curseur déplacé pendant l'écoute ou la lecture).</summary>
    public void SetVolume(object owner, double volume)
    {
        foreach (var p in _active.Where(p => ReferenceEquals(p.Owner, owner))) p.Playback.Gain = volume;
    }

    internal double? VolumeOf(object owner) => _active.FirstOrDefault(p => ReferenceEquals(p.Owner, owner))?.Playback.Gain;

    public void Stop(object owner)
    {
        foreach (var p in _active.Where(p => ReferenceEquals(p.Owner, owner)).ToList()) Release(p);
    }

    public void StopAll()
    {
        if (_active.Count > 0) Log.Info($"Arrêt de {_active.Count} son(s).");
        foreach (var p in _active.ToList()) Release(p);
    }

    /// <summary>Arrête un son ; une étape qui attendait sa fin continue (l'arrêt termine la lecture).</summary>
    private void Release(Entry e)
    {
        if (!_active.Remove(e)) return;
        e.Playback.Stop();
    }
}
