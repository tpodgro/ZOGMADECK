using System.IO.Compression;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Storage;

namespace ZogmaDeck.Tests;

public class PackageTests
{
    private static (DeckConfig Config, string Image, string Sound) BuildConfig(TempDir dir)
    {
        var image = dir.File("logo.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var sound = dir.File("jingle.wav");
        File.WriteAllBytes(sound, [1, 2, 3, 4, 5]);

        var studio = new Profile { Name = "Studio" };
        var work = new Profile { Name = "Travail" };
        work.Buttons.Add(new DeckButton
        {
            Label = "Logo",
            Icon = new ButtonIcon { Kind = IconKind.Image, ImagePath = image },
            Actions =
            [
                new PlaySoundAction { Path = sound },
                new PlaySoundAction { Path = dir.File("absent.mp3") },
                new LaunchAppAction { Path = @"C:\Programme\Inexistant\app.exe" },
                new LaunchAppAction { Path = "notepad.exe" },
                new OpenAction { Target = "https://example.org" },
                new SwitchProfileAction { ProfileId = studio.Id, ProfileName = "Studio" },
            ],
        });
        var config = new DeckConfig { Profiles = [work, studio], ActiveProfileId = work.Id };
        config.Settings.Hotkey = "Ctrl+Alt+F9";
        config.Settings.StartWithWindows = true;
        ConfigValidator.Normalize(config);
        return (config, image, sound);
    }

    [Fact]
    public void Export_BundlesMedia_ReportsMissing_AndContainsNoSecrets()
    {
        using var dir = new TempDir();
        var (config, _, _) = BuildConfig(dir);
        File.WriteAllText(dir.File("secrets.dat"), "{\"obsPassword\":\"SECRET-QUI-NE-DOIT-PAS-SORTIR\"}");
        var zipPath = dir.File("export.zogmadeck");

        var report = DeckPackage.Export(config, zipPath, new ExportOptions(null, IncludeSettings: true), dir.Path);

        Assert.Equal(2, report.ProfileCount);
        Assert.Equal(2, report.BundledFiles.Count);
        Assert.Single(report.MissingFiles);
        Assert.Contains("absent.mp3", report.MissingFiles[0]);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("manifest.json", names);
        Assert.Contains("config.json", names);
        Assert.Equal(2, names.Count(n => n.StartsWith("assets/")));
        Assert.DoesNotContain(names, n => n.Contains("secret", StringComparison.OrdinalIgnoreCase));
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            var text = r.ReadToEnd();
            Assert.DoesNotContain("SECRET-QUI-NE-DOIT-PAS-SORTIR", text);
            Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        }
        using var cfg = new StreamReader(zip.GetEntry("config.json")!.Open());
        var json = cfg.ReadToEnd();
        Assert.Contains("package:assets/", json);
        Assert.Contains("\"startWithWindows\": false", json); // propre à chaque poste, jamais exporté actif
        Assert.True(config.Settings.StartWithWindows, "la configuration source ne doit pas être modifiée");
    }

    [Fact]
    public void ExportSingleProfile_OnlyContainsIt()
    {
        using var dir = new TempDir();
        var (config, _, _) = BuildConfig(dir);
        var zipPath = dir.File("one.zogmadeck");
        DeckPackage.Export(config, zipPath, new ExportOptions([config.Profiles[1].Id], IncludeSettings: false), dir.Path);
        var preview = DeckPackage.ReadPackage(zipPath, dir.Path);
        Assert.Equal(["Studio"], preview.Config.Profiles.Select(p => p.Name));
        Assert.False(preview.IncludesSettings);
    }

    [Fact]
    public void ReadPackage_ListsMissingResources_WithoutModifyingAnything()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var (config, _, _) = BuildConfig(source);
        var zipPath = source.File("export.zogmadeck");
        DeckPackage.Export(config, zipPath, new ExportOptions(null, true), source.Path);

        var preview = DeckPackage.ReadPackage(zipPath, target.Path);

