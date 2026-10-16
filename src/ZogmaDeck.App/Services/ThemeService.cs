using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

/// <summary>Applique le thème Fluent (clair/sombre) et suit le thème de Windows sans interrogation périodique (WM_SETTINGCHANGE).</summary>
public sealed class ThemeService
{
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
        Changed?.Invoke(this, EventArgs.Empty);
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
