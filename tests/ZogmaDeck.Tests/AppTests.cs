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
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";

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
        Assert.Equal("TOMAZKI.ZogmaDeck", identity.Attribute("Name")!.Value);
        Assert.Equal("CN=683599A3-0189-4C97-92C8-4F14962DC73A", identity.Attribute("Publisher")!.Value);
        Assert.Equal("TOMAZKI", doc.Root.Element(Foundation + "Properties")!.Element(Foundation + "PublisherDisplayName")!.Value);

        // La tâche de démarrage appelée par le code est celle du manifeste, et elle lance l'application en mode discret.
        var task = doc.Descendants(Desktop + "StartupTask").Single();
        Assert.Equal(PackagedStartupTask.TaskId, task.Attribute("TaskId")!.Value);
        var trayApp = task.Ancestors(Foundation + "Application").Single();
        Assert.Equal("--tray", trayApp.Attribute(Uap10 + "Parameters")!.Value);
    }

    [Fact]
    public void NotPackaged_WhenRunFromTests()
    {
        Assert.False(PackageInfo.IsPackaged);
        Assert.False(new StartupService(@"C:\x\ZogmaDeck.exe").IsPackaged);
    }
}
