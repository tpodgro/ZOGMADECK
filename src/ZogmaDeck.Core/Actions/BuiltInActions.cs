using System.Text.Json;
using System.Text.Json.Nodes;
using ZogmaDeck.Core.Input;

namespace ZogmaDeck.Core.Actions;

[ActionType("launch", "Lancer une application", Category = "Système", Glyph = "\uE7AC", Order = 10,
    Description = "Démarre un programme, un script ou un raccourci, avec arguments facultatifs.")]
public sealed class LaunchAppAction : DeckAction
{
    [Field("Programme", Kind = FieldKind.FilePath, Order = 1, FileFilter = "Programmes|*.exe;*.lnk;*.bat;*.cmd;*.ps1;*.com|Tous les fichiers|*.*",
        Hint = "Chemin complet, ou nom d'un programme connu de Windows (ex. notepad).")]
    public string Path { get; set; } = "";

    [Field("Arguments", Order = 2, Hint = "Facultatif.")]
    public string? Arguments { get; set; }

    [Field("Dossier de travail", Kind = FieldKind.FolderPath, Order = 3, Hint = "Facultatif. Par défaut : le dossier du programme.")]
    public string? WorkingDirectory { get; set; }

    [Field("Fenêtre", Kind = FieldKind.Choice, Order = 4)]
    public LaunchWindowStyle WindowStyle { get; set; } = LaunchWindowStyle.Normal;

    public override string Describe() => $"Lancer {Quote(System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path)}"
        + (string.IsNullOrWhiteSpace(Arguments) ? "" : " " + Quote(Arguments, 24));

    public override IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Path)) yield return "Indiquez le programme à lancer.";
    }

    public override IEnumerable<ResourceSlot> GetResources()
    {
        yield return new ResourceSlot(ResourceKind.Program, "Programme", () => Path, v => Path = v);
        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
            yield return new ResourceSlot(ResourceKind.Document, "Dossier de travail", () => WorkingDirectory, v => WorkingDirectory = v);
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct) =>
        ctx.Shell.LaunchAsync(Path.Trim(), Arguments, WorkingDirectory, WindowStyle, ct);
}

[ActionType("open", "Ouvrir un fichier, dossier ou URL", Category = "Système", Glyph = "\uE8E5", Order = 20,
    Description = "Ouvre un document avec son application par défaut, un dossier dans l'Explorateur ou une adresse web.")]
public sealed class OpenAction : DeckAction
{
    [Field("Fichier, dossier ou URL", Kind = FieldKind.FileOrUrl, Order = 1, Placeholder = "https://… ou C:\\…")]
    public string Target { get; set; } = "";

    public override string Describe() => $"Ouvrir {Quote(Target)}";

    public override IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Target)) yield return "Indiquez le fichier, le dossier ou l'URL à ouvrir.";
    }

    public static bool LooksLikeUri(string target) =>
        Uri.TryCreate(target.Trim(), UriKind.Absolute, out var u) && !u.IsFile && u.Scheme.Length > 1;

    public override IEnumerable<ResourceSlot> GetResources()
    {
        if (!LooksLikeUri(Target))
            yield return new ResourceSlot(ResourceKind.Document, "Fichier ou dossier", () => Target, v => Target = v);
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct) => ctx.Shell.OpenAsync(Target.Trim(), ct);
}

public enum SoundRepeatMode
{
    [Label("Le rejouer depuis le début")] Restart,
    [Label("L'arrêter (appuyer à nouveau pour le rejouer)")] Stop,
    [Label("Le jouer une fois de plus, par-dessus")] Overlap,
}

[ActionType("playSound", "Jouer un son", Category = "Audio", Glyph = "\uE767", Order = 30,
    Description = "Joue un fichier audio (mp3, wav, wma, m4a…) ou un son de la bibliothèque, en entier ou un extrait.")]
public sealed class PlaySoundAction : DeckAction
{
    public const string AudioFilter = "Fichiers audio|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg|Tous les fichiers|*.*";

    [Field("Fichier audio", Kind = FieldKind.Sound, Order = 1, FileFilter = AudioFilter)]
    public string Path { get; set; } = "";

    [Field("Volume", Kind = FieldKind.Slider, Order = 2, Min = 0, Max = 100, Unit = "%")]
    public int Volume { get; set; } = 80;

    [Field("Si on appuie à nouveau pendant la lecture", Kind = FieldKind.Choice, Order = 3)]
    public SoundRepeatMode OnRepeat { get; set; } = SoundRepeatMode.Restart;

    /// <summary>Début de l'extrait en secondes (0 = début du fichier). Réglé par le curseur à deux poignées du champ « son ».</summary>
    public double StartSeconds { get; set; }

