using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Obs;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Helpers;
using ZogmaDeck.Services;

namespace ZogmaDeck.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly DeckRuntime _rt;
    private string? _hotkeyError;
    private string? _startupError;

    public SettingsViewModel(DeckRuntime runtime)
    {
        _rt = runtime;
        Themes = Enum.GetValues<ThemePreference>().Select(v => new Option(v, EnumLabels.Get(v))).ToList();
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(_rt.Paths.DataDirectory));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(_rt.Paths.LogDirectory));
        ResetHotkeyCommand = new RelayCommand(() => Hotkey = AppSettings.DefaultHotkey);
        SetAccentCommand = new RelayCommand(p => { if (p is string hex) AccentText = hex; });
        _accentText = S.AccentColor ?? "#2F6FDB";
    }

    private string _accentText;
    public ICommand SetAccentCommand { get; }
    public static IReadOnlyList<string> AccentPalette => ColorHelper.Palette;

    public bool UseWindowsAccent
    {
        get => S.AccentColor is null;
        set
        {
            if (value == UseWindowsAccent) return;
            ApplyAccent(value ? null : AccentText);
        }
    }

    public bool UseCustomAccent
    {
        get => !UseWindowsAccent;
        set => UseWindowsAccent = !value;
    }

    /// <summary>Couleur principale personnalisée (#RRGGBB) ; la modifier active le mode personnalisé.</summary>
    public string AccentText
    {
        get => _accentText;
        set
        {
            if (!Set(ref _accentText, value)) return;
            var v = (value ?? "").Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            if (v.Length == 7 && ConfigValidator.IsValidColor(v)) ApplyAccent(v.ToUpperInvariant());
        }
    }

    private void ApplyAccent(string? hex)
    {
        S.AccentColor = hex;
        _rt.Theme.ApplyAccent(hex);
        OnPropertyChanged(nameof(UseWindowsAccent));
        OnPropertyChanged(nameof(UseCustomAccent));
        _rt.NotifySettingsChanged();
    }

    private AppSettings S => _rt.Config.Settings;
    public IReadOnlyList<Option> Themes { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ResetHotkeyCommand { get; }

    public void Reload()
    {
        HotkeyError = _rt.Hotkey.IsRegistered ? null : _rt.Hotkey.LastError;
        foreach (var p in new[]
                 {
                     nameof(Hotkey), nameof(HotkeyDisplay), nameof(SelectedTheme), nameof(AlwaysOnTop), nameof(HideAfterAction),
                     nameof(HideOnDeactivate), nameof(StartWithWindows), nameof(HardwareAcceleration),
                     nameof(UseWindowsAccent), nameof(UseCustomAccent),
                 })
            OnPropertyChanged(p);
        if (S.AccentColor is { } accent && accent != _accentText)
        {
            _accentText = accent;
            OnPropertyChanged(nameof(AccentText));
        }
    }

    public string Hotkey
    {
        get => S.Hotkey;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!KeyChord.TryParse(value, out var chord, out var error))
            {
                HotkeyError = error;
                return;
            }
            var r = _rt.Hotkey.Register(chord);
            if (!r.Success)
            {
                HotkeyError = r.Error + " Choisissez une autre combinaison.";
                OnPropertyChanged();
                return;
            }
            HotkeyError = null;
            S.Hotkey = chord.ToString();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyDisplay));
            _rt.NotifySettingsChanged();
        }
    }

    public string HotkeyDisplay => KeyChord.TryParse(S.Hotkey, out var c, out _) ? c.ToDisplayString() : S.Hotkey;

    public string? HotkeyError
    {
        get => _hotkeyError;
        private set => Set(ref _hotkeyError, value);
    }

    public Option SelectedTheme
    {
        get => Themes.First(t => Equals(t.Value, S.Theme));
        set
        {
            if (value?.Value is not ThemePreference t || t == S.Theme) return;
            S.Theme = t;
            _rt.Theme.Apply(t);
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool AlwaysOnTop
    {
        get => S.AlwaysOnTop;
        set
        {
            S.AlwaysOnTop = value;
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool HideAfterAction
    {
        get => S.HideAfterAction;
        set
        {
            S.HideAfterAction = value;
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool HideOnDeactivate
    {
        get => S.HideOnDeactivate;
        set
        {
            S.HideOnDeactivate = value;
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool HardwareAcceleration
    {
        get => S.HardwareAcceleration;
        set
        {
            S.HardwareAcceleration = value;
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            StartupError = _rt.Startup.SetEnabled(value);
            S.StartWithWindows = StartupError is null ? value : _rt.Startup.IsEnabled();
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public string? StartupError
    {
        get => _startupError;
        private set => Set(ref _startupError, value);
    }

    public string DataDirectory => _rt.Paths.DataDirectory;
    public string StorageMode => _rt.Paths.IsPortable
        ? "Mode portable : les données sont à côté de l'exécutable."
        : _rt.Startup.IsPackaged
            ? "Version Microsoft Store : Windows conserve ces données dans l'espace privé de l'application (supprimées à la désinstallation)."
            : $"Pour un usage portable, créez un fichier vide « {AppPaths.PortableMarker} » à côté de ZogmaDeck.exe.";

    internal static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show("Ouverture du dossier impossible : " + ex.Message, "ZogmaDeck");
        }
    }
}

public sealed class ObsSettingsViewModel : ObservableObject
{
    private readonly DeckRuntime _rt;
    private string _portText = "";
    private string? _portError;
    private string? _testResult;
    private bool _testIsError;

    public ObsSettingsViewModel(DeckRuntime runtime)
    {
        _rt = runtime;
        TestCommand = new AsyncCommand(TestAsync, ex => ShowResult(ex.Message, true));
        DisconnectCommand = new AsyncCommand(() => _rt.Obs.DisconnectAsync());
        ClearPasswordCommand = new RelayCommand(() => SetPassword(null));
        _rt.Obs.StateChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(StateBrush));
            OnPropertyChanged(nameof(LastError));
        });
        Reload();
    }

    private ObsSettings O => _rt.Config.Settings.Obs;
    public ICommand TestCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ClearPasswordCommand { get; }

    public void Reload()
    {
        _portText = O.Port.ToString(CultureInfo.InvariantCulture);
        foreach (var p in new[] { nameof(Host), nameof(PortText), nameof(Timeout), nameof(ConnectOnStartup), nameof(PasswordStatus), nameof(HasPassword) })
            OnPropertyChanged(p);
    }

    private void EndpointChanged()
    {
        _rt.NotifySettingsChanged();
        if (_rt.Obs.State != ObsConnectionState.Disconnected) _ = _rt.Obs.DisconnectAsync();
    }

    public string Host
    {
        get => O.Host;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == O.Host) return;
            O.Host = value.Trim();
            OnPropertyChanged();
            EndpointChanged();
        }
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (!Set(ref _portText, value)) return;
            if (int.TryParse(value, out var p) && p is >= 1 and <= 65535)
            {
                PortError = null;
                if (p == O.Port) return;
                O.Port = p;
                EndpointChanged();
            }
            else PortError = "Port entre 1 et 65535 (OBS : 4455 par défaut).";
        }
    }

    public string? PortError
    {
        get => _portError;
        private set => Set(ref _portError, value);
    }

    public int Timeout
    {
        get => O.RequestTimeoutSeconds;
        set
        {
            O.RequestTimeoutSeconds = Math.Clamp(value, 1, 120);
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool ConnectOnStartup
    {
        get => O.ConnectOnStartup;
        set
        {
            O.ConnectOnStartup = value;
            OnPropertyChanged();
            _rt.NotifySettingsChanged();
        }
    }

    public bool HasPassword => _rt.Secrets.Has(SecretStore.ObsPasswordKey);

    public string PasswordStatus => HasPassword
        ? "Mot de passe enregistré (chiffré pour votre compte Windows, jamais exporté)."
        : "Aucun mot de passe enregistré.";

    public void SetPassword(string? password)
    {
        // Un copier-coller depuis OBS peut ajouter des espaces ou un retour à la ligne parasites.
        password = password?.Trim();
        try
        {
            _rt.Secrets.Set(SecretStore.ObsPasswordKey, password);
            // Jamais la valeur : uniquement le fait et la longueur, utile au diagnostic.
            Core.Diagnostics.Log.Info(string.IsNullOrEmpty(password)
                ? "Mot de passe OBS effacé."
                : $"Mot de passe OBS enregistré (chiffré, {password.Length} caractères).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                                       or PlatformNotSupportedException)
        {
            Core.Diagnostics.Log.Warn($"Enregistrement du mot de passe OBS impossible ({ex.GetType().Name}).");
            ShowResult("Enregistrement du mot de passe impossible : " + ex.Message, true);
            return;
        }
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(PasswordStatus));
        if (_rt.Obs.State != ObsConnectionState.Disconnected) _ = _rt.Obs.DisconnectAsync();
    }

    public string StateText => _rt.Obs.State switch
    {
        ObsConnectionState.Connected => $"Connecté à OBS {_rt.Obs.ObsVersion} ({_rt.Obs.EndpointDescription})",
        ObsConnectionState.Connecting => "Connexion en cours…",
        ObsConnectionState.Failed => "Échec de la connexion",
        _ => "Non connecté (connexion automatique au premier bouton OBS)",
    };

    public Brush StateBrush => _rt.Obs.State switch
    {
        ObsConnectionState.Connected => ColorHelper.Brush(Color.FromRgb(0x2E, 0xA0, 0x43)),
        ObsConnectionState.Connecting => ColorHelper.Brush(Color.FromRgb(0xD6, 0x9E, 0x2E)),
        ObsConnectionState.Failed => ColorHelper.Brush(Color.FromRgb(0xD1, 0x34, 0x38)),
        _ => ColorHelper.Brush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
    };

    public string? LastError => _rt.Obs.State == ObsConnectionState.Connected ? null : _rt.Obs.LastError;

    public string? TestResult
    {
        get => _testResult;
        private set => Set(ref _testResult, value);
    }

    public bool TestIsError
    {
        get => _testIsError;
        private set => Set(ref _testIsError, value);
    }

    private void ShowResult(string text, bool error)
    {
        TestResult = text;
        TestIsError = error;
    }

    private async Task TestAsync()
    {
        ShowResult("Connexion…", false);
        await _rt.Obs.DisconnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _rt.Obs.ConnectAsync(cts.Token);
        var scenes = await _rt.Obs.GetSceneNamesAsync(cts.Token);
        ShowResult($"Connexion réussie : OBS {_rt.Obs.ObsVersion}, {scenes.Count} scène(s).", false);
    }
}

public sealed class BackupViewModel : ObservableObject
{
    private readonly DeckRuntime _rt;
    private readonly Action _reload;
    private bool _exportAll = true;
    private bool _includeSettings = true;
    private string? _message;
    private bool _messageIsError;
    private ImportPreview? _preview;
    private bool _importReplace;
    private bool _importSettings;

    public BackupViewModel(DeckRuntime runtime, Action reload)
    {
        _rt = runtime;
        _reload = reload;
        ExportCommand = new RelayCommand(Export);
        ImportCommand = new RelayCommand(Import);
        ConfirmImportCommand = new RelayCommand(ConfirmImport, () => Preview is not null);
        CancelImportCommand = new RelayCommand(() => Preview = null);
        RestoreSnapshotCommand = new RelayCommand(p =>
        {
            if (p is SnapshotItem s) RestoreSnapshot(s);
        });
        OpenBackupsCommand = new RelayCommand(() => SettingsViewModel.OpenFolder(_rt.Store.BackupsDirectory));
        RefreshSnapshots();
    }

    public sealed record SnapshotItem(string Path, string Name, DateTime Date)
    {
        public string Display => $"{Date:dd/MM/yyyy HH:mm} — {Name}";
    }

    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ConfirmImportCommand { get; }
    public ICommand CancelImportCommand { get; }
    public ICommand RestoreSnapshotCommand { get; }
    public ICommand OpenBackupsCommand { get; }
    public ObservableCollection<SnapshotItem> Snapshots { get; } = [];

    public bool ExportAll
    {
        get => _exportAll;
        set => Set(ref _exportAll, value);
    }

    public bool ExportActive
    {
        get => !_exportAll;
        set => ExportAll = !value;
    }

    public string ActiveProfileName => _rt.Config.ActiveProfile.Name;

    public bool IncludeSettings
    {
        get => _includeSettings;
        set => Set(ref _includeSettings, value);
    }

    public string? Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public ImportPreview? Preview
    {
        get => _preview;
        private set
        {
            Set(ref _preview, value);
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(PreviewSummary));
            OnPropertyChanged(nameof(PreviewMissing));
            OnPropertyChanged(nameof(PreviewWarnings));
            OnPropertyChanged(nameof(HasMissing));
            OnPropertyChanged(nameof(PreviewHasSettings));
        }
    }

    public bool HasPreview => Preview is not null;
    public bool PreviewHasSettings => Preview?.IncludesSettings == true;
    public bool HasMissing => Preview?.Missing.Count > 0;

    public string PreviewSummary => Preview is not { } p
        ? ""
        : $"{Path.GetFileName(p.SourcePath)} : {p.Config.Profiles.Count} profil(s) — {string.Join(", ", p.Config.Profiles.Select(x => x.Name))} — " +
          $"{p.ButtonCount} bouton(s){(p.ExportedAt is { } d ? $", exporté le {d.LocalDateTime:dd/MM/yyyy HH:mm}" : "")}." +
          (p.ScriptCount > 0
              ? $" Attention : {p.ScriptCount} étape(s) exécutent un script. Importez seulement si vous faites confiance à la source ; vérifiez ces scripts avant d'utiliser les boutons."
              : "");

    public IReadOnlyList<string> PreviewMissing => Preview?.Missing
        .Select(m => $"{m.KindLabel} — {m.ProfileName} › {m.ButtonLabel} : {m.Path}").ToList() ?? [];

    public IReadOnlyList<string> PreviewWarnings => Preview?.Warnings ?? [];

    public bool ImportReplace
    {
        get => _importReplace;
        set
        {
            Set(ref _importReplace, value);
            OnPropertyChanged(nameof(ImportMerge));
        }
    }

    public bool ImportMerge
    {
        get => !_importReplace;
        set => ImportReplace = !value;
    }

    public bool ImportSettings
    {
        get => _importSettings;
        set => Set(ref _importSettings, value);
    }

    private void Say(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }

    private void Export()
    {
        var name = ExportAll ? "ZogmaDeck" : $"ZogmaDeck - {_rt.Config.ActiveProfile.Name}";
        var dlg = new SaveFileDialog
        {
            Title = "Exporter la configuration",
            Filter = "Configuration ZogmaDeck|*.zogmadeck",
            FileName = string.Concat(name.Split(Path.GetInvalidFileNameChars())) + DeckPackage.Extension,
            AddExtension = true,
            DefaultExt = DeckPackage.Extension,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var ids = ExportAll ? null : new[] { _rt.Config.ActiveProfileId };
            var report = DeckPackage.Export(_rt.Config, dlg.FileName, new ExportOptions(ids, IncludeSettings), _rt.Paths.DataDirectory);
            var text = $"Export terminé : {report.ProfileCount} profil(s), {report.ButtonCount} bouton(s), {report.BundledFiles.Count} image(s)/son(s) inclus.";
            text += "\nLe mot de passe OBS n'est jamais exporté. Les programmes et documents ouverts par les boutons ne sont pas copiés.";
            if (report.MissingFiles.Count > 0)
                text += $"\n\nFichiers introuvables, non inclus ({report.MissingFiles.Count}) :\n• " + string.Join("\n• ", report.MissingFiles);
            Say(text, report.MissingFiles.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say("Export impossible : " + ex.Message, true);
        }
    }

    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Importer une configuration",
            Filter = "Configuration ZogmaDeck|*.zogmadeck;*.pocketdeck;*.json|Tous les fichiers|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Preview = DeckPackage.ReadPackage(dlg.FileName, _rt.Paths.DataDirectory);
            ImportReplace = false;
            ImportSettings = false;
            Say("Vérifiez le contenu puis confirmez. Aucune action ne sera exécutée.", false);
        }
        catch (Exception ex) when (ex is ConfigFormatException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Preview = null;
            Say("Import impossible : " + ex.Message, true);
        }
    }

    private void ConfirmImport()
    {
        if (Preview is not { } p) return;
        _rt.Store.CreateSnapshot("avant-import");
        var oldHotkey = _rt.Config.Settings.Hotkey;
        var oldTheme = _rt.Config.Settings.Theme;
        try
        {
            var result = DeckPackage.Apply(p, _rt.Config, ImportReplace ? ImportMode.Replace : ImportMode.Merge, ImportSettings, _rt.Paths.DataDirectory);
            if (result.SettingsImported) ApplyImportedSettings(oldHotkey, oldTheme);
            _rt.NotifyChanged();
            _rt.NotifySettingsChanged();
            Preview = null;
            _reload();
            RefreshSnapshots();
            Say($"Import terminé : {result.ImportedProfiles.Count} profil(s) ({string.Join(", ", result.ImportedProfiles)}), " +
                $"{result.ButtonCount} bouton(s), {result.ExtractedFiles} fichier(s) copié(s). Une sauvegarde a été créée avant l'import.", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say("Import impossible : " + ex.Message, true);
        }
    }

    private void ApplyImportedSettings(string oldHotkey, ThemePreference oldTheme)
    {
        var s = _rt.Config.Settings;
        if (s.Hotkey != oldHotkey && KeyChord.TryParse(s.Hotkey, out var chord, out _))
        {
            var r = _rt.Hotkey.Register(chord);
            if (!r.Success)
            {
                s.Hotkey = oldHotkey;
                if (KeyChord.TryParse(oldHotkey, out var old, out _)) _rt.Hotkey.Register(old);
            }
        }
        _rt.Theme.ApplyAccent(s.AccentColor);
        if (s.Theme != oldTheme) _rt.Theme.Apply(s.Theme);
    }

    private void RestoreSnapshot(SnapshotItem s)
    {
        if (MessageBox.Show($"Restaurer la sauvegarde du {s.Date:dd/MM/yyyy HH:mm} ?\nLa configuration actuelle sera d'abord sauvegardée.",
                "ZogmaDeck", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            var preview = DeckPackage.ReadPackage(s.Path, _rt.Paths.DataDirectory);
            _rt.Store.CreateSnapshot("avant-restauration");
            var oldHotkey = _rt.Config.Settings.Hotkey;
            var oldTheme = _rt.Config.Settings.Theme;
            DeckPackage.Apply(preview, _rt.Config, ImportMode.Replace, importSettings: true, _rt.Paths.DataDirectory);
            ApplyImportedSettings(oldHotkey, oldTheme);
            _rt.NotifyChanged();
            _rt.NotifySettingsChanged();
            _reload();
            RefreshSnapshots();
            Say("Sauvegarde restaurée.", false);
        }
        catch (Exception ex) when (ex is ConfigFormatException or IOException or UnauthorizedAccessException)
        {
            Say("Restauration impossible : " + ex.Message, true);
        }
    }

    public void RefreshSnapshots()
    {
        Snapshots.Clear();
        foreach (var f in _rt.Store.ListSnapshots())
        {
            var name = Path.GetFileNameWithoutExtension(f.Name);
            var reason = name.Split('-', 3).ElementAtOrDefault(2) ?? "";
            if (reason.Length > 0 && char.IsDigit(reason[0])) reason = reason.Split('-', 2).ElementAtOrDefault(1) ?? "";
            Snapshots.Add(new SnapshotItem(f.FullName, reason.Replace('-', ' '), f.LastWriteTime));
        }
    }
}

public sealed class DiagnosticsViewModel(DeckRuntime runtime) : ObservableObject
{
    public sealed record Metric(string Name, string Value);

    public ObservableCollection<Metric> Metrics { get; } = [];
    public ICommand RefreshCommand => new RelayCommand(Refresh);
    public ICommand CopyCommand => new RelayCommand(() =>
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, Metrics.Select(m => $"{m.Name} : {m.Value}"))); }
        catch (COMException) { }
    });
    public ICommand OpenLogsCommand => new RelayCommand(() => SettingsViewModel.OpenFolder(runtime.Paths.LogDirectory));

    public static TimeSpan? StartupDuration { get; set; }

    public void Refresh()
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var gc = GC.GetGCMemoryInfo();
        Metrics.Clear();
        void Add(string n, string v) => Metrics.Add(new Metric(n, v));
        Add("Version", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");
        Add("Runtime", RuntimeInformation.FrameworkDescription);
        Add("Système", RuntimeInformation.OSDescription);
        Add("Démarrage (jusqu'à l'application prête)", StartupDuration is { } d ? $"{d.TotalMilliseconds:N0} ms" : "non mesuré");
        Add("Mémoire de travail (Working Set)", Mb(p.WorkingSet64));
        Add("Pic de mémoire de travail", Mb(p.PeakWorkingSet64));
        Add("Mémoire privée", Mb(p.PrivateMemorySize64));
        Add("Tas managé (GC)", Mb(GC.GetTotalMemory(false)));
        Add("Taille du tas après dernier GC", gc.Index == 0 ? "aucun GC effectué" : Mb(gc.HeapSizeBytes));
        Add("Threads", p.Threads.Count.ToString(CultureInfo.CurrentCulture));
        Add("Handles", p.HandleCount.ToString(CultureInfo.CurrentCulture));
        Add("Temps processeur total", $"{p.TotalProcessorTime.TotalSeconds:N2} s");
        Add("En cours depuis", (DateTime.Now - p.StartTime).ToString(@"hh\:mm\:ss"));
        Add("Sons en cours", runtime.Audio.ActiveCount.ToString(CultureInfo.CurrentCulture));
        Add("Connexion OBS", runtime.Obs.State switch
        {
            ObsConnectionState.Connected => $"connectée ({runtime.Obs.ObsVersion})",
            ObsConnectionState.Connecting => "en cours",
            ObsConnectionState.Failed => "échec : " + runtime.Obs.LastError,
            _ => "non connectée",
        });
        Add("Profils / boutons", $"{runtime.Config.Profiles.Count} / {runtime.Config.Profiles.Sum(x => x.Buttons.Count)}");
        Add("Dossier de données", runtime.Paths.DataDirectory + (runtime.Paths.IsPortable ? " (portable)" : ""));
        Add("Mesuré à", DateTime.Now.ToString("HH:mm:ss"));
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:N1} Mo";
}
