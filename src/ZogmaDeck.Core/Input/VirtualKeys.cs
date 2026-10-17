namespace ZogmaDeck.Core.Input;

/// <summary>Touche connue : nom canonique (stocké en JSON), code virtuel Windows, libellé français.</summary>
public sealed record KeyInfo(string Name, ushort Vk, string Display, bool Extended = false, bool IsMedia = false);

/// <summary>Table des touches virtuelles Windows (indépendante de WPF pour rester testable).</summary>
public static class VirtualKeys
{
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private static readonly List<KeyInfo> All = Build();
    private static readonly Dictionary<string, KeyInfo> ByName = BuildNameIndex();
    private static readonly Dictionary<ushort, KeyInfo> ByVk = All.GroupBy(k => k.Vk).ToDictionary(g => g.Key, g => g.First());

    public static IReadOnlyList<KeyInfo> Keys => All;

    /// <summary>Touches spéciales proposées dans l'éditeur (difficiles à capturer au clavier).</summary>
    public static IEnumerable<KeyInfo> SpecialKeys => All.Where(k => k.IsMedia || k.Name.StartsWith('F') && k.Name.Length > 1 && char.IsDigit(k.Name[1]) && k.Vk >= 0x7C);

    public static KeyInfo? FromName(string name) => ByName.GetValueOrDefault(name.Trim());
    public static KeyInfo? FromVk(int vk) => ByVk.GetValueOrDefault((ushort)vk);

    public static bool IsModifierVk(int vk) => vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN
        or VK_LSHIFT or VK_RSHIFT or VK_LCONTROL or VK_RCONTROL or VK_LMENU or VK_RMENU;