    /// <summary>Fin de l'extrait en secondes (0 = fin du fichier).</summary>
    public double EndSeconds { get; set; }

    [Field("Attendre la fin du son avant l'étape suivante", Kind = FieldKind.Toggle, Order = 6)]
    public bool WaitForEnd { get; set; }

    public override string Describe()
    {
        var segment = StartSeconds > 0 || EndSeconds > 0
            ? $", {TimeFormat.Format(StartSeconds)} → {(EndSeconds > 0 ? TimeFormat.Format(EndSeconds) : "fin")}"
            : "";
        return $"Jouer {Quote(System.IO.Path.GetFileName(Path))} ({Volume} %{segment})";
    }

    public override IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Path)) yield return "Choisissez un fichier audio.";
        if (Volume is < 0 or > 100) yield return "Le volume doit être compris entre 0 et 100.";
        if (StartSeconds < 0 || EndSeconds < 0) yield return "Les positions de l'extrait ne peuvent pas être négatives.";
        if (EndSeconds > 0 && EndSeconds <= StartSeconds) yield return "La fin de l'extrait doit être après son début.";
    }

    /// <summary>
    /// Volume du curseur (0–100 %) vers le gain appliqué. Échelle progressive (carré) : linéaire, 80 % ne baissait le son
    /// que de 2 dB et 50 % de 6 dB, presque inaudible ; ici 50 % ≈ −12 dB, ressenti comme environ deux fois moins fort.
    /// </summary>
    public static double Gain(int volume) => Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 2);

    public SoundRequest ToRequest(string dataDirectory) => new(
        Storage.ResourcePaths.Resolve(Path, dataDirectory),
        Gain(Volume),
        WaitForEnd,
        TimeSpan.FromSeconds(Math.Max(0, StartSeconds)),
        EndSeconds > StartSeconds ? TimeSpan.FromSeconds(EndSeconds) : null,
        Owner: this,
        OnRepeat);

    public override IEnumerable<ResourceSlot> GetResources()
    {
        yield return new ResourceSlot(ResourceKind.Media, "Son", () => Path, v => Path = v);
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct) => ctx.Audio.PlayAsync(ToRequest(ctx.DataDirectory), ct);
}

[ActionType("stopSounds", "Arrêter les sons", Category = "Audio", Glyph = "\uE71A", Order = 31,
    Description = "Arrête tous les sons lancés par ZogmaDeck.")]
public sealed class StopSoundsAction : DeckAction
{
    public override string Describe() => "Arrêter tous les sons de ZogmaDeck";

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        ctx.Audio.StopAll();
        return Task.CompletedTask;
    }
}

[ActionType("hotkey", "Raccourci clavier", Category = "Clavier", Glyph = "\uE765", Order = 40,
    Description = "Envoie une combinaison de touches à la fenêtre active (y compris touches multimédias).")]
public sealed class HotkeyAction : DeckAction
{
    [Field("Combinaison", Kind = FieldKind.KeyChord, Order = 1, Hint = "Cliquez dans la zone puis appuyez sur la combinaison, ou choisissez une touche spéciale.")]
    public string Keys { get; set; } = "";

    public override bool NeedsTargetFocus => !(KeyChord.TryParse(Keys, out var c, out _) && c.IsMediaKey);

    public override string Describe() =>
        KeyChord.TryParse(Keys, out var c, out _) ? $"Touches {c.ToDisplayString()}" : "Raccourci clavier (non défini)";

    public override IEnumerable<string> Validate()
    {
        if (!KeyChord.TryParse(Keys, out _, out var error)) yield return error;
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        if (!KeyChord.TryParse(Keys, out var chord, out var error))
            throw new ActionException("Raccourci invalide : " + error);
        return SendAsync(ctx, chord, ct);
    }

    private async Task SendAsync(ActionContext ctx, KeyChord chord, CancellationToken ct)
    {
        if (NeedsTargetFocus) await ctx.Focus.PrepareForInputAsync(ctx.HidePanelAfterRun, ct);
        await ctx.Input.SendChordAsync(chord, ct);
    }
}

[ActionType("typeText", "Saisir du texte", Category = "Clavier", Glyph = "\uE8D2", Order = 41,
    Description = "Tape un texte (Unicode, accents, emoji) dans la fenêtre active.")]
public sealed class TypeTextAction : DeckAction
{
    [Field("Texte", Kind = FieldKind.MultilineText, Order = 1)]
    public string Text { get; set; } = "";

    [Field("Délai entre caractères", Kind = FieldKind.Integer, Order = 2, Min = 0, Max = 1000, Unit = "ms",
        Hint = "0 = le plus rapide. Augmentez si l'application cible perd des caractères.")]
    public int DelayBetweenCharsMs { get; set; }

    public override bool NeedsTargetFocus => true;

