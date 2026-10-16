using System.Diagnostics;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

/// <summary>
/// Mémorise la dernière fenêtre active d'une autre application (via un hook d'événement Windows, sans interrogation périodique)
/// et lui rend le focus avant d'envoyer des frappes.
/// </summary>
public sealed class FocusService : IFocusService, IDisposable
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "TaskSwitcherWnd", "#32768",
    };

    private readonly Win32.WinEventDelegate _callback;
    private readonly IntPtr _hook;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private IntPtr _target;

    public FocusService()
    {
        _callback = OnForegroundChanged;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        CaptureForeground();
    }

    /// <summary>Masque le panneau (fourni par l'application).</summary>
    public Action? HidePanel { get; set; }

    public IntPtr Target => _target != IntPtr.Zero && Win32.IsWindow(_target) ? _target : IntPtr.Zero;

    public string TargetDescription => Target is var t && t != IntPtr.Zero ? Win32.GetWindowTitle(t) : "";

    /// <summary>Enregistre la fenêtre au premier plan si elle appartient à une autre application.</summary>
    public void CaptureForeground()
    {
        var fg = Win32.GetForegroundWindow();
        if (IsCandidate(fg)) _target = fg;
    }

    private void OnForegroundChanged(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject == 0 && IsCandidate(hwnd)) _target = hwnd;
    }

    private bool IsCandidate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return false;
        return !IgnoredClasses.Contains(Win32.GetClassName(hwnd));
    }

    public async Task PrepareForInputAsync(bool hidePanel, CancellationToken ct)
    {
        // 1. Attendre que l'utilisateur ait relâché Ctrl/Alt/Maj/Win (sinon ils se combineraient aux frappes envoyées).
        var sw = Stopwatch.StartNew();
        while (AnyModifierDown() && sw.ElapsedMilliseconds < 3000) await Task.Delay(20, ct);

        if (hidePanel) HidePanel?.Invoke();

        var target = Target;
        var fg = Win32.GetForegroundWindow();
        Win32.GetWindowThreadProcessId(fg, out var fgPid);
        if (target == IntPtr.Zero)
        {
            if (fgPid != _ownPid) return; // une autre application a déjà le focus : on lui envoie les frappes
            throw new ActionException("Aucune fenêtre cible : ouvrez le panneau depuis l'application qui doit recevoir les frappes.");
        }

        Win32.GetWindowThreadProcessId(target, out var targetPid);
        if (Win32.IsProcessElevated(targetPid) == true && Win32.IsProcessElevated(_ownPid) != true)
            throw new ActionException($"« {Shorten(Win32.GetWindowTitle(target))} » est exécutée en administrateur : Windows bloque les frappes simulées " +
                                      "venant d'une application non administrateur.");

        if (fg != target)
        {
            if (Win32.IsIconic(target)) Win32.ShowWindow(target, Win32.SW_RESTORE);
            Win32.SetForegroundWindow(target);
            sw.Restart();
            while (Win32.GetForegroundWindow() != target && sw.ElapsedMilliseconds < 1000)
            {
                await Task.Delay(15, ct);
                if (sw.ElapsedMilliseconds > 300) Win32.SetForegroundWindow(target);
            }
            if (Win32.GetForegroundWindow() != target)
            {
                Log.Warn("Impossible de rendre le focus à la fenêtre cible.");
                throw new ActionException($"Impossible de rendre le focus à « {Shorten(Win32.GetWindowTitle(target))} ».");
            }
            await Task.Delay(40, ct); // laisse l'application cible traiter l'activation
        }
    }

    private static bool AnyModifierDown()
    {
        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((Win32.GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    private static string Shorten(string s) => s.Length > 50 ? s[..49] + "…" : s.Length == 0 ? "fenêtre cible" : s;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
    }
}
