using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;

namespace ZogmaDeck.Services;

/// <summary>Lancement de programmes et ouverture de fichiers/URL via le shell Windows, sur un thread STA dédié pour ne jamais bloquer l'interface.</summary>
public sealed class ShellService : IShellService
{
    public Task LaunchAsync(string path, string? arguments, string? workingDirectory, LaunchWindowStyle style, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exe = Expand(path);
        if (exe.Length == 0) throw new ActionException("Aucun programme n'est défini.");
        var qualified = Path.IsPathFullyQualified(exe);
        if (qualified && !File.Exists(exe) && !Directory.Exists(exe))
            throw new ActionException($"Programme introuvable : {exe}");

        var wd = Expand(workingDirectory);
        if (wd.Length > 0 && !Directory.Exists(wd)) throw new ActionException($"Dossier de travail introuvable : {wd}");
        // Par défaut, le dossier du programme (indispensable pour certaines applications comme OBS).
        if (wd.Length == 0 && qualified && File.Exists(exe)) wd = Path.GetDirectoryName(exe) ?? "";

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = arguments ?? "",
            UseShellExecute = true,
            WorkingDirectory = wd,
            WindowStyle = style switch
            {
                LaunchWindowStyle.Minimized => ProcessWindowStyle.Minimized,
                LaunchWindowStyle.Maximized => ProcessWindowStyle.Maximized,
                _ => ProcessWindowStyle.Normal,
            },
        };
        Log.Info($"Lancement : {Path.GetFileName(exe)}");
        return RunShell(psi, exe);
    }

    public Task OpenAsync(string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var t = Expand(target);
        if (t.Length == 0) throw new ActionException("Aucune cible n'est définie.");
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.IsFile) t = uri.LocalPath;
        else if (OpenAction.LooksLikeUri(t)) return RunShell(new ProcessStartInfo(t) { UseShellExecute = true }, t);

        if (!File.Exists(t) && !Directory.Exists(t)) throw new ActionException($"Fichier ou dossier introuvable : {t}");
        return RunShell(new ProcessStartInfo(t) { UseShellExecute = true }, t);
    }

    private static string Expand(string? s) => string.IsNullOrWhiteSpace(s) ? "" : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));

    private static Task RunShell(ProcessStartInfo psi, string display)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var _ = Process.Start(psi);
                tcs.TrySetResult();
            }
            catch (Win32Exception ex)
            {
                tcs.TrySetException(new ActionException(ex.NativeErrorCode switch
                {
                    2 or 3 => $"Introuvable : {display}",
                    5 => $"Accès refusé : {display}",
                    1155 => $"Aucune application n'est associée à ce type de fichier : {display}",
                    1223 => "Lancement annulé (demande d'autorisation refusée).",
                    740 => $"Ce programme exige des droits administrateur : {display}",
                    _ => $"Impossible d'ouvrir « {display} » : {ex.Message}",
                }, ex));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new ActionException($"Impossible d'ouvrir « {display} » : {ex.Message}", ex));
            }
        })
        {
            IsBackground = true,
            Name = "ZogmaDeck.Shell",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