    public override string Describe() => $"Saisir {Quote(Text)}";

    public override IEnumerable<string> Validate()
    {
        if (string.IsNullOrEmpty(Text)) yield return "Le texte à saisir est vide.";
        if (DelayBetweenCharsMs is < 0 or > 1000) yield return "Le délai doit être compris entre 0 et 1000 ms.";
    }

    public override async Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        await ctx.Focus.PrepareForInputAsync(ctx.HidePanelAfterRun, ct);
        await ctx.Input.TypeTextAsync(Text, Math.Clamp(DelayBetweenCharsMs, 0, 1000), ct);
    }
}

[ActionType("delay", "Attendre", Category = "Séquence", Glyph = "\uE916", Order = 60,
    Description = "Fait une pause avant l'étape suivante.")]
public sealed class DelayAction : DeckAction
{
    public const int MaxMs = 600_000;

    [Field("Durée", Kind = FieldKind.Integer, Order = 1, Min = 0, Max = MaxMs, Unit = "ms", Hint = "1000 ms = 1 seconde.")]
    public int DurationMs { get; set; } = 1000;

    public override string Describe() => DurationMs >= 1000
        ? $"Attendre {DurationMs / 1000.0:0.##} s"
        : $"Attendre {DurationMs} ms";

    public override IEnumerable<string> Validate()
    {
        if (DurationMs is < 0 or > MaxMs) yield return "La durée doit être comprise entre 0 et 600 000 ms (10 min).";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct) =>
        ctx.Delay.DelayAsync(TimeSpan.FromMilliseconds(Math.Clamp(DurationMs, 0, MaxMs)), ct);
}

public enum ProfileSwitchMode
{
    [Label("Profil précis")] Specific,
    [Label("Profil suivant")] Next,
    [Label("Profil précédent")] Previous,
}

[ActionType("switchProfile", "Changer de profil", Category = "Séquence", Glyph = "\uE8AB", Order = 61,
    Description = "Affiche un autre profil dans le panneau.")]
public sealed class SwitchProfileAction : DeckAction
{
    [Field("Mode", Kind = FieldKind.Choice, Order = 1)]
    public ProfileSwitchMode Mode { get; set; } = ProfileSwitchMode.Specific;

    [Field("Profil", Kind = FieldKind.Profile, Order = 2, VisibleWhen = nameof(Mode), VisibleValues = nameof(ProfileSwitchMode.Specific))]
    public Guid? ProfileId { get; set; }

    /// <summary>Nom du profil au moment de la configuration, pour l'affichage si le profil est supprimé.</summary>
    public string? ProfileName { get; set; }

    public override string Describe() => Mode switch
    {
        ProfileSwitchMode.Next => "Profil suivant",
        ProfileSwitchMode.Previous => "Profil précédent",
        _ => $"Afficher le profil {Quote(ProfileName)}",
    };

    public override IEnumerable<string> Validate()
    {
        if (Mode == ProfileSwitchMode.Specific && ProfileId is null) yield return "Choisissez le profil à afficher.";
    }

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        switch (Mode)
        {
            case ProfileSwitchMode.Next: ctx.Profiles.SwitchRelative(1); break;
            case ProfileSwitchMode.Previous: ctx.Profiles.SwitchRelative(-1); break;
            default:
                if (ProfileId is not { } id || !ctx.Profiles.SwitchTo(id))
                    throw new ActionException($"Le profil {Quote(ProfileName)} n'existe plus.");
                break;
        }
        return Task.CompletedTask;
    }
}

public enum ObsCommand
{
    [Label("Changer de scène")] SetScene,
    [Label("Démarrer l'enregistrement")] StartRecord,
    [Label("Arrêter l'enregistrement")] StopRecord,
    [Label("Basculer l'enregistrement")] ToggleRecord,
    [Label("Mettre en pause / reprendre l'enregistrement")] ToggleRecordPause,
    [Label("Démarrer le stream")] StartStream,
    [Label("Arrêter le stream")] StopStream,
    [Label("Basculer le stream")] ToggleStream,
    [Label("Couper le son d'une source")] MuteInput,
    [Label("Rétablir le son d'une source")] UnmuteInput,
    [Label("Basculer le son d'une source")] ToggleInputMute,
    [Label("Sauvegarder le replay")] SaveReplayBuffer,
    [Label("Requête personnalisée (avancé)")] Custom,
}

[ActionType("obs", "OBS Studio", Category = "OBS", Glyph = "\uE714", Order = 50,
    Description = "Pilote OBS Studio via obs-websocket (v5) : scènes, enregistrement, stream, micro…")]
public sealed class ObsAction : DeckAction
{
    [Field("Commande", Kind = FieldKind.Choice, Order = 1)]
    public ObsCommand Command { get; set; } = ObsCommand.SetScene;

