using System.IO.Compression;
using System.Windows;
using System.Windows.Media.Imaging;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Storage;
using ZogmaDeck.Helpers;
using ZogmaDeck.ViewModels;

namespace ZogmaDeck.Tests;

public class AppearanceConfigTests
{
    [Fact]
    public void Version1File_IsMigrated_WithFrostedGlassByDefault()
    {
        var warnings = new List<string>();
        var config = ConfigSerializer.Deserialize("""
            { "schemaVersion": 1, "settings": { "hotkey": "Ctrl+Alt+Space" },
              "profiles": [ { "name": "P", "buttons": [ { "label": "B", "icon": { "kind": "Glyph", "glyph": "x" } } ] } ] }
            """, warnings);
        Assert.Equal(2, config.SchemaVersion);
        Assert.Contains(warnings, w => w.Contains("schéma 1 vers le schéma 2"));
        // Verre dépoli par défaut : voile blanc léger (20 %) + flou de l'arrière-plan, boutons en verre, taille automatique.
        Assert.Equal("#FFFFFF", config.Settings.Panel.BackgroundColor);
        Assert.Equal(20, config.Settings.Panel.BackgroundOpacity);
        Assert.Equal(PanelSizeMode.Auto, config.Settings.Panel.SizeMode);
        Assert.True(config.Settings.Panel.BlurBehind);
        Assert.Equal(TileStyle.Glass, config.Settings.Panel.TileStyle);
        Assert.Equal(PanelPositionMode.Centered, config.Settings.Panel.Position);
        Assert.Equal(IconKind.Glyph, config.Profiles[0].Buttons[0].Icon.Kind);
    }

    [Fact]
    public void NewIconKinds_RoundTrip()
    {
        var config = SampleConfig.CreateEmpty();
        config.Profiles[0].Buttons.Add(new DeckButton { Icon = new ButtonIcon { Kind = IconKind.Emoji, Glyph = "🚀" } });
        config.Profiles[0].Buttons.Add(new DeckButton { Icon = new ButtonIcon { Kind = IconKind.Brand, Glyph = "spotify" } });
        var back = ConfigSerializer.Deserialize(ConfigSerializer.Serialize(config), []);
        Assert.Equal(IconKind.Emoji, back.Profiles[0].Buttons[0].Icon.Kind);
        Assert.Equal("🚀", back.Profiles[0].Buttons[0].Icon.Glyph);
        Assert.Equal("spotify", back.Profiles[0].Buttons[1].Icon.Glyph);
    }

    [Fact]
    public void PanelAppearance_IsValidatedAndClamped()
    {
        var config = SampleConfig.CreateEmpty();
        config.Settings.Panel = new PanelAppearance
        {
            BackgroundColor = "vert", BackgroundOpacity = 250, IconScale = 5, LabelScale = 999, TileSpacing = -3,
            TileCornerRadius = 99, PositionX = double.NaN, PositionY = 7, Position = (PanelPositionMode)42, TileStyle = (TileStyle)9,
            SizeMode = (PanelSizeMode)5, PanelWidth = 10, PanelHeight = double.PositiveInfinity,
        };
        ConfigValidator.Normalize(config);
        var p = config.Settings.Panel;
        Assert.Equal(PanelAppearance.DefaultBackground, p.BackgroundColor);
        Assert.Equal(100, p.BackgroundOpacity);
        Assert.Equal(40, p.IconScale);
        Assert.Equal(180, p.LabelScale);
        Assert.Equal(0, p.TileSpacing);
        Assert.Equal(40, p.TileCornerRadius);
        Assert.Equal(0.5, p.PositionX);
        Assert.Equal(1, p.PositionY);
        Assert.Equal(PanelPositionMode.Centered, p.Position);
        Assert.Equal(TileStyle.Glass, p.TileStyle);
        Assert.Equal(PanelSizeMode.Auto, p.SizeMode);
        Assert.Equal(PanelAppearance.MinPanelSize, p.PanelWidth);
        Assert.Equal(420, p.PanelHeight);
    }

    [Fact]
    public void ManualPanelSize_RoundTrips()
    {
        var config = SampleConfig.CreateEmpty();
        config.Settings.Panel.SizeMode = PanelSizeMode.Manual;
        config.Settings.Panel.PanelWidth = 150;
        config.Settings.Panel.PanelHeight = 820;
        var back = ConfigSerializer.Deserialize(ConfigSerializer.Serialize(config), []);
        Assert.Equal(PanelSizeMode.Manual, back.Settings.Panel.SizeMode);
        Assert.Equal(150, back.Settings.Panel.PanelWidth);
        Assert.Equal(820, back.Settings.Panel.PanelHeight);
    }

