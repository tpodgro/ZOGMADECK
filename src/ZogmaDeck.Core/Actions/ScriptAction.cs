using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ZogmaDeck.Core.Actions;

public enum ScriptLanguage
{
    [Label("Invite de commandes (cmd)")] Cmd,
    [Label("Windows PowerShell")] PowerShell,
    [Label("PowerShell 7 (pwsh)")] Pwsh,
    [Label("Python")] Python,
    [Label("Node.js (JavaScript)")] Node,
    [Label("Bash (WSL)")] Bash,
    [Label("Fichier de script existant")] File,
}

public enum ScriptWindow
{
    [Label("Masquée")] Hidden,
    [Label("Visible (console)")] Visible,
}

public sealed record ScriptRequest(
    ScriptLanguage Language, string? Code, string? FilePath, string? Arguments, string? WorkingDirectory,
    bool Hidden, bool Wait, TimeSpan Timeout, bool FailOnError);

public interface IScriptService
{
    Task RunAsync(ScriptRequest request, CancellationToken ct);
}

[ActionType("script", "Exécuter un script", Category = "Système", Glyph = "\uE756", Order = 12,
    Description = "Exécute un script cmd, PowerShell, Python, Node.js ou Bash (WSL), écrit ici ou dans un fichier.")]
public sealed class ScriptAction : DeckAction
{
    [Field("Langage", Kind = FieldKind.Choice, Order = 1)]
    public ScriptLanguage Language { get; set; } = ScriptLanguage.PowerShell;

    [Field("Script", Kind = FieldKind.Code, Order = 2, VisibleWhen = nameof(Language),
        VisibleValues = "Cmd,PowerShell,Pwsh,Python,Node,Bash")]
    public string? Code { get; set; }

    [Field("Fichier de script", Kind = FieldKind.FilePath, Order = 3, VisibleWhen = nameof(Language), VisibleValues = nameof(ScriptLanguage.File),
        FileFilter = "Scripts|*.ps1;*.bat;*.cmd;*.py;*.pyw;*.js;*.mjs;*.sh;*.vbs|Tous les fichiers|*.*",
        Hint = "L'interpréteur est choisi selon l'extension (.ps1, .bat/.cmd, .py, .js, .sh, .vbs…).")]
    public string? ScriptPath { get; set; }

    [Field("Arguments", Order = 4, Hint = "Facultatif. Transmis au script.")]
    public string? Arguments { get; set; }

    [Field("Dossier de travail", Kind = FieldKind.FolderPath, Order = 5, Hint = "Facultatif. Par défaut : le dossier de l'utilisateur.")]
    public string? WorkingDirectory { get; set; }

    [Field("Fenêtre", Kind = FieldKind.Choice, Order = 6)]
    public ScriptWindow Window { get; set; } = ScriptWindow.Hidden;

    [Field("Attendre la fin du script avant l'étape suivante", Kind = FieldKind.Toggle, Order = 7)]
    public bool WaitForExit { get; set; } = true;

    [Field("Durée maximale", Kind = FieldKind.Integer, Order = 8, Min = 1, Max = 3600, Unit = "s", VisibleWhen = nameof(WaitForExit), VisibleValues = "True",
        Hint = "Au-delà, le script est arrêté et l'étape est en erreur.")]
    public int TimeoutSeconds { get; set; } = 60;

    [Field("Erreur si le script se termine avec un code différent de 0", Kind = FieldKind.Toggle, Order = 9, VisibleWhen = nameof(WaitForExit), VisibleValues = "True")]
    public bool FailOnNonZeroExit { get; set; } = true;

    public override string Describe() => Language == ScriptLanguage.File
        ? $"Script {Quote(System.IO.Path.GetFileName(ScriptPath ?? ""))}"
        : $"Script {EnumLabels.Get(Language)} : {Quote(FirstLine(Code), 32)}";

