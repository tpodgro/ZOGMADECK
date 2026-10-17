using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Helpers;
using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

/// <summary>
/// Applique le thème Fluent (clair/sombre) et suit le thème de Windows sans interrogation périodique (WM_SETTINGCHANGE).
/// Gère aussi la couleur principale (accentuation) : celle de Windows, ou une couleur choisie.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Nuances d'accentuation auxquelles se réfèrent les pinceaux du thème Fluent de WPF.</summary>
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    /// <summary>Clés système équivalentes (même ordre), auxquelles se réfèrent aussi les ressources du thème.</summary>
    private static readonly ResourceKey[] SystemAccentKeys =
    [
        SystemColors.AccentColorKey, SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight3Key,
        SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark3Key,
    ];

    private ResourceDictionary? _tokens;

    public ThemeService(MessageWindow window) => window.AddHook(WndProc);

    public ThemePreference Preference { get; private set; } = ThemePreference.System;
    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        var dark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDarkTheme(),
        };
        var app = Application.Current;
        if (_tokens is not null && dark == IsDark && app.ThemeMode != ThemeMode.None) return;
        IsDark = dark;

        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        var tokens = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ZogmaDeck;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        if (_tokens is not null) app.Resources.MergedDictionaries.Remove(_tokens);
        app.Resources.MergedDictionaries.Add(tokens);
        _tokens = tokens;

        foreach (Window w in app.Windows) ApplyChrome(w);
        ApplyAccent(Accent);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Couleur principale choisie (#RRGGBB), ou null pour celle de Windows.</summary>
    public string? Accent { get; private set; }

    /// <summary>
    /// Couleur principale des cases, curseurs, boutons d'action… Les modèles du thème Fluent figent leurs pinceaux au
    /// chargement : on redéfinit les nuances d'accentuation au niveau de l'application puis on fait réévaluer ces pinceaux
    /// (ils restent partagés, donc tous les contrôles suivent). Null rétablit la couleur de Windows.
    /// </summary>
    public void ApplyAccent(string? hex)
    {
        Accent = ConfigValidator.IsValidColor(hex) && hex!.Length == 7 ? hex : null;
        var app = Application.Current;
        if (app is null) return;
        Color? fill = null;
        if (Accent is null)
        {
            foreach (var k in AccentKeys) app.Resources.Remove(k);
            foreach (var k in SystemAccentKeys) app.Resources.Remove(k);
        }
        else
        {
            var shades = AccentShades(ColorHelper.Parse(Accent, Colors.SteelBlue));
            for (var i = 0; i < AccentKeys.Length; i++)
            {
                app.Resources[AccentKeys[i]] = shades[i];
                app.Resources[SystemAccentKeys[i]] = shades[i];
            }
            // Remplissage d'accent utilisé par Fluent : nuance claire en thème sombre, foncée en thème clair.
            fill = IsDark ? shades[2] : shades[4];
        }

        var fluent = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("PresentationFramework.Fluent", StringComparison.OrdinalIgnoreCase) == true);
        if (fluent is null) return;
        foreach (var key in fluent.Keys.Cast<object>().ToList())
        {
            switch (fluent[key])
            {
                case SolidColorBrush b when !b.IsFrozen:
                    b.InvalidateProperty(SolidColorBrush.ColorProperty);
                    break;
                case GradientBrush g when !g.IsFrozen:
                    foreach (var stop in g.GradientStops) stop.InvalidateProperty(GradientStop.ColorProperty);
                    break;
            }
        }
        // Texte posé sur la couleur principale (coche, bouton d'action) : blanc ou noir selon le contraste.
        if (fluent["TextOnAccentFillColorPrimaryBrush"] is SolidColorBrush onAccent && !onAccent.IsFrozen && fill is { } f)
            onAccent.SetCurrentValue(SolidColorBrush.ColorProperty, ColorHelper.ContrastingForeground(f));
    }

    /// <summary>Accent, 3 nuances claires puis 3 foncées (approximation des nuances calculées par Windows).</summary>
    internal static Color[] AccentShades(Color accent)
    {
        var (h, s, l) = ToHsl(accent);
        return
        [
            accent,
            FromHsl(h, s, l + (1 - l) * 0.25),
            FromHsl(h, s, l + (1 - l) * 0.45),
            FromHsl(h, s, l + (1 - l) * 0.70),
            FromHsl(h, s, l * 0.78),
            FromHsl(h, s, l * 0.58),
            FromHsl(h, s, l * 0.38),
        ];
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d == 0) return (0, 0, l);
        var s = d / (1 - Math.Abs(2 * l - 1));
        var h = max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            w.SourceInitialized += (_, _) => Win32.ApplyWindowChrome(new WindowInteropHelper(w).Handle, IsDark);
            return;
        }
        Win32.ApplyWindowChrome(hwnd, IsDark);
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_SETTINGCHANGE && Preference == ThemePreference.System && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemePreference.System));
        }
        return IntPtr.Zero;
    }
}