    [Fact]
    public void PanelBackgroundImage_IsExported_AndImportedWithSettings()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var img = source.File("fond.png");
        File.WriteAllBytes(img, [0x89, 0x50, 0x4E, 0x47, 9, 9, 9]);
        var config = SampleConfig.CreateEmpty();
        config.Settings.Panel.BackgroundImage = img;
        config.Settings.Panel.BackgroundOpacity = 60;
        var zip = source.File("e.zogmadeck");

        DeckPackage.Export(config, zip, new ExportOptions(null, IncludeSettings: true), source.Path);
        using (var archive = ZipFile.OpenRead(zip)) Assert.Single(archive.Entries, e => e.FullName.StartsWith("assets/"));

        var current = SampleConfig.CreateEmpty();
        var preview = DeckPackage.ReadPackage(zip, target.Path);
        Assert.Empty(preview.Missing);
        DeckPackage.Apply(preview, current, ImportMode.Merge, importSettings: true, target.Path);
        Assert.Equal(60, current.Settings.Panel.BackgroundOpacity);
        Assert.StartsWith("assets/", current.Settings.Panel.BackgroundImage);
        Assert.True(File.Exists(ResourcePaths.Resolve(current.Settings.Panel.BackgroundImage, target.Path)));
    }

    [Fact]
    public void PanelBackgroundImage_IsNotExported_WithoutSettings()
    {
        using var dir = new TempDir();
        var img = dir.File("fond.png");
        File.WriteAllBytes(img, [1, 2, 3]);
        var config = SampleConfig.CreateEmpty();
        config.Settings.Panel.BackgroundImage = img;
        var zip = dir.File("e.zogmadeck");
        var report = DeckPackage.Export(config, zip, new ExportOptions(null, IncludeSettings: false), dir.Path);
        Assert.Empty(report.BundledFiles);
    }
}

/// <summary>Les catalogues sont des ressources WPF « pack:// » : on initialise l'assembly de ressources.</summary>
public class IconLibraryTests
{
    static IconLibraryTests()
    {
        if (Application.ResourceAssembly is null) Application.ResourceAssembly = typeof(App).Assembly;
    }

    [Fact]
    public void Catalogs_AreLoaded()
    {
        Assert.True(IconLibrary.Fluent.Count >= 1000, $"symboles : {IconLibrary.Fluent.Count}");
        Assert.True(IconLibrary.Emoji.Count >= 1000, $"emoji : {IconLibrary.Emoji.Count}");
        Assert.True(IconLibrary.Brands.Count >= 100, $"logos : {IconLibrary.Brands.Count}");
        Assert.Equal(IconLibrary.Fluent.Count, IconLibrary.Fluent.Select(c => c.Value).Distinct().Count());
    }

    [Theory]
    [InlineData(IconKind.Emoji, "fusee", "🚀")]
    [InlineData(IconKind.Emoji, "chat", "🐱")]
    [InlineData(IconKind.Emoji, "Cœur rouge", "❤️")]
    [InlineData(IconKind.Glyph, "micro", "\uE720")]
    [InlineData(IconKind.Glyph, "play", "\uE768")]
    [InlineData(IconKind.Brand, "spotify", "spotify")]
    [InlineData(IconKind.Brand, "obs", "obsstudio")]
    public void Search_FindsExpectedIcon(IconKind kind, string query, string expected)
    {
        IEnumerable<IconChoice> source = kind switch
        {
            IconKind.Emoji => IconLibrary.Emoji,
            IconKind.Brand => IconLibrary.BrandChoices,
            _ => IconLibrary.Fluent,
        };
        Assert.Contains(IconLibrary.Search(source, query), c => c.Value == expected);
    }

    [Fact]
    public void Search_IsAccentAndCaseInsensitive()
    {
        Assert.Equal(IconLibrary.Search(IconLibrary.Emoji, "FUSÉE").Count(), IconLibrary.Search(IconLibrary.Emoji, "fusee").Count());
        Assert.Empty(IconLibrary.Search(IconLibrary.Emoji, "zzzzqqq"));
    }

    [Fact]
    public void EveryBrand_HasGeometryAndColor()
    {
        Assert.All(IconLibrary.Brands, b =>
        {
            Assert.False(b.Geometry.IsEmpty());
            Assert.Matches("^#[0-9A-Fa-f]{6}$", b.Hex);
        });
    }

