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

/// <summary>
/// Combinaison de touches, ex. « Ctrl+Alt+Space » ou « Right+Num1 » : des modificateurs puis une ou plusieurs touches,
/// appuyées dans l'ordre et relâchées dans l'ordre inverse. Format canonique (anglais) en stockage, libellé français à l'affichage.
/// </summary>
public readonly struct KeyChord : IEquatable<KeyChord>
{
    public const int MaxKeys = 6;

    private readonly KeyInfo[]? _keys;

    public KeyChord(KeyModifiers modifiers, KeyInfo key) : this(modifiers, [key]) { }

    public KeyChord(KeyModifiers modifiers, IEnumerable<KeyInfo> keys)
    {
        Modifiers = modifiers;
        _keys = keys.ToArray();
        if (_keys.Length == 0) throw new ArgumentException("Au moins une touche est nécessaire.", nameof(keys));
    }

    public KeyModifiers Modifiers { get; }

    /// <summary>Touches principales, dans l'ordre d'appui.</summary>
    public IReadOnlyList<KeyInfo> Keys => _keys ?? [];

    /// <summary>Dernière touche principale (la seule pour une combinaison classique).</summary>
    public KeyInfo Key => Keys[^1];

    /// <summary>Une seule touche principale : seule forme acceptée par Windows pour un raccourci global.</summary>
    public bool IsSingleKey => Keys.Count == 1;

    public bool IsMediaKey => Modifiers == KeyModifiers.None && Keys.Count > 0 && Keys.All(k => k.IsMedia);

    public override string ToString() =>
        string.Join('+', ModifierNames(Modifiers, french: false).Concat(Keys.Select(k => k.Name)));

    public string ToDisplayString() =>
        string.Join(" + ", ModifierNames(Modifiers, french: true).Concat(Keys.Select(k => k.Display)));

    public bool Equals(KeyChord other) => Modifiers == other.Modifiers && Keys.SequenceEqual(other.Keys);
    public override bool Equals(object? obj) => obj is KeyChord other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Modifiers, ToString());
    public static bool operator ==(KeyChord a, KeyChord b) => a.Equals(b);
    public static bool operator !=(KeyChord a, KeyChord b) => !a.Equals(b);

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
        var keys = new List<KeyInfo>();
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
                if (keys.Count > 0)
                {
                    error = $"Le modificateur « {part} » doit précéder les touches (ou utilisez « Ctrl gauche », « Maj droite »…).";
                    return false;
                }
                mods |= mod;
                continue;
            }
            var key = VirtualKeys.FromName(part);
            if (key is null)
            {
                error = $"Touche inconnue : « {part} ».";
                return false;
            }
            if (keys.Contains(key))
            {
                error = $"La touche « {key.Display} » apparaît deux fois.";
                return false;
            }
            keys.Add(key);
        }
        if (keys.Count == 0)
        {
            error = "Il manque la touche principale (ex. Ctrl+Alt+Espace).";
            return false;
        }
        if (keys.Count > MaxKeys)
        {
            error = $"Au plus {MaxKeys} touches en plus des modificateurs.";
            return false;
        }
        chord = new KeyChord(mods, keys);
        error = "";
        return true;
    }
}
