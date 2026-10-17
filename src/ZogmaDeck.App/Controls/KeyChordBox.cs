using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZogmaDeck.Core.Input;

namespace ZogmaDeck.Controls;

/// <summary>
/// Zone de capture d'une combinaison de touches. Pendant la capture, le raccourci global est suspendu
/// (événements <see cref="CaptureStarted"/> / <see cref="CaptureEnded"/>) pour pouvoir saisir n'importe quelle combinaison.
/// Toutes les touches appuyées ensemble sont enregistrées dans leur ordre d'appui (ex. Droite puis Pavé 1) ; un
/// modificateur appuyé puis relâché seul est enregistré avec son côté (ex. « Ctrl droit »).
/// Tab et Maj+Tab, en début de saisie, gardent leur rôle de navigation.
/// </summary>
public sealed class KeyChordBox : TextBox
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord), typeof(string), typeof(KeyChordBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((KeyChordBox)d).UpdateText()));

    public static event EventHandler? CaptureStarted;
    public static event EventHandler? CaptureEnded;

    private bool _capturing;
    // Geste en cours : du premier appui jusqu'au relâchement de toutes les touches.
    private readonly HashSet<Key> _down = [];
    private readonly List<KeyInfo> _keys = [];
    private readonly List<KeyInfo> _modifierKeys = [];
    private KeyModifiers _modifiers;

    public KeyChordBox()
    {
        // Reprend le style implicite (Fluent) des TextBox, qui ne s'applique pas automatiquement aux classes dérivées.
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, "Appuyez sur la combinaison de touches à enregistrer");
        UpdateText();
    }

    public string Chord
    {
        get => (string)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _capturing = true;
        ResetGesture();
        CaptureStarted?.Invoke(this, EventArgs.Empty);
        Text = "Appuyez sur la combinaison…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ResetGesture();
        if (_capturing)
        {
            _capturing = false;
            CaptureEnded?.Invoke(this, EventArgs.Empty);
        }
        UpdateText();
    }

    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = RealKey(e);
        if (key == Key.Tab && _down.Count == 0 && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None && !WinDown())
        {
            base.OnPreviewKeyDown(e); // navigation
            return;
        }
        e.Handled = true;
        if (e.IsRepeat || !_down.Add(key)) return;

        var info = VirtualKeys.FromVk(KeyInterop.VirtualKeyFromKey(key));
        if (IsModifier(key))
        {
            if (info is not null && !_modifierKeys.Contains(info)) _modifierKeys.Add(info);
            if (_keys.Count == 0) Text = DescribeModifiers(CurrentModifiers()) + " + …";
            return;
        }
        if (info is null)
        {
            Text = "Touche non prise en charge : utilisez la liste « Touche spéciale ».";
            return;
        }
        if (_keys.Count >= KeyChord.MaxKeys || _keys.Contains(info)) return;
        // Les modificateurs tenus au moment de la première touche s'appliquent à toute la combinaison.
        if (_keys.Count == 0) _modifiers = CurrentModifiers();
        _keys.Add(info);
        Chord = new KeyChord(_modifiers, _keys).ToString();
        UpdateText();
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = RealKey(e);
        if (!_down.Remove(key))
        {
            base.OnPreviewKeyUp(e);
            return;
        }
        e.Handled = true; // évite aussi l'activation du menu par Alt
        if (_down.Count > 0) return;

        // Tout est relâché : si seuls des modificateurs ont été appuyés, on enregistre ces touches elles-mêmes.
        if (_keys.Count == 0 && _modifierKeys.Count > 0)
        {
            Chord = new KeyChord(KeyModifiers.None, _modifierKeys.Take(KeyChord.MaxKeys)).ToString();
            UpdateText();
        }
        ResetGesture();
    }

    private void ResetGesture()
    {
        _down.Clear();
        _keys.Clear();
        _modifierKeys.Clear();
        _modifiers = KeyModifiers.None;
    }

    private static bool WinDown() => Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

    private static KeyModifiers CurrentModifiers()
    {
        var mods = Keyboard.Modifiers;
        var km = KeyModifiers.None;
        if (mods.HasFlag(ModifierKeys.Control)) km |= KeyModifiers.Ctrl;
        if (mods.HasFlag(ModifierKeys.Alt)) km |= KeyModifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Shift)) km |= KeyModifiers.Shift;
        if (WinDown()) km |= KeyModifiers.Win;
        return km;
    }

    private static string DescribeModifiers(KeyModifiers m)
    {
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Maj");
        if (m.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    private void UpdateText()
    {
        if (_capturing && string.IsNullOrEmpty(Chord)) return;
        Text = string.IsNullOrEmpty(Chord)
            ? "Cliquez puis appuyez sur les touches"
            : KeyChord.TryParse(Chord, out var c, out _) ? c.ToDisplayString() : Chord;
    }
}
