using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZogmaDeck.Controls;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Obs;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Native;
using ZogmaDeck.Services;
using ZogmaDeck.ViewModels;
using ZogmaDeck.Views;

namespace ZogmaDeck;

public partial class App : Application
{
    private SingleInstance? _instance;
    private MessageWindow? _messages;
    private HotkeyService? _hotkey;
    private FocusService? _focus;
    private TrayIcon? _tray;
    private DeckRuntime? _rt;
    private PanelWindow? _panel;
    private EditorWindow? _editor;
    private EditorViewModel? _editorVm;

    public bool IsShuttingDown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Start(e.Args);
        }
        catch (Exception ex)
        {
            // Un démarrage incomplet ne doit pas laisser une application à moitié active.
            Log.Error("Échec du démarrage", ex);
            MessageBox.Show($"ZogmaDeck n'a pas pu démarrer :\n{ex.Message}\n\nDétails dans le journal.", "ZogmaDeck",
                MessageBoxButton.OK, MessageBoxImage.Error);
            IsShuttingDown = true;
            Cleanup();
            Shutdown(1);
        }
    }

    private void Start(string[] startArgs)
    {
        var args = startArgs;
        // --tray : démarrage avec Windows de la version classique ; version Store : lancée par sa tâche de démarrage.
        var startHidden = args.Contains("--tray", StringComparer.OrdinalIgnoreCase) ||
                          (PackageInfo.IsPackaged && PackagedStartupTask.WasLaunchedByStartupTask());
        var dataIndex = Array.FindIndex(args, a => a.Equals("--data", StringComparison.OrdinalIgnoreCase));
        var dataDir = dataIndex >= 0 && dataIndex + 1 < args.Length ? args[dataIndex + 1] : null;

        var instanceKey = dataDir is null ? null : Path.GetFullPath(dataDir);
        _instance = SingleInstance.TryAcquire(instanceKey);
        if (_instance is null)
        {
            // Déjà lancé : on demande à l'instance existante d'afficher son panneau.
            SingleInstance.SignalExisting(instanceKey);
            IsShuttingDown = true;
            Shutdown();
            return;
        }

        var paths = new AppPaths(dataDir);
        Log.Initialize(paths.LogDirectory);
        if (paths.MigratedFrom is { } legacy) Log.Info($"Données reprises de l'ancien nom (PocketDeck) : {legacy}");
        Log.Info($"Démarrage de ZogmaDeck {typeof(App).Assembly.GetName().Version?.ToString(3)} — {RuntimeInformation.FrameworkDescription} — " +
                 $"données : {paths.DataDirectory}{(paths.IsPortable ? " (portable)" : "")}{(PackageInfo.IsPackaged ? " (paquet MSIX)" : "")}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Exception non gérée (fatale)", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Log.Error("Tâche en échec non observée", ev.Exception);
            ev.SetObserved();
        };

        // --- Configuration ---
        var store = new ConfigStore(paths.DataDirectory);
        var load = store.Load();
        var config = load.Config ?? SampleConfig.Create();

        // Rendu logiciel par défaut : interface légère et statique, et évite les fenêtres vides observées
        // avec le rendu Direct3D de WPF sur certaines configurations graphiques. Réactivable dans Réglages.
        if (!config.Settings.HardwareAcceleration) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // --- Services ---
        _messages = new MessageWindow();
        var theme = new ThemeService(_messages);
        theme.ApplyAccent(config.Settings.AccentColor);
        theme.Apply(config.Settings.Theme);
        _hotkey = new HotkeyService(_messages);
        _focus = new FocusService();
        var secrets = new SecretStore(paths.SecretsPath);
        DeckRuntime? rt = null;
        var obs = new ObsClient(new ClientWebSocketFactory(), () => rt!.GetObsEndpoint(), () => secrets.Get(SecretStore.ObsPasswordKey));
        rt = new DeckRuntime(config, store, paths, obs, secrets, new AudioService(), _focus, new InputService(), new ShellService(), _hotkey,
            new StartupService(paths.ExecutablePath, PackageInfo.IsPackaged), theme);
        _rt = rt;
        if (load.Config is null || load.Warnings.Count > 0) rt.SaveNow();

        // --- Zone de notification ---
        _tray = new TrayIcon(_messages, "ZogmaDeck");
        rt.Tray = _tray;
        _tray.Activated += (_, _) => _panel?.Toggle(fromHotkey: false);
        _tray.MenuRequested += (_, _) => ShowTrayMenu();

        // --- Panneau ---
        var panelVm = new PanelViewModel(rt);
        _panel = new PanelWindow(rt, panelVm);
        rt.Panel = _panel;
        _focus.HidePanel = () => _panel.HidePanel();
        panelVm.EditorRequested += (_, r) => OpenEditor(r.ButtonId, r.AddNew);

        // --- Raccourci global ---
        _hotkey.Pressed += (_, _) => _panel.Toggle(fromHotkey: true);
        KeyChordBox.CaptureStarted += (_, _) => _hotkey.Suspend();
        KeyChordBox.CaptureEnded += (_, _) => _hotkey.Resume();
        var hotkeyResult = KeyChord.TryParse(config.Settings.Hotkey, out var chord, out _)
            ? _hotkey.Register(chord)
            : new HotkeyResult(false, "Raccourci invalide.");

        // Démarrage avec Windows : remplace l'entrée de l'ancien nom et met à jour le chemin si l'exécutable a été déplacé.
        if (rt.Startup.RemoveLegacyEntry() && !config.Settings.StartWithWindows) config.Settings.StartWithWindows = true;
        // Entrée créée par l'installateur (option « Lancer ZogmaDeck à l'ouverture de session ») : le réglage la reflète.
        else if (!config.Settings.StartWithWindows && rt.Startup.IsEnabled()) config.Settings.StartWithWindows = true;
        // Démarrage refusé (désactivé par l'utilisateur dans Paramètres Windows, stratégie…) : le réglage reflète l'état réel.
        if (config.Settings.StartWithWindows && !rt.Startup.IsEnabled() && rt.Startup.SetEnabled(true) is not null)
            config.Settings.StartWithWindows = false;

        if (config.Settings.Obs.ConnectOnStartup) _ = ConnectObsOnceAsync(obs);

        // --- Mises à jour (Store ou GitHub), première vérification 90 s après le démarrage ---
        var messages = _messages;
        var updates = new UpdateService(() => rt.Config.Settings.CheckForUpdates, () => messages.Handle);
        updates.Found += (_, u) => _tray?.ShowBalloon("Mise à jour disponible",
            (u.FromStore ? "Une nouvelle version de ZogmaDeck est disponible dans le Microsoft Store." : $"ZogmaDeck {u.Version} est disponible.") +
            " Clic droit sur l'icône › Mettre à jour.");
        rt.Updates = updates;
        updates.Start();

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _panel.ShowPanel(fromHotkey: false)));
        rt.ActiveProfileChanged += (_, _) => UpdateTrayTooltip();
        rt.SettingsChanged += (_, _) => UpdateTrayTooltip();
        UpdateTrayTooltip();

        // Mesuré depuis la création du processus (inclut le chargement du runtime .NET).
        using (var self = Process.GetCurrentProcess()) DiagnosticsViewModel.StartupDuration = DateTime.Now - self.StartTime;
        Log.Info($"Prêt en {DiagnosticsViewModel.StartupDuration?.TotalMilliseconds:N0} ms.");

        // --- Messages de démarrage ---
        if (!hotkeyResult.Success)
            _tray.ShowBalloon("Raccourci indisponible", $"{hotkeyResult.Error} Choisissez-en un autre dans l'éditeur › Réglages.", BalloonKind.Warning);
        else if (load.Warnings.Count > 0)
            _tray.ShowBalloon("Configuration vérifiée", string.Join(" ", load.Warnings.Take(2)), BalloonKind.Warning);
        else if (load.IsFirstRun)
            _tray.ShowBalloon("ZogmaDeck est prêt",
                $"Appuyez sur {chord.ToDisplayString()} pour afficher le panneau. Des profils d'exemple ont été créés.");

        var editorIndex = Array.FindIndex(args, a => a.Equals("--editor", StringComparison.OrdinalIgnoreCase));
        if (editorIndex >= 0)
        {
            OpenEditor();
            if (editorIndex + 1 < args.Length && int.TryParse(args[editorIndex + 1], out var tab)) _editorVm!.SelectedTab = Math.Clamp(tab, 0, 6);
        }
        else if (!startHidden) _panel.ShowPanel(fromHotkey: false);
    }

    private static async Task ConnectObsOnceAsync(ObsClient obs)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await obs.ConnectAsync(cts.Token);
        }
        catch (Exception ex) when (ex is ObsException or OperationCanceledException)
        {
            // Pas de nouvelle tentative automatique : l'état est visible dans le panneau.
        }
    }

    private void UpdateTrayTooltip()
    {
        if (_rt is null || _tray is null) return;
        var hk = KeyChord.TryParse(_rt.Config.Settings.Hotkey, out var c, out _) ? c.ToDisplayString() : "";
        _tray.SetTooltip($"ZogmaDeck — {_rt.Config.ActiveProfile.Name}\n{hk}");
    }

    public void OpenEditor(Guid? buttonId = null, bool addNew = false, int? tab = null)
    {
        if (_rt is null) return;
        _editorVm ??= new EditorViewModel(_rt);
        _editor ??= new EditorWindow(_editorVm, _rt);
        _panel?.HidePanel();
        _editor.ShowAndFocus();
        if (buttonId is not null || addNew) _editorVm.Focus(buttonId, addNew);
        else if (tab is { } t) _editorVm.SelectedTab = t;
        if (addNew) _editor.FocusLabel();
    }

    /// <summary>Affiche le panneau en aperçu, sans fermer l'éditeur.</summary>
    public void ShowPanelPreview() => _panel?.ShowPreview();

    private void ShowTrayMenu()
    {
        if (_rt is null) return;
        var rt = _rt;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var hk = KeyChord.TryParse(rt.Config.Settings.Hotkey, out var c, out _) ? c.ToDisplayString() : "";

        if (rt.Updates?.Available is { } update)
        {
            menu.Items.Add(Item(update.MenuText, () => _ = rt.Shell.OpenAsync(update.Url, CancellationToken.None), bold: true));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Afficher le panneau", () => _panel?.ShowPanel(false), hk, bold: true));
        menu.Items.Add(Item("Éditeur…", () => OpenEditor()));
        menu.Items.Add(new Separator());

        var profiles = new MenuItem { Header = "Profil" };
        foreach (var p in rt.Config.Profiles)
        {
            var id = p.Id;
            profiles.Items.Add(new MenuItem
            {
                Header = p.Name,
                IsCheckable = true,
                IsChecked = p.Id == rt.Config.ActiveProfileId,
                Command = new RelayCommand(() => rt.SwitchTo(id)),
            });
        }
        menu.Items.Add(profiles);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Arrêter les sons", () => rt.Audio.StopAll(), enabled: rt.Audio.ActiveCount > 0));
        menu.Items.Add(Item("Annuler les actions en cours", rt.CancelAll, enabled: rt.BusyCount > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quitter ZogmaDeck", Quit));

        // Le menu doit appartenir à la fenêtre au premier plan pour se fermer correctement lors d'un clic ailleurs.
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource src) Win32.SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true, bool bold = false) => new()
    {
        Header = header,
        InputGestureText = gesture ?? "",
        IsEnabled = enabled,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Command = new RelayCommand(action),
    };

    public void Quit()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        Log.Info("Arrêt demandé.");
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        try
        {
            _rt?.Updates?.Stop();
            _rt?.CancelAll();
            _rt?.Audio.StopAll();
            _rt?.Flush();
            if (_rt is not null)
            {
                var dispose = _rt.Obs.DisposeAsync().AsTask();
                dispose.Wait(TimeSpan.FromSeconds(2));
                _rt.Discord.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Erreur pendant l'arrêt", ex);
        }
        _tray?.Dispose();
        _hotkey?.Dispose();
        _focus?.Dispose();
        _messages?.Dispose();
        _instance?.Dispose();
        _tray = null;
        _hotkey = null;
        _focus = null;
        _messages = null;
        _instance = null;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;
        Cleanup();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Une erreur ne doit jamais faire planter ZogmaDeck : on la journalise et on la signale discrètement.
        Log.Error("Exception non gérée sur le thread d'interface", e.Exception);
        _tray?.ShowBalloon("Erreur inattendue", e.Exception.Message + " (détails dans le journal)", BalloonKind.Error);
        e.Handled = true;
    }
}
