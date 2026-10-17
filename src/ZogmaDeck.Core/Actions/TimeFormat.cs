using System.Globalization;

namespace ZogmaDeck.Core.Actions;

/// <summary>Positions temporelles saisies par l'utilisateur : « 83.5 », « 1:23.5 », « 1:02:03 » (virgule acceptée).</summary>
public static class TimeFormat
{
    public static string Format(double seconds)
    {
        if (seconds <= 0) return "0:00";
        var whole = Math.Floor(seconds);
        var frac = seconds - whole;
        var fracText = frac >= 0.0005 ? frac.ToString("0.###", CultureInfo.InvariantCulture)[1..] : "";
        var ts = TimeSpan.FromSeconds(whole);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}{fracText}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}{fracText}";
    }

    /// <summary>Vide = 0. Refuse les valeurs négatives, les minutes ou secondes ≥ 60 hors premier champ, et les décimales hors secondes.</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var isLast = i == parts.Length - 1;
            if (!double.TryParse(parts[i].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return false;
            if (!isLast && v != Math.Floor(v)) return false;
            if (i > 0 && v >= 60) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}
