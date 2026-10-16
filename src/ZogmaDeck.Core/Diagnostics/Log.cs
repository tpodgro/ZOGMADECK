using System.Text;

namespace ZogmaDeck.Core.Diagnostics;

/// <summary>
/// Journal local minimal (un fichier par jour, 7 jours conservés). Aucun envoi réseau.
/// Règle : ne jamais y écrire de mot de passe ni de données de requête OBS.
/// </summary>
public static class Log
{
    private static readonly Lock Sync = new();
    private static string? _directory;
    private const int KeepDays = 7;

    public static string? Directory => _directory;

    public static void Initialize(string directory)
    {
        lock (Sync)
        {
            _directory = directory;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                foreach (var f in new DirectoryInfo(directory).GetFiles("zogmadeck-*.log"))
                    if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-KeepDays)) f.Delete();
            }
            catch
            {
                // Le journal ne doit jamais empêcher l'application de fonctionner.
            }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").Append(message);
        if (ex is not null) line.AppendLine().Append(ex.ToString());
        System.Diagnostics.Debug.WriteLine(line.ToString());
        lock (Sync)
        {
            if (_directory is null) return;
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"zogmadeck-{DateTime.Now:yyyyMMdd}.log"), line.AppendLine().ToString());
            }
            catch
            {
                // Disque plein / droits : on ignore.
            }
        }
    }
}
