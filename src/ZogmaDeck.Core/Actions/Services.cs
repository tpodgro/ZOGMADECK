using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Input;

namespace ZogmaDeck.Core.Actions;

/// <summary>Erreur « attendue » d'une action, avec un message en français destiné à l'utilisateur.</summary>
public class ActionException(string message, Exception? inner = null) : Exception(message, inner);

public enum LaunchWindowStyle
{
    [Label("Normale")] Normal,
    [Label("Réduite")] Minimized,
    [Label("Agrandie")] Maximized,
}

public interface IShellService
{
    Task LaunchAsync(string path, string? arguments, string? workingDirectory, LaunchWindowStyle style, CancellationToken ct);
    Task OpenAsync(string target, CancellationToken ct);
}

public interface IInputService
{
    Task SendChordAsync(KeyChord chord, CancellationToken ct);
    Task TypeTextAsync(string text, int delayBetweenCharsMs, CancellationToken ct);
}

/// <summary>
/// Lecture d'un son, en entier ou un extrait (<paramref name="Start"/> à <paramref name="End"/>). <paramref name="Owner"/>
/// identifie la source (l'action) : un nouvel appui pendant la lecture applique <paramref name="Repeat"/> aux sons
/// encore joués par la même source.
/// </summary>
public sealed record SoundRequest(
    string Path, double Volume, bool WaitForEnd, TimeSpan Start = default, TimeSpan? End = null, object? Owner = null,
    SoundRepeatMode Repeat = SoundRepeatMode.Overlap);

public interface IAudioService
{
    /// <summary>
    /// Joue un son. Si <see cref="SoundRequest.WaitForEnd"/>, la tâche se termine à la fin de la lecture (l'annulation
    /// arrête le son). En mode « arrêter », si la même source joue déjà, le son est arrêté et rien n'est rejoué.
    /// </summary>
    Task PlayAsync(SoundRequest request, CancellationToken ct);
    void StopAll();
}

public interface IObsService
{
    Task<JsonElement> SendRequestAsync(string requestType, JsonObject? requestData, CancellationToken ct);
}

public interface IProfileNavigator
{
    /// <summary>Retourne false si le profil n'existe pas.</summary>
    bool SwitchTo(Guid profileId);
    void SwitchRelative(int offset);
}

public interface IFocusService
{
    /// <summary>Rend le focus à la fenêtre active avant l'ouverture du panneau, et attend le relâchement des touches de modification.</summary>
    Task PrepareForInputAsync(bool hidePanel, CancellationToken ct);
}

public interface IDelayService
{
    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}

public sealed class TaskDelayService : IDelayService
{
    public Task DelayAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}

/// <summary>Services mis à disposition des actions. Les implémentations Windows sont fournies par l'application.</summary>
public sealed class ActionContext
{
    public required IShellService Shell { get; init; }
    public required IInputService Input { get; init; }
    public required IAudioService Audio { get; init; }
    public required IObsService Obs { get; init; }
    public required IProfileNavigator Profiles { get; init; }
    public required IFocusService Focus { get; init; }
    public IDelayService Delay { get; init; } = new TaskDelayService();
    public IScriptService Scripts { get; init; } = new ProcessScriptService();
    /// <summary>Le panneau doit-il être masqué après cette exécution ? (permet de le masquer avant une saisie clavier)</summary>
    public bool HidePanelAfterRun { get; init; }
    /// <summary>Dossier de données, pour résoudre les chemins relatifs (assets/…).</summary>
    public string DataDirectory { get; init; } = "";
}
