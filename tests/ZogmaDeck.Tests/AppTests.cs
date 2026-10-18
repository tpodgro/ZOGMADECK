using System.Xml.Linq;
using System.Windows.Media;
using ZogmaDeck.Helpers;
using ZogmaDeck.Services;

namespace ZogmaDeck.Tests;

public class SecretStoreTests
{
    [Fact]
    public void Password_IsEncryptedAtRest_AndReadable()
    {
        using var dir = new TempDir();
        var store = new SecretStore(dir.File("secrets.dat"));
        store.Set(SecretStore.ObsPasswordKey, "Mon mot de passe très secret");

        Assert.Equal("Mon mot de passe très secret", new SecretStore(dir.File("secrets.dat")).Get(SecretStore.ObsPasswordKey));
        var raw = File.ReadAllText(dir.File("secrets.dat"));
        Assert.DoesNotContain("secret", raw.Replace("obsPassword", ""), StringComparison.OrdinalIgnoreCase);

        store.Set(SecretStore.ObsPasswordKey, null);
        Assert.Null(store.Get(SecretStore.ObsPasswordKey));
    }

    [Fact]
    public void CorruptedSecrets_AreIgnoredSafely()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("secrets.dat"), "{\"obsPassword\":\"AAAA\"}");
        Assert.Null(new SecretStore(dir.File("secrets.dat")).Get(SecretStore.ObsPasswordKey));
        File.WriteAllText(dir.File("secrets.dat"), "pas du json");
        Assert.Null(new SecretStore(dir.File("secrets.dat")).Get(SecretStore.ObsPasswordKey));
    }
}

public class VisualTests
{
    [Fact]
    public void AllCatalogGlyphs_ExistInIconFont()
    {
        var face = new Typeface(new FontFamily("Segoe Fluent Icons"), default, default, default);
        if (!face.TryGetGlyphTypeface(out var glyphs))
        {
            Assert.Skip("Police Segoe Fluent Icons absente (Windows 10) : Segoe MDL2 Assets sera utilisée.");
        }
        var missing = IconCatalog.Glyphs.Where(g => !glyphs.CharacterToGlyphMap.ContainsKey(g.Glyph[0])).Select(g => g.Name).ToList();
        Assert.Empty(missing);
        Assert.Equal(IconCatalog.Glyphs.Count, IconCatalog.Glyphs.Select(g => g.Glyph).Distinct().Count());
    }

    [Fact]
    public void SampleAndActionGlyphs_ExistInIconFont()
    {
        var face = new Typeface(new FontFamily("Segoe Fluent Icons"), default, default, default);
        if (!face.TryGetGlyphTypeface(out var glyphs))
        {
            Assert.Skip("Police Segoe Fluent Icons absente.");
        }
        var used = Core.Model.SampleConfig.Create().Profiles.SelectMany(p => p.Buttons).Select(b => b.Icon.Glyph!)
            .Concat(Core.Actions.ActionRegistry.All.Select(a => a.Glyph));
        Assert.All(used, g => Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(g[0]), $"U+{(int)g[0]:X4} absent"));
    }

    [Theory]
    [InlineData("#FFFFFF", false)]
    [InlineData("#F2D16B", false)]
    [InlineData("#1F2937", true)]
    [InlineData("#2F6FDB", true)]
    public void Foreground_IsReadableOnBackground(string bg, bool expectWhite)
    {
        var fg = ColorHelper.ContrastingForeground(ColorHelper.Parse(bg, Colors.Black));
        Assert.Equal(expectWhite, fg == Colors.White);
    }

    [Fact]
    public void ImageImport_CopiesIntoAssets_WithContentHashName()
    {
        using var dir = new TempDir();
        var src = dir.File("icone.PNG");
        File.WriteAllBytes(src, [1, 2, 3]);
        var rel = ImageLoader.ImportToAssets(src, Path.Combine(dir.Path, "assets"));
        Assert.StartsWith("assets/", rel);
        Assert.EndsWith(".png", rel);
        Assert.Equal(rel, ImageLoader.ImportToAssets(src, Path.Combine(dir.Path, "assets"))); // dédupliqué
    }
}

public class HotkeyServiceTests
{
    /// <summary>Exécute du code Win32/WPF sur un thread STA et propage ses exceptions (y compris Assert.Skip).</summary>
    private static void RunSta(Action body)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        error?.Throw();
    }

    [Fact]
    public void Conflict_IsDetected_AndSuspendResumeKeepsRegistration()
    {
        RunSta(() =>
        {
            using var w1 = new MessageWindow();
            using var w2 = new MessageWindow();
            using var h1 = new HotkeyService(w1);
            using var h2 = new HotkeyService(w2);
            var a = Core.Input.KeyChord.Parse("Ctrl+Alt+Shift+F24");
            var b = Core.Input.KeyChord.Parse("Ctrl+Alt+Shift+F23");

            if (!h1.Register(a).Success) Assert.Skip("Combinaison de test déjà utilisée sur ce poste.");

            // Une seconde inscription de la même combinaison est refusée avec un message clair.
            var conflict = h2.Register(a);
            Assert.False(conflict.Success);
            Assert.Contains("déjà utilisé", conflict.Error);
            Assert.False(h2.Probe(a).Success);

            // Pendant la capture d'un nouveau raccourci, l'ancien est libéré…
            h1.Suspend();
            Assert.True(h2.Probe(a).Success);
            // …et le nouveau, choisi pendant la suspension, devient actif à la reprise.
            Assert.True(h1.Register(b).Success);
            h1.Resume();
            Assert.True(h1.IsRegistered);
            Assert.False(h2.Probe(b).Success);
            Assert.True(h2.Probe(a).Success);
        });
    }
}