    [Fact]
    public void Picker_FiltersByCategory_AndResetsSearchWhenKindChanges()
    {
        IconChoice? picked = null;
        var picker = new IconPickerViewModel(c => picked = c) { Kind = IconKind.Emoji };
        picker.Query = "fusée";
        Assert.Contains(picker.Rows.SelectMany(r => r.Items), c => c.Value == "🚀");
        picker.Kind = IconKind.Brand;
        Assert.Equal("", picker.Query);
        Assert.True(picker.Rows.Sum(r => r.Items.Count) >= 100);
        picker.Kind = IconKind.Emoji;
        picker.Category = IconLibrary.EmojiGroups[2]; // Animaux et nature
        Assert.All(picker.Rows.SelectMany(r => r.Items), c => Assert.Equal(IconLibrary.EmojiGroups[2], c.Category));
        picker.PickCommand.Execute(picker.Rows[0].Items[0]);
        Assert.NotNull(picked);
    }
}

public class PanelVisualTests
{
    [Fact]
    public void IconScale_And_LabelScale_AreApplied()
    {
        var button = new DeckButton { Label = "Test", Icon = ButtonIcon.FromGlyph("\uE768") };
        var normal = new PanelButtonViewModel(button, 100, 0, "", new PanelAppearance());
        var big = new PanelButtonViewModel(button, 100, 0, "", new PanelAppearance { IconScale = 200, LabelScale = 150, ShowShortcutNumbers = false });
        Assert.Equal(normal.GlyphSize * 2, big.GlyphSize, 1);
        Assert.True(big.LabelSize > normal.LabelSize);
        Assert.Equal("1", normal.ShortcutHint);
        Assert.Null(big.ShortcutHint);
    }

    [Fact]
    public void GlassStyle_PutsIconOnColoredBadge_SolidStyleUsesButtonColor()
    {
        var button = new DeckButton { Label = "Micro", Color = "#C42B1C", Icon = ButtonIcon.FromGlyph("\uE720") };
        var glass = new PanelButtonViewModel(button, 100, 0, "", new PanelAppearance());
        Assert.True(glass.IsGlass);
        Assert.False(double.IsNaN(glass.BadgeSize));
        Assert.NotNull(glass.BadgeBrush);
        Assert.Equal(System.Windows.Media.Brushes.White, glass.LabelForeground);
        Assert.NotNull(glass.LabelEffect);
        Assert.InRange(((System.Windows.Media.SolidColorBrush)glass.TileBackground).Color.A, 1, 0x60); // translucide

        var solid = new PanelButtonViewModel(button, 100, 0, "", new PanelAppearance { TileStyle = TileStyle.Solid });
        Assert.False(solid.IsGlass);
        Assert.True(double.IsNaN(solid.BadgeSize));
        Assert.Null(solid.BadgeBrush);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xC4, 0x2B, 0x1C), ((System.Windows.Media.SolidColorBrush)solid.TileBackground).Color);
        Assert.Null(solid.LabelEffect);
    }

    [Fact]
    public void GlassStyle_WithoutIcon_HasValidSizes()
    {
        var vm = new PanelButtonViewModel(new DeckButton { Label = "Vide" }, 92, 0, "", new PanelAppearance());
        Assert.False(double.IsNaN(vm.GlyphSize));
        Assert.False(double.IsNaN(vm.GeometrySize));
        Assert.True(double.IsNaN(vm.BadgeSize));
    }

    [Fact]
    public void UnknownBrand_FallsBackToWarningGlyph()
    {
        if (Application.ResourceAssembly is null) Application.ResourceAssembly = typeof(App).Assembly;
        var vm = new PanelButtonViewModel(new DeckButton { Icon = new ButtonIcon { Kind = IconKind.Brand, Glyph = "nexistepas" } }, 92, 0, "");
        Assert.False(vm.HasGeometry);
        Assert.Equal("\uE7BA", vm.Glyph);
    }
}

public class ShellIconTests
{
    [Fact]
    public void ProgramIcon_IsExtractedInHighResolution()
    {
        BitmapSource? icon = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { icon = ShellIcons.Get(Environment.ExpandEnvironmentVariables(@"%WINDIR%\explorer.exe")); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(error);
        Assert.NotNull(icon);
        Assert.True(icon!.PixelWidth >= 128, $"largeur {icon.PixelWidth}");

        // L'icône n'est pas vide : au moins un pixel non transparent.
        var pixels = new byte[icon.PixelWidth * icon.PixelHeight * 4];
        icon.CopyPixels(pixels, icon.PixelWidth * 4, 0);
        Assert.Contains(pixels.Where((_, i) => i % 4 == 3), a => a > 0);
    }

    [Fact]
    public void MissingFile_ReturnsNull() => Assert.Null(ShellIcons.Get(@"C:\nexiste\pas.exe"));
}
