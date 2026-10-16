using System.Windows.Threading;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Execution;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Obs;
using ZogmaDeck.Core.Storage;

namespace ZogmaDeck.Services;

public enum RunOrigin { Panel, Editor }

public sealed class RunStateChangedEventArgs(DeckButton button, bool isRunning, SequenceResult? result, StepProgress? progress) : EventArgs
{
    public DeckButton Button { get; } = button;
    public bool IsRunning { get; } = isRunning;
    public SequenceResult? Result { get; } = result;
    public StepProgress? Progress { get; } = progress;
}

public interface IPanelHost
{
    bool IsPanelVisible { get; }
    void HidePanel();
}

/// <summary>Cœur de l'application en cours d'exécution : configuration, sauvegarde automatique, exécution et annulation des boutons.</summary>
public sealed class DeckRuntime : IProfileNavigator
{
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly DispatcherTimer _saveTimer;
    private Task _pendingWrite = Task.CompletedTask;

    public DeckRuntime(DeckConfig config, ConfigStore store, AppPaths paths, ObsClient obs, SecretStore secrets, AudioService audio,
        FocusService focus, InputService input, ShellService shell, HotkeyService hotkey, StartupService startup, ThemeService theme)
    {
        Config = config;
        Store = store;
        Paths = paths;
        Obs = obs;
        Secrets = secrets;
        Audio = audio;
        Focus = focus;
        Input = input;
        Shell = shell;
        Hotkey = hotkey;
        Startup = startup;
        Theme = theme;
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => SaveNow(), Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
    }

    public DeckConfig Config { get; }
    public ConfigStore Store { get; }
    public AppPaths Paths { get; }
    public ObsClient Obs { get; }
    public SecretStore Secrets { get; }
    public AudioService Audio { get; }
    public FocusService Focus { get; }
    public InputService Input { get; }
    public ShellService Shell { get; }
    public HotkeyService Hotkey { get; }
    public StartupService Startup { get; }
    public ThemeService Theme { get; }
    public IPanelHost? Panel { get; set; }
    public TrayIcon? Tray { get; set; }

    public string? LastSaveError { get; private set; }
    public DateTime? LastSavedAt { get; private set; }

    /// <summary>Nombre d'exécutions en cours (le panneau ne se masque pas automatiquement pendant ce temps).</summary>
    public int BusyCount => _running.Count;

    public event EventHandler? ConfigChanged;
    public event EventHandler? ActiveProfileChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler<RunStateChangedEventArgs>? RunStateChanged;
    public event EventHandler? Saved;
    public event EventHandler? LayoutChanged;

    // ---------- Configuration ----------