public class LegacyMigrationTests
{
    [Fact]
    public void PocketDeckData_IsCopied_WithoutLogs_AndNeverOverwritten()
    {
        using var dir = new TempDir();
        var legacy = Path.Combine(dir.Path, "PocketDeck");
        var target = Path.Combine(dir.Path, "ZogmaDeck");
        Directory.CreateDirectory(Path.Combine(legacy, "assets"));
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        File.WriteAllText(Path.Combine(legacy, "config.json"), "{\"schemaVersion\":2}");
        File.WriteAllText(Path.Combine(legacy, "secrets.dat"), "{}");
        File.WriteAllText(Path.Combine(legacy, "assets", "icone.png"), "png");
        File.WriteAllText(Path.Combine(legacy, "logs", "pocketdeck-20261016.log"), "journal");

        Assert.Equal(legacy, AppPaths.MigrateLegacyData(legacy, target));
        Assert.True(File.Exists(Path.Combine(target, "config.json")));
        Assert.True(File.Exists(Path.Combine(target, "secrets.dat")));
        Assert.True(File.Exists(Path.Combine(target, "assets", "icone.png")));
        Assert.False(Directory.Exists(Path.Combine(target, "logs")), "les journaux ne sont pas repris");
        Assert.True(File.Exists(Path.Combine(legacy, "config.json")), "l'ancien dossier n'est pas modifié");

        // Une fois ZogmaDeck configuré, plus aucune reprise (ses données ne sont jamais écrasées).
        File.WriteAllText(Path.Combine(target, "config.json"), "{\"schemaVersion\":2,\"nouveau\":true}");
        Assert.Null(AppPaths.MigrateLegacyData(legacy, target));
        Assert.Contains("nouveau", File.ReadAllText(Path.Combine(target, "config.json")));
    }

    [Fact]
    public void NoLegacyFolder_NothingHappens()
    {
        using var dir = new TempDir();
        Assert.Null(AppPaths.MigrateLegacyData(Path.Combine(dir.Path, "absent"), Path.Combine(dir.Path, "ZogmaDeck")));
    }
}

public class MsixPackageTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    private static XDocument LoadManifest([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
        return XDocument.Load(Path.Combine(root, "packaging", "msix", "AppxManifest.xml"));
    }

    [Fact]
    public void Manifest_MatchesStoreIdentity_AndStartupTaskId()
    {
        var doc = LoadManifest();
        var identity = doc.Root!.Element(Foundation + "Identity")!;
        Assert.Equal("TOMAZKI.ZogmaStreamDeck", identity.Attribute("Name")!.Value);
        Assert.Equal("CN=683599A3-0189-4C97-92C8-4F14962DC73A", identity.Attribute("Publisher")!.Value);
        Assert.Equal("TOMAZKI", doc.Root.Element(Foundation + "Properties")!.Element(Foundation + "PublisherDisplayName")!.Value);
        // Nom réservé dans Partner Center (« ZogmaDeck » était pris) : le paquet est refusé s'il ne correspond pas.
        Assert.Equal("Zogma Deck", doc.Root.Element(Foundation + "Properties")!.Element(Foundation + "DisplayName")!.Value);
        Assert.All(doc.Descendants().Where(e => e.Name.LocalName == "VisualElements"),
            v => Assert.Equal("Zogma Deck", v.Attribute("DisplayName")!.Value));

        // La tâche de démarrage appelée par le code est celle du manifeste, déclarée sur l'application unique :
        // Windows ne la rend visible qu'à l'application qui la déclare (celle dont l'éditeur modifie le réglage).
        var task = doc.Descendants(Desktop + "StartupTask").Single();
        Assert.Equal(PackagedStartupTask.TaskId, task.Attribute("TaskId")!.Value);
        Assert.Equal("false", task.Attribute("Enabled")!.Value);
        var app = Assert.Single(doc.Descendants(Foundation + "Application"));
        Assert.Same(app, task.Ancestors(Foundation + "Application").Single());
    }

    [Fact]
    public void NotPackaged_WhenRunFromTests()
    {
        Assert.False(PackageInfo.IsPackaged);
        Assert.False(new StartupService(@"C:\x\ZogmaDeck.exe").IsPackaged);
    }
}

