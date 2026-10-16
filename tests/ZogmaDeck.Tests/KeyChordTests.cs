using ZogmaDeck.Core.Input;

namespace ZogmaDeck.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", KeyModifiers.Ctrl | KeyModifiers.Alt, 0x20)]
    [InlineData("ctrl + shift + esc", KeyModifiers.Ctrl | KeyModifiers.Shift, 0x1B)]
    [InlineData("Win+Shift+S", KeyModifiers.Win | KeyModifiers.Shift, 'S')]
    [InlineData("Maj+F13", KeyModifiers.Shift, 0x7C)]
    [InlineData("Control+D5", KeyModifiers.Ctrl, '5')]
    [InlineData("MediaPlayPause", KeyModifiers.None, 0xB3)]
    [InlineData("PlayPause", KeyModifiers.None, 0xB3)]
    [InlineData("VolumeMute", KeyModifiers.None, 0xAD)]
    [InlineData("Alt+Entrée", KeyModifiers.Alt, 0x0D)]
    public void Parse_ValidChords(string text, KeyModifiers mods, int vk)
    {
        var chord = KeyChord.Parse(text);
        Assert.Equal(mods, chord.Modifiers);
        Assert.Equal(vk, chord.Key.Vk);
    }

    [Theory]
    [InlineData("", "Aucune touche")]
    [InlineData("Ctrl+Alt", "touche principale")]
    [InlineData("Ctrl+Truc", "Touche inconnue")]
    [InlineData("A+B", "Une seule touche")]
    [InlineData("A+Ctrl", "précéder")]
    public void Parse_InvalidChords_GiveFrenchErrors(string text, string expected)
    {
        Assert.False(KeyChord.TryParse(text, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public void ToString_IsCanonical_AndRoundTrips()
    {
        var chord = KeyChord.Parse("shift+ctrl+win+alt+delete");
        Assert.Equal("Ctrl+Alt+Shift+Win+Delete", chord.ToString());
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
        Assert.Equal("Ctrl + Alt + Maj + Win + Suppr", chord.ToDisplayString());
    }

    [Fact]
    public void MediaKeys_AreFlagged_AndExtended()
    {
        Assert.True(KeyChord.Parse("MediaNext").IsMediaKey);
        Assert.False(KeyChord.Parse("Ctrl+MediaNext").IsMediaKey);
        Assert.False(KeyChord.Parse("Ctrl+C").IsMediaKey);
        Assert.True(KeyChord.Parse("Left").Key.Extended);
        Assert.False(KeyChord.Parse("A").Key.Extended);
    }

    [Fact]
    public void EveryKey_HasUniqueCanonicalName_AndParsesBack()
    {
        var names = VirtualKeys.Keys.Select(k => k.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var k in VirtualKeys.Keys)
            Assert.Equal(k.Vk, KeyChord.Parse(k.Name).Key.Vk);
    }
}
