namespace ZogmaDeck.Core.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>Combinaison de touches, ex. « Ctrl+Alt+Space ». Format canonique (anglais) en stockage, libellé français à l'affichage.</summary>
public readonly record struct KeyChord(KeyModifiers Modifiers, KeyInfo Key)
{
    public bool IsMediaKey => Key.IsMedia && Modifiers == KeyModifiers.None;

    public override string ToString()
    {
        var parts = ModifierNames(Modifiers, french: false).ToList();
        parts.Add(Key.Name);
        return string.Join('+', parts);
    }

    public string ToDisplayString()
    {
        var parts = ModifierNames(Modifiers, french: true).ToList();
        parts.Add(Key.Display);
        return string.Join(" + ", parts);
    }

    private static IEnumerable<string> ModifierNames(KeyModifiers m, bool french)
    {
        if (m.HasFlag(KeyModifiers.Ctrl)) yield return "Ctrl";
        if (m.HasFlag(KeyModifiers.Alt)) yield return "Alt";
        if (m.HasFlag(KeyModifiers.Shift)) yield return french ? "Maj" : "Shift";
        if (m.HasFlag(KeyModifiers.Win)) yield return "Win";
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord, out var error) ? chord : throw new FormatException(error);

    public static bool TryParse(string? text, out KeyChord chord, out string error)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Aucune touche définie.";
            return false;
        }

        // « + » seul (touche plus) n'est pas géré : on utilise OemPlus / NumAdd.
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var mods = KeyModifiers.None;
        KeyInfo? key = null;
        foreach (var part in parts)
        {
            var mod = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "ctl" => KeyModifiers.Ctrl,
                "alt" => KeyModifiers.Alt,
                "shift" or "maj" => KeyModifiers.Shift,
                "win" or "windows" or "meta" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (mod != KeyModifiers.None)
            {
                if (key is not null)
                {
                    error = $"Le modificateur « {part} » doit précéder la touche.";
                    return false;
                }
                mods |= mod;
                continue;
            }
            if (key is not null)
            {
                error = $"Une seule touche principale est autorisée (« {key.Name} » et « {part} »).";
                return false;
            }
            key = VirtualKeys.FromName(part);
            if (key is null)
            {
                error = $"Touche inconnue : « {part} ».";
                return false;
            }
        }
        if (key is null)
        {
            error = "Il manque la touche principale (ex. Ctrl+Alt+Espace).";
            return false;
        }
        chord = new KeyChord(mods, key);
        error = "";
        return true;
    }
}