public class AppearanceTests
{
    [Theory]
    [InlineData("#2F6FDB")]
    [InlineData("#C2185B")]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#7F7F7F")]
    public void ColorPicker_HsvRoundTrip(string hex)
    {
        var c = ColorHelper.Parse(hex, Colors.Red);
        var (h, s, v) = Controls.ColorPicker.ToHsv(c);
        Assert.Equal(hex, Controls.ColorPicker.ToHex(Controls.ColorPicker.FromHsv(h, s, v)));
    }

    [Fact]
    public void AccentShades_GoFromLightToDark()
    {
        var shades = ThemeService.AccentShades(Color.FromRgb(0x00, 0x78, 0xD4));
        static double L(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 2.0;
        Assert.Equal(7, shades.Length);
        Assert.True(L(shades[1]) > L(shades[0]) && L(shades[2]) > L(shades[1]) && L(shades[3]) > L(shades[2]));
        Assert.True(L(shades[4]) < L(shades[0]) && L(shades[5]) < L(shades[4]) && L(shades[6]) < L(shades[5]));
    }

    [Theory]
    [InlineData("Scène 1", "S1")]
    [InlineData("Bloc-notes", "Bl")]
    [InlineData("Démarrer le stream", "DLS")]
    [InlineData("", "A")]
    public void TextIcon_DefaultFromLabel(string label, string expected) =>
        Assert.Equal(expected, ViewModels.ButtonEditViewModel.DefaultIconText(label));

    [Fact]
    public void InvalidAccent_IsResetByValidator()
    {
        var config = new Core.Model.DeckConfig { Settings = { AccentColor = "rouge" }, Profiles = [new Core.Model.Profile()] };
        Core.Storage.ConfigValidator.Normalize(config);
        Assert.Null(config.Settings.AccentColor);
    }
}

public class UpdateTests
{
    private static string Release(string tag, bool pre = false) =>
        $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/tpodgro/ZOGMADECK/releases/tag/{{tag}}","draft":false,"prerelease":{{(pre ? "true" : "false")}}}""";

    [Theory]
    [InlineData("v1.4.1", "1.4.1")]
    [InlineData("1.5.0", "1.5.0")]
    [InlineData("v2.0", "2.0.0")]
    public void NewerRelease_IsReported(string tag, string expected)
    {
        var u = UpdateService.ParseRelease(Release(tag), new Version(1, 4, 0));
        Assert.NotNull(u);
        Assert.Equal(expected, u.Version);
        Assert.False(u.FromStore);
        Assert.StartsWith("https://github.com/tpodgro/ZOGMADECK/releases/", u.Url);
    }

    [Theory]
    [InlineData("v1.4.0", false)]
    [InlineData("v1.3.0", false)]
    [InlineData("v9.0.0", true)]
    [InlineData("nouvelle-version", false)]
    public void SameOlderPrereleaseOrInvalid_IsIgnored(string tag, bool prerelease) =>
        Assert.Null(UpdateService.ParseRelease(Release(tag, prerelease), new Version(1, 4, 0)));

    [Fact]
    public void ForeignUrl_IsReplacedByReleasesPage()
    {
        var u = UpdateService.ParseRelease("""{"tag_name":"v3.0.0","html_url":"https://exemple.com/piege"}""", new Version(1, 4, 0));
        Assert.Equal(UpdateService.ReleasesPage, u!.Url);
    }
}

public class EditorColorTests
{
    [Theory]
    [InlineData("#1B2430", true)]
    [InlineData("#202020", true)]
    [InlineData("#EEF2F8", false)]
    [InlineData("#FFFFFF", false)]
    public void Background_DecidesLightOrDarkText(string hex, bool dark) =>
        Assert.Equal(dark, ThemeService.IsDarkBackground(ColorHelper.Parse(hex, Colors.Red)));

    [Fact]
    public void SurfaceShades_StayCloseToBackground_WithVisibleBorders()
    {
        static double L(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        var dark = ThemeService.SurfaceShades(Color.FromRgb(0x1B, 0x24, 0x30), dark: true);
        Assert.Equal(Color.FromRgb(0x1B, 0x24, 0x30), dark[0]);
        Assert.True(L(dark[2]) > L(dark[0]) && L(dark[2]) - L(dark[0]) < 0.1); // cartes un peu plus claires
        Assert.True(L(dark[5]) > L(dark[4]) && L(dark[4]) > L(dark[2]));        // bordures plus marquées

        var light = ThemeService.SurfaceShades(Color.FromRgb(0xEE, 0xF2, 0xF8), dark: false);
        Assert.True(L(light[2]) >= L(light[0]));                                // cartes plus claires que le fond
        Assert.True(L(light[5]) < L(light[4]) && L(light[4]) < L(light[0]));    // bordures plus foncées
    }

    [Fact]
    public void InvalidEditorBackground_IsReset()
    {
        var config = new Core.Model.DeckConfig { Settings = { EditorBackground = "bleu" }, Profiles = [new Core.Model.Profile()] };
        Core.Storage.ConfigValidator.Normalize(config);
        Assert.Null(config.Settings.EditorBackground);
    }
}