    private static string FirstLine(string? code) =>
        (code ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    public override IEnumerable<string> Validate()
    {
        if (Language == ScriptLanguage.File)
        {
            if (string.IsNullOrWhiteSpace(ScriptPath)) yield return "Choisissez le fichier de script.";
        }
        else if (string.IsNullOrWhiteSpace(Code)) yield return "Le script est vide.";
        if (TimeoutSeconds is < 1 or > 3600) yield return "La durée maximale doit être comprise entre 1 et 3600 secondes.";
    }

    public override IEnumerable<ResourceSlot> GetResources()
    {
        if (Language == ScriptLanguage.File && !string.IsNullOrWhiteSpace(ScriptPath))
            yield return new ResourceSlot(ResourceKind.Document, "Script", () => ScriptPath, v => ScriptPath = v);
    }

    public ScriptRequest ToRequest(string dataDirectory) => new(
        Language, Code,
        string.IsNullOrWhiteSpace(ScriptPath) ? null : Storage.ResourcePaths.Resolve(ScriptPath.Trim(), dataDirectory),
        Arguments, WorkingDirectory, Window == ScriptWindow.Hidden, WaitForExit,
        TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 3600)), FailOnNonZeroExit);

    public override Task ExecuteAsync(ActionContext ctx, CancellationToken ct)
    {
        foreach (var problem in Validate()) throw new ActionException(problem);
        return ctx.Scripts.RunAsync(ToRequest(ctx.DataDirectory), ct);
    }
}

