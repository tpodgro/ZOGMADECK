using System.Runtime.InteropServices;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Input;
using ZogmaDeck.Native;

namespace ZogmaDeck.Services;

/// <summary>Simulation clavier via SendInput. Chaque combinaison est envoyée en un seul appel (appui + relâchement), sans touche bloquée.</summary>
public sealed class InputService : IInputService
{
    private static readonly int InputSize = Marshal.SizeOf<Win32.INPUT>();

    public Task SendChordAsync(KeyChord chord, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mods = new List<(ushort Vk, bool Ext)>();
        if (chord.Modifiers.HasFlag(KeyModifiers.Ctrl)) mods.Add((VirtualKeys.VK_LCONTROL, false));
        if (chord.Modifiers.HasFlag(KeyModifiers.Alt)) mods.Add((VirtualKeys.VK_LMENU, false));
        if (chord.Modifiers.HasFlag(KeyModifiers.Shift)) mods.Add((VirtualKeys.VK_LSHIFT, false));
        if (chord.Modifiers.HasFlag(KeyModifiers.Win)) mods.Add((VirtualKeys.VK_LWIN, true));

        var inputs = new List<Win32.INPUT>();
        foreach (var (vk, ext) in mods) inputs.Add(Key(vk, ext, up: false));
        inputs.Add(Key(chord.Key.Vk, chord.Key.Extended, up: false));
        inputs.Add(Key(chord.Key.Vk, chord.Key.Extended, up: true));
        for (var i = mods.Count - 1; i >= 0; i--) inputs.Add(Key(mods[i].Vk, mods[i].Ext, up: true));
        Send(inputs.ToArray());
        return Task.CompletedTask;
    }

    public async Task TypeTextAsync(string text, int delayBetweenCharsMs, CancellationToken ct)
    {
        text = text.ReplaceLineEndings("\n");
        const int chunk = 48;
        var batch = new List<Win32.INPUT>(chunk * 2);
        for (var i = 0; i < text.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = text[i];
            switch (c)
            {
                case '\n':
                    batch.Add(Key(0x0D, false, false));
                    batch.Add(Key(0x0D, false, true));
                    break;
                case '\t':
                    batch.Add(Key(0x09, false, false));
                    batch.Add(Key(0x09, false, true));
                    break;
                default:
                    // KEYEVENTF_UNICODE : indépendant de la disposition du clavier ; les paires de substitution (emoji) passent en deux unités.
                    batch.Add(Unicode(c, up: false));
                    batch.Add(Unicode(c, up: true));
                    break;
            }

            var lowSurrogateNext = char.IsHighSurrogate(c) && i + 1 < text.Length;
            if (delayBetweenCharsMs > 0 && !lowSurrogateNext)
            {
                Send(batch.ToArray());
                batch.Clear();
                await Task.Delay(delayBetweenCharsMs, ct);
            }
            else if (batch.Count >= chunk * 2 && !lowSurrogateNext)
            {
                Send(batch.ToArray());
                batch.Clear();
                await Task.Yield(); // garde l'interface réactive et permet l'annulation sur les longs textes
            }
        }
        if (batch.Count > 0) Send(batch.ToArray());
    }

    private static void Send(Win32.INPUT[] inputs)
    {
        var sent = Win32.SendInput((uint)inputs.Length, inputs, InputSize);
        if (sent != inputs.Length)
            throw new ActionException($"Windows a bloqué la saisie simulée (erreur {Marshal.GetLastWin32Error()}). " +
                                      "Un écran sécurisé (UAC, verrouillage) est peut-être actif.");
    }

    private static Win32.INPUT Key(ushort vk, bool extended, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)Win32.MapVirtualKey(vk, Win32.MAPVK_VK_TO_VSC),
                dwFlags = (extended ? Win32.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Win32.KEYEVENTF_KEYUP : 0),
            },
        },
    };

    private static Win32.INPUT Unicode(char c, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0),
            },
        },
    };
}
