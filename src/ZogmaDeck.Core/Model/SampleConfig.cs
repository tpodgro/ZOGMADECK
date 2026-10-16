using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Storage;

namespace ZogmaDeck.Core.Model;

/// <summary>Profils d'exemple créés au premier lancement (Travail, Studio, Multimédia).</summary>
public static class SampleConfig
{
    private const string Jingle = @"%WINDIR%\Media\tada.wav";
    private const string ObsExe = @"%ProgramFiles%\obs-studio\bin\64bit\obs64.exe";
    private const string ObsDir = @"%ProgramFiles%\obs-studio\bin\64bit";

    public static DeckConfig Create()
    {
        var work = new Profile { Name = "Travail", Columns = 4 };
        var studio = new Profile { Name = "Studio", Columns = 4 };
        var media = new Profile { Name = "Multimédia", Columns = 4 };

        work.Buttons.AddRange(
        [
            Btn("Bloc-notes", "\uE70F", "#2F6FDB", new LaunchAppAction { Path = "notepad.exe" }),
            Btn("Calculatrice", "\uE8EF", "#5B5FC7", new LaunchAppAction { Path = "calc.exe" }),
            Btn("Documents", "\uE8B7", "#C27C0E", new OpenAction { Target = @"%USERPROFILE%\Documents" }),
            Btn("Wikipédia", "\uE774", "#0F7B6C", new OpenAction { Target = "https://fr.wikipedia.org" }),
            Btn("Copier", "\uE8C8", "#4A5568", new HotkeyAction { Keys = "Ctrl+C" }),
            Btn("Coller", "\uE77F", "#4A5568", new HotkeyAction { Keys = "Ctrl+V" }),
            Btn("Signature", "\uE8BD", "#8E44AD", new TypeTextAction { Text = "Bien cordialement,\nPrénom Nom" }),
            Btn("Capture", "\uE722", "#D35400", new HotkeyAction { Keys = "Win+Shift+S" }),
            Btn("Tâches", "\uE9D9", "#7F8C8D", new HotkeyAction { Keys = "Ctrl+Shift+Esc" }),
            Btn("Studio", "\uE8AB", "#1F2937", new SwitchProfileAction { ProfileId = studio.Id, ProfileName = studio.Name }),
        ]);

        studio.Buttons.AddRange(
        [
            Btn("Lancer le studio", "\uE768", "#0F7B6C",
                new LaunchAppAction { Path = ObsExe, WorkingDirectory = ObsDir },
                new DelayAction { DurationMs = 6000 },
                new ObsAction { Command = ObsCommand.SetScene, SceneName = "Scène" },
                new PlaySoundAction { Path = Jingle, Volume = 70 }),
            Btn("Scène", "\uE7F4", "#2F6FDB", new ObsAction { Command = ObsCommand.SetScene, SceneName = "Scène" }),
            Btn("Enregistrer", "\uE7C8", "#C42B1C", new ObsAction { Command = ObsCommand.ToggleRecord }),
            Btn("Pause enreg.", "\uE769", "#B7472A", new ObsAction { Command = ObsCommand.ToggleRecordPause }),
            Btn("Stream", "\uE93E", "#6B46C1", new ObsAction { Command = ObsCommand.ToggleStream }),
            Btn("Micro", "\uE720", "#2C7A7B", new ObsAction { Command = ObsCommand.ToggleInputMute, InputName = "Mic/Aux" }),
            Btn("Jingle", "\uE8D6", "#D69E2E", new PlaySoundAction { Path = Jingle, Volume = 80 }),
            Btn("Couper les sons", "\uE74F", "#4A5568", new StopSoundsAction()),
            Btn("Multimédia", "\uE8AB", "#1F2937", new SwitchProfileAction { ProfileId = media.Id, ProfileName = media.Name }),
        ]);

        media.Buttons.AddRange(
        [
            Btn("Précédent", "\uE892", "#2D3748", new HotkeyAction { Keys = "MediaPrevious" }),
            Btn("Lecture/Pause", "\uE768", "#2F6FDB", new HotkeyAction { Keys = "MediaPlayPause" }),
            Btn("Suivant", "\uE893", "#2D3748", new HotkeyAction { Keys = "MediaNext" }),
            Btn("Muet", "\uE74F", "#C42B1C", new HotkeyAction { Keys = "VolumeMute" }),
            Btn("Volume -", "\uE993", "#4A5568", new HotkeyAction { Keys = "VolumeDown" }, new HotkeyAction { Keys = "VolumeDown" }),
            Btn("Volume +", "\uE995", "#4A5568", new HotkeyAction { Keys = "VolumeUp" }, new HotkeyAction { Keys = "VolumeUp" }),
            Btn("Mélangeur", "\uE767", "#0F7B6C", new OpenAction { Target = "ms-settings:apps-volume" }),
            Btn("Travail", "\uE8AB", "#1F2937", new SwitchProfileAction { ProfileId = work.Id, ProfileName = work.Name }),
        ]);

        var config = new DeckConfig { Profiles = [work, studio, media], ActiveProfileId = work.Id };
        ConfigValidator.Normalize(config);
        return config;
    }

    public static DeckConfig CreateEmpty()
    {
        var p = new Profile { Name = "Profil" };
        return new DeckConfig { Profiles = [p], ActiveProfileId = p.Id };
    }

    private static DeckButton Btn(string label, string glyph, string color, params DeckAction[] actions) => new()
    {
        Label = label,
        Icon = ButtonIcon.FromGlyph(glyph),
        Color = color,
        Actions = [.. actions],
    };
}