/// <summary>
/// Exécute les scripts dans un processus séparé. Le code saisi est écrit dans un fichier temporaire (encodage adapté à
/// l'interpréteur), supprimé après l'exécution. L'annulation et le dépassement de durée arrêtent toute l'arborescence.
/// </summary>
public sealed class ProcessScriptService : IScriptService
{
    private const int MaxCapturedChars = 4000;

    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "ZogmaDeck", "scripts");

    public async Task RunAsync(ScriptRequest r, CancellationToken ct)
    {
        string? tempFile = null;
        string scriptFile;
        if (r.Language == ScriptLanguage.File)
        {
            scriptFile = r.FilePath ?? throw new ActionException("Aucun fichier de script n'est défini.");
            if (!File.Exists(scriptFile)) throw new ActionException($"Script introuvable : {scriptFile}");
        }
        else
        {
            tempFile = WriteTempScript(r.Language, r.Code ?? "");
            scriptFile = tempFile;
        }

        var psi = BuildStartInfo(r.Language == ScriptLanguage.File ? LanguageFromExtension(scriptFile) : r.Language, scriptFile, r.Arguments);
        psi.WorkingDirectory = !string.IsNullOrWhiteSpace(r.WorkingDirectory) && Directory.Exists(r.WorkingDirectory)
            ? r.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = r.Hidden;
        var capture = r.Hidden && r.Wait;
        if (capture)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new ActionException("Le script n'a pas pu démarrer.");
        }
        catch (Win32Exception ex)
        {
            DeleteQuietly(tempFile);
            throw new ActionException($"Interpréteur introuvable ({psi.FileName}) : {ex.Message}. Installez-le ou vérifiez le PATH.", ex);
        }

        if (!r.Wait)
        {
            // Sans attente : le fichier temporaire est supprimé à la fin du processus.
            if (tempFile is not null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => { DeleteQuietly(tempFile); process.Dispose(); };
                if (process.HasExited) DeleteQuietly(tempFile);
            }
            else process.Dispose();
            return;
        }

        var stderr = new StringBuilder();
        var stdout = new StringBuilder();
        if (capture)
        {
            process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
            process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }

        using var timeout = new CancellationTokenSource(r.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (capture) process.WaitForExit(); // vide les flux redirigés
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            process.Dispose();
            DeleteQuietly(tempFile);
            if (ct.IsCancellationRequested) throw;
            throw new ActionException($"Le script a dépassé la durée maximale ({r.Timeout.TotalSeconds:0} s) et a été arrêté.");
        }

        var code = process.ExitCode;
        process.Dispose();
        DeleteQuietly(tempFile);
        if (code != 0 && r.FailOnError)
        {
            var detail = LastLine(stderr) ?? LastLine(stdout);
            throw new ActionException($"Le script s'est terminé avec le code {code}" + (detail is null ? "." : $" : {detail}"));
        }
    }

    private static void Append(StringBuilder sb, string? line)
    {
        if (line is null) return;
        lock (sb)
        {
            sb.AppendLine(line);
            if (sb.Length > MaxCapturedChars) sb.Remove(0, sb.Length - MaxCapturedChars);
        }
    }

    private static string? LastLine(StringBuilder sb)
    {
        lock (sb)
        {
            var line = sb.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return line is null ? null : line.Length > 300 ? line[..300] + "…" : line;
        }
    }

    internal static string WriteTempScript(ScriptLanguage language, string code)
    {
        Directory.CreateDirectory(TempDirectory);
        var (ext, encoding, newline) = language switch
        {
            // cmd lit le fichier dans la page de codes de la console : on passe en UTF-8 dès la première ligne.
            ScriptLanguage.Cmd => (".cmd", (Encoding)new UTF8Encoding(false), "\r\n"),
            // Windows PowerShell 5.1 a besoin du BOM pour lire l'UTF-8 (accents).
            ScriptLanguage.PowerShell or ScriptLanguage.Pwsh => (".ps1", new UTF8Encoding(true), "\r\n"),
            ScriptLanguage.Python => (".py", new UTF8Encoding(false), "\n"),
            ScriptLanguage.Node => (".js", new UTF8Encoding(false), "\n"),
            ScriptLanguage.Bash => (".sh", new UTF8Encoding(false), "\n"),
            _ => throw new ActionException("Langage de script inconnu."),
        };
        var body = code.ReplaceLineEndings(newline);
        if (language == ScriptLanguage.Cmd) body = "@chcp 65001 >nul" + newline + body;
        var path = Path.Combine(TempDirectory, $"{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, body, encoding);
        return path;
    }

    internal static ScriptLanguage LanguageFromExtension(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".bat" or ".cmd" => ScriptLanguage.Cmd,
        ".ps1" => ScriptLanguage.PowerShell,
        ".py" or ".pyw" => ScriptLanguage.Python,
        ".js" or ".mjs" or ".cjs" => ScriptLanguage.Node,
        ".sh" => ScriptLanguage.Bash,
        _ => ScriptLanguage.File, // autre (ex. .vbs) : programme associé par Windows
    };

    internal static ProcessStartInfo BuildStartInfo(ScriptLanguage language, string file, string? arguments)
    {
        var psi = new ProcessStartInfo();
        var args = string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim();
        switch (language)
        {
            case ScriptLanguage.Cmd:
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = $"/d /c \"\"{file}\"{args}\"";
                break;
            case ScriptLanguage.PowerShell:
                psi.FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"{args}";
                break;
            case ScriptLanguage.Pwsh:
                psi.FileName = FindOnPath("pwsh.exe") ?? "pwsh.exe";
                psi.Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"{args}";
                break;
            case ScriptLanguage.Python:
                // Lanceur « py » de python.org en priorité ; l'alias « python » du Microsoft Store ouvre le Store s'il n'est pas installé.
                var py = FindOnPath("py.exe");
                psi.FileName = py ?? FindOnPath("python.exe", skipStoreAlias: true) ?? "python.exe";
                psi.Arguments = (py is null ? "" : "-3 ") + $"\"{file}\"{args}";
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                break;
            case ScriptLanguage.Node:
                psi.FileName = FindOnPath("node.exe") ?? "node.exe";
                psi.Arguments = $"\"{file}\"{args}";
                break;
            case ScriptLanguage.Bash:
                psi.FileName = Path.Combine(Environment.SystemDirectory, "wsl.exe");
                psi.Arguments = $"-e bash \"{ToWslPath(file)}\"{args}";
                break;
            default:
                // Fichier d'un autre type : programme associé (ex. wscript pour .vbs), via cmd « start » pour rester sans shell COM.
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = $"/d /c \"\"{file}\"{args}\"";
                break;
        }
        return psi;
    }

    internal static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        return full.Length >= 2 && full[1] == ':'
            ? "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/')
            : full.Replace('\\', '/');
    }

    internal static string? FindOnPath(string exe, bool skipStoreAlias = false)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), exe);
                if (!File.Exists(candidate)) continue;
                if (skipStoreAlias && candidate.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) continue;
                return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static void KillQuietly(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