    /// <summary>Signale une modification : sauvegarde automatique différée (regroupe les frappes rapides).</summary>
    public void NotifyChanged(bool structural = true)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        if (structural) ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Colonnes ou taille des boutons modifiées depuis le panneau : sauvegarde et mise à jour de l'éditeur.</summary>
    public void NotifyLayoutChanged()
    {
        NotifyChanged(structural: false);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NotifySettingsChanged()
    {
        NotifyChanged(structural: false);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sérialise sur le thread d'interface (cohérence), écrit sur disque en arrière-plan.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        string json;
        try
        {
            json = ConfigSerializer.Serialize(Config);
        }
        catch (Exception ex)
        {
            Log.Error("Sérialisation de la configuration impossible", ex);
            LastSaveError = ex.Message;
            return;
        }
        var previous = _pendingWrite;
        _pendingWrite = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            Store.WriteJson(json);
        }).ContinueWith(t =>
        {
            if (t.Exception is { } ex)
            {
                LastSaveError = ex.InnerException?.Message ?? ex.Message;
                Log.Error("Écriture de la configuration impossible", ex.InnerException);
                Tray?.ShowBalloon("Sauvegarde impossible", LastSaveError, BalloonKind.Error);
            }
            else
            {
                LastSaveError = null;
                LastSavedAt = DateTime.Now;
            }
            Saved?.Invoke(this, EventArgs.Empty);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Sauvegarde synchrone (fermeture de l'application).</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled) SaveNow();
        try { _pendingWrite.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* déjà journalisé */ }
    }

    // ---------- Profils ----------

    public bool SwitchTo(Guid profileId)
    {
        if (Config.FindProfile(profileId) is null) return false;
        if (Config.ActiveProfileId == profileId) return true;
        Config.ActiveProfileId = profileId;
        NotifyChanged(structural: false);
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SwitchRelative(int offset)
    {
        var list = Config.Profiles;
        if (list.Count == 0) return;
        var i = list.FindIndex(p => p.Id == Config.ActiveProfileId);
        SwitchTo(list[((i + offset) % list.Count + list.Count) % list.Count].Id);
    }

    // ---------- Exécution ----------

    public bool IsRunning(Guid buttonId) => _running.ContainsKey(buttonId);

    public bool ShouldHideAfter(DeckButton b) => b.HideAfterRun switch
    {
        HideBehavior.Hide => true,
        HideBehavior.KeepOpen => false,
        _ => Config.Settings.HideAfterAction,
    };

    public ActionContext CreateContext(bool hidePanel) => new()
    {
        Shell = Shell,
        Input = Input,
        Audio = Audio,
        Obs = Obs,
        Profiles = this,
        Focus = Focus,
        HidePanelAfterRun = hidePanel,
        DataDirectory = Paths.DataDirectory,
    };

    /// <summary>Exécute la séquence d'un bouton. Si elle est déjà en cours, ce second appel l'annule.</summary>
    public async Task<SequenceResult?> RunAsync(DeckButton button, RunOrigin origin)
    {
        if (_running.TryGetValue(button.Id, out var existing))
        {
            existing.Cancel();
            return null;
        }
        if (button.Actions.Count == 0)
        {
            var empty = new SequenceResult(SequenceStatus.Failed, [new StepResult(0, "", StepStatus.Failed, "Ce bouton n'a aucune action.", TimeSpan.Zero)]);
            RunStateChanged?.Invoke(this, new(button, false, empty, null));
            return empty;
        }

        var hide = origin == RunOrigin.Panel && ShouldHideAfter(button);
        var cts = new CancellationTokenSource();
        _running[button.Id] = cts;
        RunStateChanged?.Invoke(this, new(button, true, null, null));
        Log.Info($"Exécution de « {button.Label} » ({button.Actions.Count} étape(s)).");

        SequenceResult result;
        try
        {
            var progress = new Progress<StepProgress>(p => RunStateChanged?.Invoke(this, new(button, true, null, p)));
            result = await SequenceRunner.RunAsync(button.Actions, CreateContext(hide), cts.Token, progress);
        }
        finally
        {
            _running.Remove(button.Id);
            cts.Dispose();
        }

        RunStateChanged?.Invoke(this, new(button, false, result, null));

        if (origin == RunOrigin.Panel)
        {
            var panelVisible = Panel?.IsPanelVisible == true;
            if (result.Status is SequenceStatus.Succeeded or SequenceStatus.CompletedWithErrors)
            {
                if (hide && panelVisible) Panel!.HidePanel();
            }
            if (result.Status is SequenceStatus.Failed or SequenceStatus.CompletedWithErrors && !(Panel?.IsPanelVisible ?? false))
                Tray?.ShowBalloon($"« {button.Label} »", result.FirstError ?? "Erreur", BalloonKind.Warning);
        }
        return result;
    }

    public void CancelAll()
    {
        foreach (var cts in _running.Values.ToList()) cts.Cancel();
    }

    // ---------- OBS ----------

    public ObsEndpoint GetObsEndpoint()
    {
        var o = Config.Settings.Obs;
        return new ObsEndpoint(o.Host, o.Port, TimeSpan.FromSeconds(o.RequestTimeoutSeconds));
    }

    public bool UsesObs => Config.Profiles.SelectMany(p => p.Buttons).SelectMany(b => b.Actions).Any(a => a is ObsAction);
}
