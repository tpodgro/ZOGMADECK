using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Input;

namespace ZogmaDeck.Tests;

/// <summary>Dossier temporaire supprimé à la fin du test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZogmaDeckTests", Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>Services simulés : enregistrent chaque appel dans un journal commun pour vérifier l'ordre d'exécution.</summary>
public sealed class FakeServices : IShellService, IInputService, IAudioService, IObsService, IProfileNavigator, IFocusService, IDelayService
{
    public List<string> Calls { get; } = [];
    public Func<string, Exception?> FailOn { get; set; } = _ => null;
    public HashSet<Guid> KnownProfiles { get; } = [];

    private void Record(string call)
    {
        Calls.Add(call);
        if (FailOn(call) is { } ex) throw ex;
    }

    public Task LaunchAsync(string path, string? arguments, string? workingDirectory, LaunchWindowStyle style, CancellationToken ct)
    {
        Record($"launch:{path}|{arguments}|{workingDirectory}");
        return Task.CompletedTask;
    }

    public Task OpenAsync(string target, CancellationToken ct)
    {
        Record($"open:{target}");
        return Task.CompletedTask;
    }

    public Task SendChordAsync(KeyChord chord, CancellationToken ct)
    {
        Record($"chord:{chord}");
        return Task.CompletedTask;
    }

    public Task TypeTextAsync(string text, int delayBetweenCharsMs, CancellationToken ct)
    {
        Record($"type:{text}");
        return Task.CompletedTask;
    }

    public List<SoundRequest> Sounds { get; } = [];

    public Task PlayAsync(SoundRequest request, CancellationToken ct)
    {
        Sounds.Add(request);
        Record($"play:{System.IO.Path.GetFileName(request.Path)}@{request.Volume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        return Task.CompletedTask;
    }

    public void StopAll() => Record("stopSounds");

    public Task<JsonElement> SendRequestAsync(string requestType, JsonObject? requestData, CancellationToken ct)
    {
        Record($"obs:{requestType}{(requestData is null ? "" : " " + requestData.ToJsonString())}");
        return Task.FromResult(default(JsonElement));
    }

    public bool SwitchTo(Guid profileId)
    {
        Record($"profile:{profileId}");
        return KnownProfiles.Contains(profileId);
    }

    public void SwitchRelative(int offset) => Record($"profile:{offset:+0;-0}");

    public Task PrepareForInputAsync(bool hidePanel, CancellationToken ct)
    {
        Record($"focus:hide={hidePanel}");
        return Task.CompletedTask;
    }

    public Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        Record($"delay:{duration.TotalMilliseconds}");
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ActionContext Context(bool hide = false) => new()
    {
        Shell = this,
        Input = this,
        Audio = this,
        Obs = this,
        Profiles = this,
        Focus = this,
        Delay = this,
        HidePanelAfterRun = hide,
        DataDirectory = System.IO.Path.GetTempPath(),
    };
}