        // Programme absent et son déjà manquant à l'export ; notepad.exe (nom simple) et l'URL ne sont pas signalés.
        Assert.Equal(2, preview.Missing.Count);
        Assert.Contains(preview.Missing, m => m.Kind == ResourceKind.Program && m.Path.Contains("Inexistant"));
        Assert.Contains(preview.Missing, m => m.Kind == ResourceKind.Media && m.Path.Contains("absent.mp3"));
        Assert.False(Directory.Exists(Path.Combine(target.Path, "assets")), "la prévisualisation ne doit rien écrire");
    }

    [Fact]
    public void ApplyMerge_RenamesConflicts_RemapsIds_AndExtractsAssets()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var (config, _, _) = BuildConfig(source);
        var zipPath = source.File("export.zogmadeck");
        DeckPackage.Export(config, zipPath, new ExportOptions(null, true), source.Path);

        // Cible : contient déjà les mêmes profils (mêmes identifiants), comme après un premier import.
        var existing = ConfigSerializer.DeepClone(config);
        existing.Settings.Hotkey = "Ctrl+Alt+F1";
        var preview = DeckPackage.ReadPackage(zipPath, target.Path);
        var result = DeckPackage.Apply(preview, existing, ImportMode.Merge, importSettings: false, target.Path);

        Assert.Equal(4, existing.Profiles.Count);
        Assert.Equal(["Travail (importé)", "Studio (importé)"], result.ImportedProfiles);
        Assert.Equal(existing.Profiles.Count, existing.Profiles.Select(p => p.Id).Distinct().Count());
        Assert.Equal(existing.Profiles.SelectMany(p => p.Buttons).Count(),
            existing.Profiles.SelectMany(p => p.Buttons).Select(b => b.Id).Distinct().Count());

        var importedWork = existing.Profiles[2];
        var importedStudio = existing.Profiles[3];
        var link = importedWork.Buttons[0].Actions.OfType<SwitchProfileAction>().Single();
        Assert.Equal(importedStudio.Id, link.ProfileId); // le lien pointe vers le profil importé, pas l'original

        var imagePath = importedWork.Buttons[0].Icon.ImagePath!;
        Assert.StartsWith("assets/", imagePath);
        Assert.True(File.Exists(ResourcePaths.Resolve(imagePath, target.Path)));
        Assert.Equal(2, result.ExtractedFiles);
        Assert.False(result.SettingsImported);
        Assert.Equal("Ctrl+Alt+F1", existing.Settings.Hotkey); // réglages non importés : inchangés
    }

    [Fact]
    public void ApplyReplace_WithSettings_ReplacesProfiles_ButNeverEnablesStartup()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var (config, _, _) = BuildConfig(source);
        var zipPath = source.File("export.zogmadeck");
        DeckPackage.Export(config, zipPath, new ExportOptions(null, true), source.Path);

        var current = SampleConfig.CreateEmpty();
        var preview = DeckPackage.ReadPackage(zipPath, target.Path);
        var result = DeckPackage.Apply(preview, current, ImportMode.Replace, importSettings: true, target.Path);

        Assert.Equal(["Travail", "Studio"], current.Profiles.Select(p => p.Name));
        Assert.True(result.SettingsImported);
        Assert.Equal("Ctrl+Alt+F9", current.Settings.Hotkey);
        Assert.False(current.Settings.StartWithWindows);
        Assert.Equal(current.Profiles[0].Id, current.ActiveProfileId);
    }

    [Fact]
    public void MaliciousArchive_PathTraversalIsIgnored()
    {
        using var dir = new TempDir();
        var zipPath = dir.File("evil.zogmadeck");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("config.json").Open()))
                w.Write("""{ "schemaVersion": 1, "profiles": [ { "name": "P", "buttons": [ { "label": "B", "icon": { "kind": "Image", "imagePath": "package:assets/../../evil.png" } } ] } ] }""");
            using (var w = new StreamWriter(zip.CreateEntry("../evil.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("assets/../../evil.png").Open())) w.Write("x");
        }

        var preview = DeckPackage.ReadPackage(zipPath, dir.Path);
        Assert.Contains(preview.Warnings, w => w.Contains("inattendu"));
        Assert.Single(preview.Missing); // l'image référencée n'est pas acceptée
        var target = SampleConfig.CreateEmpty();
        DeckPackage.Apply(preview, target, ImportMode.Merge, false, dir.Path);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path)!, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path)!, "evil.png")));
        Assert.Empty(Directory.Exists(Path.Combine(dir.Path, "assets")) ? Directory.GetFiles(Path.Combine(dir.Path, "assets")) : []);
    }

    [Fact]
    public void InvalidFiles_GiveClearErrors()
    {
        using var dir = new TempDir();
        var notZip = dir.File("faux.zogmadeck");
        File.WriteAllText(notZip, "ceci n'est pas un zip");
        Assert.Throws<ConfigFormatException>(() => DeckPackage.ReadPackage(notZip, dir.Path));

        var emptyZip = dir.File("vide.zogmadeck");
        using (ZipFile.Open(emptyZip, ZipArchiveMode.Create)) { }
        var ex = Assert.Throws<ConfigFormatException>(() => DeckPackage.ReadPackage(emptyZip, dir.Path));
        Assert.Contains("config.json absent", ex.Message);
    }

    [Fact]
    public void RawConfigJson_CanBeImported()
    {
        using var dir = new TempDir();
        var path = dir.File("config.json");
        File.WriteAllText(path, ConfigSerializer.Serialize(SampleConfig.Create()));
        var preview = DeckPackage.ReadPackage(path, dir.Path);
        Assert.Equal(3, preview.Config.Profiles.Count);
    }
}
