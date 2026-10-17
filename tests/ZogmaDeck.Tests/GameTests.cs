using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Tests;

public class GameTests
{
    private const string LibraryFolders = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"C:\\Program Files (x86)\\Steam"
                "label"		""
                "apps"
                {
                    "228980"		"0"
                }
            }
            "1"
            {
                "path"		"D:\\SteamLibrary"
                "apps" { "730" "123" }
            }
        }
        """;

    [Fact]
    public void SteamLibraryFolders_AreRead_CurrentAndLegacyFormats()
    {
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameManifests.SteamLibraryFolders(LibraryFolders));

        const string legacy = """
            "LibraryFolders"
            {
                "TimeNextStatsReport"  "1561832478"
                "1"  "E:\\Jeux\\Steam"
            }
            """;
        Assert.Equal([@"E:\Jeux\Steam"], GameManifests.SteamLibraryFolders(legacy));
    }

    [Fact]
    public void SteamManifest_GivesGame_AndSkipsRedistributables()
    {
        const string acf = """
            "AppState"
            {
                "appid"		"730"
                "Universe"		"1"
                "name"		"Counter-Strike 2"
                "StateFlags"		"4"
            }
            """;
        var game = GameManifests.SteamGameFromManifest(acf);
        Assert.Equal(new InstalledGame(GamePlatform.Steam, "730", "Counter-Strike 2"), game);

        Assert.Null(GameManifests.SteamGameFromManifest("\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" }"));
        Assert.Null(GameManifests.SteamGameFromManifest("\"AppState\" { \"appid\" \"1493710\" \"name\" \"Proton Experimental\" }"));
        Assert.Null(GameManifests.SteamGameFromManifest("pas du vdf {"));
    }

    [Fact]
    public void EpicManifest_GivesFullLaunchId()
    {
        const string item = """
            { "FormatVersion": 0, "DisplayName": "Rocket League", "AppName": "Sugar",
              "CatalogNamespace": "9773aa1aa54f4f7b80e44bef04986cea", "CatalogItemId": "530145df28a24424923f5828cc9031a1",
              "bIsIncompleteInstall": false }
            """;
        var game = GameManifests.EpicGameFromManifest(item);
        Assert.NotNull(game);
        Assert.Equal("Rocket League", game.Name);
        Assert.Equal("9773aa1aa54f4f7b80e44bef04986cea:530145df28a24424923f5828cc9031a1:Sugar", game.Id);
        Assert.Equal("com.epicgames.launcher://apps/9773aa1aa54f4f7b80e44bef04986cea%3A530145df28a24424923f5828cc9031a1%3ASugar?action=launch&silent=true",
            GameUris.EpicLaunch(game.Id));

        Assert.Null(GameManifests.EpicGameFromManifest("""{ "DisplayName": "X", "AppName": "Y", "bIsIncompleteInstall": true }"""));
        Assert.Null(GameManifests.EpicGameFromManifest("{ cassé"));
    }

    [Theory]
    [InlineData(SteamCommand.Friends, null, "steam://open/friends")]
    [InlineData(SteamCommand.StatusInvisible, null, "steam://friends/status/invisible")]
    [InlineData(SteamCommand.BigPicture, null, "steam://open/bigpicture")]
    [InlineData(SteamCommand.StorePage, "730", "steam://store/730")]
    [InlineData(SteamCommand.Exit, null, "steam://exit")]
    public void SteamCommands_MapToSteamUris(SteamCommand command, string? appId, string expected) =>
        Assert.Equal(expected, GameUris.Steam(command, appId));

    [Fact]
    public async Task LaunchGame_ValidatesAndDelegates()
    {
        var steam = new LaunchGameAction { Platform = GamePlatform.Steam, GameId = "abc" };
        Assert.Contains("nombre", steam.Validate().Single());
        steam.GameId = "730";
        Assert.Empty(steam.Validate());
        Assert.Equal("steam://rungameid/730", GameUris.SteamRun("730"));
        Assert.Equal("uplay://launch/635/0", GameUris.UbisoftLaunch("635"));

        var fake = new RecordingGames();
        var ctx = new FakeServices().Context() is var c ? new ActionContext
        {
            Shell = c.Shell, Input = c.Input, Audio = c.Audio, Obs = c.Obs, Profiles = c.Profiles, Focus = c.Focus, Games = fake,
        } : null!;
        await new LaunchGameAction { Platform = GamePlatform.BattleNet, GameId = " WoW " }.ExecuteAsync(ctx, CancellationToken.None);
        Assert.Equal((GamePlatform.BattleNet, "WoW"), fake.Last);
    }

    private sealed class RecordingGames : IGameService
    {
        public (GamePlatform, string)? Last { get; private set; }

        public Task LaunchAsync(GamePlatform platform, string gameId, CancellationToken ct)
        {
            Last = (platform, gameId);
            return Task.CompletedTask;
        }
    }
}
