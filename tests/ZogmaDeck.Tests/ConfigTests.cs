using System.Text.Json.Nodes;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Model;
using ZogmaDeck.Core.Storage;

namespace ZogmaDeck.Tests;

public class ConfigSerializationTests
{
    [Fact]
    public void SampleConfig_RoundTrips_Identically()
    {
        var config = SampleConfig.Create();
        var json = ConfigSerializer.Serialize(config);
        var warnings = new List<string>();
        var back = ConfigSerializer.Deserialize(json, warnings);

        Assert.Empty(warnings);
        Assert.Equal(json, ConfigSerializer.Serialize(back));
        Assert.Equal(3, back.Profiles.Count);
        Assert.Equal(["Travail", "Studio", "Multimédia"], back.Profiles.Select(p => p.Name));
    }

    [Fact]
    public void Actions_AreWritten_WithStableTypeDiscriminator()
    {
        var config = SampleConfig.Create();
        var root = JsonNode.Parse(ConfigSerializer.Serialize(config))!;
        var firstAction = root["profiles"]![0]!["buttons"]![0]!["actions"]![0]!;
        Assert.Equal("launch", firstAction["type"]!.GetValue<string>());
        Assert.Equal(DeckConfig.CurrentSchemaVersion, root["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void AllRegisteredActionTypes_RoundTrip()
    {
        foreach (var info in ActionRegistry.All)
        {
            var action = ActionRegistry.Create(info.TypeId);
            var clone = action.Clone();
            Assert.IsType(info.ClrType, clone);
            Assert.Equal(info.TypeId, clone.TypeId);
        }
    }

    [Fact]
    public void UnknownActionType_IsPreserved_AndReportedAsInvalid()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "profiles": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "P", "buttons": [
            { "id": "22222222-2222-2222-2222-222222222222", "label": "B", "actions": [
              { "type": "futureAction", "magic": 42, "nested": { "a": [1, 2] } } ] } ] } ]
        }
        """;
        var config = ConfigSerializer.Deserialize(json, []);
        var action = Assert.IsType<UnknownAction>(config.Profiles[0].Buttons[0].Actions[0]);
        Assert.Equal("futureAction", action.OriginalTypeId);
        Assert.NotEmpty(action.Validate());

        var rewritten = JsonNode.Parse(ConfigSerializer.Serialize(config))!;
        var raw = rewritten["profiles"]![0]!["buttons"]![0]!["actions"]![0]!;
        Assert.Equal(42, raw["magic"]!.GetValue<int>());
        Assert.Equal(2, raw["nested"]!["a"]![1]!.GetValue<int>());
    }

    [Fact]
    public void InvalidJson_ThrowsConfigFormatException()
    {
        Assert.Throws<ConfigFormatException>(() => ConfigSerializer.Deserialize("{ not json", []));
        Assert.Throws<ConfigFormatException>(() => ConfigSerializer.Deserialize("[1,2,3]", []));
        Assert.Throws<ConfigFormatException>(() => ConfigSerializer.Deserialize("""{ "profiles": "oops" }""", []));
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        var config = ConfigSerializer.Deserialize("""
        {
          // configuration écrite à la main
          "schemaVersion": 1,
          "profiles": [ { "name": "Manuel", "buttons": [], }, ],
        }
        """, []);
        Assert.Equal("Manuel", config.Profiles[0].Name);
    }
}

public class ConfigValidationTests
{
    [Fact]
    public void Normalize_FixesInvalidValues()
    {
        var dup = Guid.NewGuid();
        var config = new DeckConfig
        {
            Settings = new AppSettings { Hotkey = "Ctrl+Truc", Obs = new ObsSettings { Port = 99999, Host = " " } },
            Profiles =
            [
                new Profile { Id = dup, Name = "", Columns = 99, ButtonSize = 5, Buttons = [new DeckButton { Id = dup, Color = "bleu" }] },
                new Profile { Id = dup, Name = "B", Buttons = [new DeckButton { Id = dup }] },
            ],
            ActiveProfileId = Guid.NewGuid(),
        };

        var warnings = ConfigValidator.Normalize(config);

        Assert.Equal(AppSettings.DefaultHotkey, config.Settings.Hotkey);
        Assert.Equal(4455, config.Settings.Obs.Port);
        Assert.Equal("localhost", config.Settings.Obs.Host);
        Assert.Equal("Profil 1", config.Profiles[0].Name);
        Assert.Equal(Profile.MaxColumns, config.Profiles[0].Columns);
        Assert.Equal(Profile.MinButtonSize, config.Profiles[0].ButtonSize);
        Assert.Equal(DeckButton.DefaultColor, config.Profiles[0].Buttons[0].Color);
        Assert.NotEqual(config.Profiles[0].Id, config.Profiles[1].Id);
        Assert.NotEqual(config.Profiles[0].Buttons[0].Id, config.Profiles[1].Buttons[0].Id);
        Assert.Equal(config.Profiles[0].Id, config.ActiveProfileId);
        Assert.True(warnings.Count >= 4);
    }

    [Fact]
    public void Normalize_CreatesProfile_WhenNone()
    {
        var config = new DeckConfig { Profiles = [] };
        var warnings = ConfigValidator.Normalize(config);
        Assert.Single(config.Profiles);
        Assert.Contains(warnings, w => w.Contains("Aucun profil"));
    }

    [Fact]
    public void NullCollectionsInJson_AreRepaired()
    {
        var config = ConfigSerializer.Deserialize("""
        { "schemaVersion": 1, "settings": null,
          "profiles": [ { "name": "P", "buttons": [ null, { "label": null, "icon": null, "actions": null } ] } ] }
        """, []);
        Assert.NotNull(config.Settings);
        var b = Assert.Single(config.Profiles[0].Buttons);
        Assert.Equal("", b.Label);
        Assert.NotNull(b.Icon);
        Assert.Empty(b.Actions);
    }

    [Theory]
    [InlineData("#2F6FDB", true)]
    [InlineData("#802F6FDB", true)]
    [InlineData("2F6FDB", false)]
    [InlineData("#2F6FD", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void ColorValidation(string? color, bool valid) => Assert.Equal(valid, ConfigValidator.IsValidColor(color));
}

public class ConfigMigrationTests
{
    [Fact]
    public void UnversionedFile_IsMigratedToCurrentVersion()
    {
        var warnings = new List<string>();
        var config = ConfigSerializer.Deserialize("""{ "profiles": [ { "name": "Ancien" } ] }""", warnings);
        Assert.Equal(DeckConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Contains(warnings, w => w.Contains("migrée"));
    }

    [Fact]
    public void NewerVersion_IsLoadedWithWarning()
    {
        var warnings = new List<string>();
        var config = ConfigSerializer.Deserialize("""{ "schemaVersion": 99, "futureField": true, "profiles": [ { "name": "Futur" } ] }""", warnings);
        Assert.Equal("Futur", config.Profiles[0].Name);
        Assert.Contains(warnings, w => w.Contains("plus récente"));
    }
}

public class ConfigStoreTests
{
    [Fact]
    public void FirstRun_IsDetected()
    {
        using var dir = new TempDir();
        var result = new ConfigStore(dir.Path).Load();
        Assert.True(result.IsFirstRun);
        Assert.Null(result.Config);
    }

    [Fact]
    public void SaveThenLoad_ReturnsSameConfig_AndKeepsBackupAndSnapshot()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        var config = SampleConfig.Create();
        store.Save(config);
        config.Profiles[0].Name = "Modifié";
        store.Save(config);

        var loaded = new ConfigStore(dir.Path).Load();
        Assert.False(loaded.IsFirstRun);
        Assert.Empty(loaded.Warnings);
        Assert.Equal("Modifié", loaded.Config!.Profiles[0].Name);
        Assert.True(File.Exists(store.BackupPath), "la version précédente doit être conservée en .bak");
        Assert.Contains("Travail", File.ReadAllText(store.BackupPath));
        Assert.NotEmpty(store.ListSnapshots());
        Assert.False(File.Exists(store.ConfigPath + ".tmp"));
    }

    [Fact]
    public void CorruptedPrimary_IsRecoveredFromBackup_AndQuarantined()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        var config = SampleConfig.Create();
        store.Save(config);
        store.Save(config); // crée le .bak
        File.WriteAllText(store.ConfigPath, "{ corrompu");

        var result = new ConfigStore(dir.Path).Load();

        Assert.NotNull(result.Config);
        Assert.True(result.RecoveredFromBackup);
        Assert.Contains(result.Warnings, w => w.Contains("illisible"));
        Assert.Contains(store.ListSnapshots(), f => f.Name.Contains("corrompu"));
    }

    [Fact]
    public void CorruptedPrimaryAndBackup_ReturnsNoConfig_WithWarnings()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        File.WriteAllText(store.ConfigPath, "{ corrompu");
        File.WriteAllText(store.BackupPath, "aussi corrompu");

        var result = store.Load();

        Assert.Null(result.Config);
        Assert.False(result.IsFirstRun);
        Assert.True(result.Warnings.Count >= 2);
    }

    [Fact]
    public void Snapshots_ArePrunedToTen()
    {
        using var dir = new TempDir();
        var store = new ConfigStore(dir.Path);
        store.Save(SampleConfig.Create());
        Directory.CreateDirectory(store.BackupsDirectory);
        for (var i = 0; i < 15; i++)
        {
            var f = Path.Combine(store.BackupsDirectory, $"config-2020010{i % 10}-00000{i}-ancien.json");
            File.WriteAllText(f, "{}");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-30 + i));
        }
        store.CreateSnapshot("test");
        Assert.Equal(10, store.ListSnapshots().Count);
        Assert.Contains(store.ListSnapshots(), f => f.Name.EndsWith("-test.json"));
    }
}
