using System.Runtime.InteropServices;
using ZogmaDeck.Core.Diagnostics;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

public sealed record HotkeyResult(bool Success, string? Error);

/// <summary>Raccourci clavier global (RegisterHotKey). Ne nécessite aucun hook clavier ni droit administrateur.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int MainId = 0x5044;
    private const int ProbeId = 0x5045;
    private readonly MessageWindow _window;
    private KeyChord? _registered;
    private int _suspendCount;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public KeyChord? Current { get; private set; }
    public bool IsRegistered => _registered is not null;
    public string? LastError { get; private set; }

    /// <summary>Remplace le raccourci global. En cas d'échec, l'ancien raccourci reste actif.</summary>
    public HotkeyResult Register(KeyChord chord)
    {
        if (_suspendCount > 0)
        {
            // Suspendu (capture en cours) : on vérifie seulement la disponibilité ; l'enregistrement réel se fera à la reprise.
            var probe = Probe(chord);
            if (!probe.Success)
            {
                LastError = probe.Error;
                return probe;
            }
            _registered = chord;
            Current = chord;
            LastError = null;
            Log.Info($"Raccourci global défini (actif à la fin de la saisie) : {chord}");
            return new(true, null);
        }

        var previous = _registered;
        UnregisterInternal();
        if (TryRegister(MainId, chord, out var error))
        {
            _registered = chord;
            Current = chord;
            LastError = null;
            Log.Info($"Raccourci global enregistré : {chord}");
            return new(true, null);
        }

        if (previous is { } p && TryRegister(MainId, p, out _)) _registered = p;
        else Current = chord; // mémorisé pour réessayer plus tard
        LastError = error;
        Log.Warn($"Raccourci global {chord} indisponible : {error}");
        return new(false, error);
    }

    /// <summary>Vérifie qu'une combinaison est libre sans la conserver.</summary>
    public HotkeyResult Probe(KeyChord chord)
    {
        if (_suspendCount == 0 && _registered is { } r && r.Equals(chord)) return new(true, null);
        if (!TryRegister(ProbeId, chord, out var error)) return new(false, error);
        Win32.UnregisterHotKey(_window.Handle, ProbeId);
        return new(true, null);
    }

    /// <summary>Désactive temporairement le raccourci (ex. pendant la saisie d'un nouveau raccourci).</summary>
    public void Suspend()
    {
        if (_suspendCount++ == 0 && _registered is not null)
            Win32.UnregisterHotKey(_window.Handle, MainId);
    }

    public void Resume()
    {
        if (_suspendCount == 0 || --_suspendCount > 0) return;
        Win32.UnregisterHotKey(_window.Handle, MainId); // sans effet s'il n'est pas enregistré
        if (_registered is { } r && !TryRegister(MainId, r, out var error))
        {
            _registered = null;
            LastError = error;
        }
    }

    private bool TryRegister(int id, KeyChord chord, out string error)
    {
        if (!chord.IsSingleKey)
        {
            error = $"Windows n'accepte qu'une seule touche principale pour le raccourci d'affichage (avec Ctrl, Alt, Maj ou Win) : {chord.ToDisplayString()} n'est pas possible.";
            return false;
        }
        uint mods = Win32.MOD_NOREPEAT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Ctrl)) mods |= Win32.MOD_CONTROL;
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= Win32.MOD_ALT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= Win32.MOD_SHIFT;
        if (chord.Modifiers.HasFlag(KeyModifiers.Win)) mods |= Win32.MOD_WIN;

        if (Win32.RegisterHotKey(_window.Handle, id, mods, chord.Key.Vk))
        {
            error = "";
            return true;
        }
        var code = Marshal.GetLastWin32Error();
        error = code == Win32.ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"Le raccourci {chord.ToDisplayString()} est déjà utilisé par Windows ou une autre application."
            : $"Impossible d'enregistrer le raccourci {chord.ToDisplayString()} (erreur Windows {code}).";
        return false;
    }

    private void UnregisterInternal()
    {
        if (_registered is null) return;
        Win32.UnregisterHotKey(_window.Handle, MainId);
        _registered = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == MainId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose() => UnregisterInternal();
}