    [Field("Scène", Kind = FieldKind.ObsScene, Order = 2, VisibleWhen = nameof(Command), VisibleValues = nameof(ObsCommand.SetScene))]
    public string? SceneName { get; set; }

    [Field("Source audio", Kind = FieldKind.ObsInput, Order = 3, VisibleWhen = nameof(Command),
        VisibleValues = $"{nameof(ObsCommand.MuteInput)},{nameof(ObsCommand.UnmuteInput)},{nameof(ObsCommand.ToggleInputMute)}",
        Hint = "Nom exact de la source dans OBS (ex. « Mic/Aux »).")]
    public string? InputName { get; set; }

    [Field("Type de requête", Order = 4, VisibleWhen = nameof(Command), VisibleValues = nameof(ObsCommand.Custom),
        Placeholder = "ex. SetCurrentPreviewScene", Hint = "Voir la documentation du protocole obs-websocket 5.x.")]
    public string? RequestType { get; set; }

    [Field("Données (JSON)", Kind = FieldKind.Json, Order = 5, VisibleWhen = nameof(Command), VisibleValues = nameof(ObsCommand.Custom),
        Placeholder = "{ \"sceneName\": \"Principale\" }")]
    public string? RequestData { get; set; }

    public override string Describe() => Command switch
    {
        ObsCommand.SetScene => $"OBS : scène {Quote(SceneName)}",
        ObsCommand.MuteInput => $"OBS : couper {Quote(InputName)}",
        ObsCommand.UnmuteInput => $"OBS : rétablir {Quote(InputName)}",
        ObsCommand.ToggleInputMute => $"OBS : basculer le son de {Quote(InputName)}",
        ObsCommand.Custom => $"OBS : requête {Quote(RequestType)}",
        _ => "OBS : " + EnumLabels.Get(Command).ToLowerInvariant(),
    };

    public override IEnumerable<string> Validate()
    {
        switch (Command)
        {
            case ObsCommand.SetScene when string.IsNullOrWhiteSpace(SceneName):
                yield return "Indiquez le nom de la scène.";
                break;
            case ObsCommand.MuteInput or ObsCommand.UnmuteInput or ObsCommand.ToggleInputMute when string.IsNullOrWhiteSpace(InputName):
                yield return "Indiquez le nom de la source audio.";
                break;
            case ObsCommand.Custom:
                if (string.IsNullOrWhiteSpace(RequestType)) yield return "Indiquez le type de requête.";
                if (!TryParseData(out _, out var err)) yield return err;
                break;
        }
    }

    private bool TryParseData(out JsonObject? data, out string error)
    {
        data = null;
        error = "";
        if (string.IsNullOrWhiteSpace(RequestData)) return true;
        try
        {
            if (JsonNode.Parse(RequestData) is JsonObject o)
            {
                data = o;
                return true;
            }
            error = "Les données doivent être un objet JSON ({ … }).";
        }
        catch (JsonException ex)
        {
            error = "JSON invalide : " + ex.Message;
        }
        return false;
    }

    /// <summary>Traduit la commande en requête obs-websocket (type, données).</summary>
    public (string RequestType, JsonObject? Data) BuildRequest()
    {
        foreach (var problem in Validate()) throw new ActionException(problem);
        return Command switch
        {
            ObsCommand.SetScene => ("SetCurrentProgramScene", new JsonObject { ["sceneName"] = SceneName!.Trim() }),
            ObsCommand.StartRecord => ("StartRecord", null),
            ObsCommand.StopRecord => ("StopRecord", null),
            ObsCommand.ToggleRecord => ("ToggleRecord", null),
            ObsCommand.ToggleRecordPause => ("ToggleRecordPause", null),
            ObsCommand.StartStream => ("StartStream", null),
            ObsCommand.StopStream => ("StopStream", null),
            ObsCommand.ToggleStream => ("ToggleStream", null),
            ObsCommand.MuteInput => ("SetInputMute", new JsonObject { ["inputName"] = InputName!.Trim(), ["inputMuted"] = true }),
            ObsCommand.UnmuteInput => ("SetInputMute", new JsonObject { ["inputName"] = InputName!.Trim(), ["inputMuted"] = false }),
            ObsCommand.ToggleInputMute => ("ToggleInputMute", new JsonObject { ["inputName"] = InputName!.Trim() }),
            ObsCommand.SaveReplayBuffer => ("SaveReplayBuffer", null),
            ObsCommand.Custom => (RequestType!.Trim(), TryParseData(out var d, out _) ? d : null),
            _ => throw new ActionException("Commande OBS inconnue."),
        };
    }

    public override async Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        var (type, data) = BuildRequest();
        await ctx.Obs.SendRequestAsync(type, data, ct);
    }
}