    private static Dictionary<string, KeyInfo> BuildNameIndex()
    {
        var d = new Dictionary<string, KeyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in All)
        {
            d.TryAdd(k.Name, k);
            d.TryAdd(k.Display, k);
        }
        // Alias courants
        (string Alias, string Name)[] aliases =
        [
            ("Escape", "Esc"), ("Return", "Enter"), ("Del", "Delete"), ("Ins", "Insert"), ("PgUp", "PageUp"), ("PgDn", "PageDown"),
            ("PageDn", "PageDown"), ("Prior", "PageUp"), ("Next", "PageDown"), ("Back", "Backspace"), ("Apps", "Menu"),
            ("Snapshot", "PrintScreen"), ("PrtSc", "PrintScreen"), ("Capital", "CapsLock"), ("Scroll", "ScrollLock"),
            ("PlayPause", "MediaPlayPause"), ("MediaPlay", "MediaPlayPause"), ("NextTrack", "MediaNext"), ("PrevTrack", "MediaPrevious"),
            ("Mute", "VolumeMute"),
        ];
        foreach (var (alias, name) in aliases) d.TryAdd(alias, d[name]);
        for (var c = '0'; c <= '9'; c++) d.TryAdd("D" + c, d[c.ToString()]);
        return d;
    }

    private static List<KeyInfo> Build()
    {
        var l = new List<KeyInfo>();
        for (var c = 'A'; c <= 'Z'; c++) l.Add(new(c.ToString(), c, c.ToString()));
        for (var c = '0'; c <= '9'; c++) l.Add(new(c.ToString(), c, c.ToString()));
        for (var i = 1; i <= 24; i++) l.Add(new($"F{i}", (ushort)(0x6F + i), $"F{i}"));
        for (var i = 0; i <= 9; i++) l.Add(new($"Num{i}", (ushort)(0x60 + i), $"Pavé {i}"));
        l.AddRange(
        [
            new("Space", 0x20, "Espace"),
            new("Enter", 0x0D, "Entrée"),
            new("Tab", 0x09, "Tab"),
            new("Esc", 0x1B, "Échap"),
            new("Backspace", 0x08, "Retour arrière"),
            new("Delete", 0x2E, "Suppr", Extended: true),
            new("Insert", 0x2D, "Inser", Extended: true),
            new("Home", 0x24, "Début", Extended: true),
            new("End", 0x23, "Fin", Extended: true),
            new("PageUp", 0x21, "Page préc.", Extended: true),
            new("PageDown", 0x22, "Page suiv.", Extended: true),
            new("Up", 0x26, "Haut", Extended: true),
            new("Down", 0x28, "Bas", Extended: true),
            new("Left", 0x25, "Gauche", Extended: true),
            new("Right", 0x27, "Droite", Extended: true),
            new("PrintScreen", 0x2C, "Impr. écran", Extended: true),
            new("Pause", 0x13, "Pause"),
            new("CapsLock", 0x14, "Verr. maj."),
            new("NumLock", 0x90, "Verr. num.", Extended: true),
            new("ScrollLock", 0x91, "Arrêt défil."),
            new("Menu", 0x5D, "Menu contextuel", Extended: true),
            new("NumMultiply", 0x6A, "Pavé *"),
            new("NumAdd", 0x6B, "Pavé +"),
            new("NumSubtract", 0x6D, "Pavé -"),
            new("NumDecimal", 0x6E, "Pavé ."),
            new("NumDivide", 0x6F, "Pavé /", Extended: true),
            new("OemSemicolon", 0xBA, "OEM ;"),
            new("OemPlus", 0xBB, "OEM ="),
            new("OemComma", 0xBC, "OEM ,"),
            new("OemMinus", 0xBD, "OEM -"),
            new("OemPeriod", 0xBE, "OEM ."),
            new("Oem2", 0xBF, "OEM 2"),
            new("Oem3", 0xC0, "OEM 3"),
            new("Oem4", 0xDB, "OEM 4"),
            new("Oem5", 0xDC, "OEM 5"),
            new("Oem6", 0xDD, "OEM 6"),
            new("Oem7", 0xDE, "OEM 7"),
            new("Oem8", 0xDF, "OEM 8"),
            new("Oem102", 0xE2, "OEM <>"),
            // Modificateurs d'un côté précis, utilisables comme touches à part entière (ex. « Ctrl droit » seul)
            new("LeftCtrl", VK_LCONTROL, "Ctrl gauche"),
            new("RightCtrl", VK_RCONTROL, "Ctrl droit", Extended: true),
            new("LeftShift", VK_LSHIFT, "Maj gauche"),
            new("RightShift", VK_RSHIFT, "Maj droite"),
            new("LeftAlt", VK_LMENU, "Alt gauche"),
            new("RightAlt", VK_RMENU, "Alt Gr", Extended: true),
            new("LeftWin", VK_LWIN, "Win gauche", Extended: true),
            new("RightWin", VK_RWIN, "Win droite", Extended: true),
            // Multimédia et système (ne nécessitent pas de fenêtre cible)
            new("MediaPlayPause", 0xB3, "Lecture/Pause", Extended: true, IsMedia: true),
            new("MediaNext", 0xB0, "Piste suivante", Extended: true, IsMedia: true),
            new("MediaPrevious", 0xB1, "Piste précédente", Extended: true, IsMedia: true),
            new("MediaStop", 0xB2, "Arrêt média", Extended: true, IsMedia: true),
            new("VolumeUp", 0xAF, "Volume +", Extended: true, IsMedia: true),
            new("VolumeDown", 0xAE, "Volume -", Extended: true, IsMedia: true),
            new("VolumeMute", 0xAD, "Muet", Extended: true, IsMedia: true),
            new("BrowserBack", 0xA6, "Navigateur : précédent", Extended: true, IsMedia: true),
            new("BrowserForward", 0xA7, "Navigateur : suivant", Extended: true, IsMedia: true),
            new("BrowserRefresh", 0xA8, "Navigateur : actualiser", Extended: true, IsMedia: true),
            new("BrowserHome", 0xAC, "Navigateur : accueil", Extended: true, IsMedia: true),
            new("LaunchMail", 0xB4, "Courrier", Extended: true, IsMedia: true),
            new("LaunchMediaSelect", 0xB5, "Lecteur multimédia", Extended: true, IsMedia: true),
            new("LaunchApp1", 0xB6, "Application 1 (Explorateur)", Extended: true, IsMedia: true),
            new("LaunchApp2", 0xB7, "Application 2 (Calculatrice)", Extended: true, IsMedia: true),
        ]);
        return l;
    }
}
