using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZogmaDeck.Core.Input;

namespace ZogmaDeck.Controls;

/// <summary>
/// Zone de capture d'une combinaison de touches. Pendant la capture, le raccourci global est suspendu
/// (événements <see cref="CaptureStarted"/> / <see cref="CaptureEnded"/>) pour pouvoir saisir n'importe quelle combinaison.
/// Tab et Maj+Tab sans autre modificateur gardent leur rôle de navigation.
/// </summary>
public sealed class KeyChordBox : TextBox
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord), typeof(string), typeof(KeyChordBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((KeyChordBox)d).UpdateText()));

    public static event EventHandler? CaptureStarted;
    public static event EventHandler? CaptureEnded;

    private bool _capturing;

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
        CaptureStarted?.Invoke(this, EventArgs.Empty);
        Text = "Appuyez sur la combinaison…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (_capturing)
        {
            _capturing = false;
            CaptureEnded?.Invoke(this, EventArgs.Empty);
        }
        UpdateText();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var mods = Keyboard.Modifiers;
        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        if (key == Key.Tab && (mods & ~ModifierKeys.Shift) == ModifierKeys.None && !win)
        {
            base.OnPreviewKeyDown(e); // navigation
            return;
        }
        e.Handled = true;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Text = Describe(mods, win) + " + …";
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        var info = VirtualKeys.FromVk(vk);
        if (info is null)
        {
            Text = "Touche non prise en charge : utilisez la liste « Touche spéciale ».";
            return;
        }
        var km = KeyModifiers.None;
        if (mods.HasFlag(ModifierKeys.Control)) km |= KeyModifiers.Ctrl;
        if (mods.HasFlag(ModifierKeys.Alt)) km |= KeyModifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Shift)) km |= KeyModifiers.Shift;
        if (win) km |= KeyModifiers.Win;

        Chord = new KeyChord(km, info).ToString();
        UpdateText();
    }

    private static string Describe(ModifierKeys mods, bool win)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Maj");
        if (win) parts.Add("Win");
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

