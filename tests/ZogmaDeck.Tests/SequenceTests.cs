using ZogmaDeck.Core.Actions;
using ZogmaDeck.Core.Execution;
using ZogmaDeck.Core.Obs;

namespace ZogmaDeck.Tests;

public class SequenceTests
{
    private static Task<SequenceResult> Run(FakeServices fake, CancellationToken ct = default, bool hide = false, params DeckAction[] actions) =>
        SequenceRunner.RunAsync(actions, fake.Context(hide), ct);

    [Fact]
    public async Task ExampleFromSpec_OpenObs_Wait_Scene_Jingle_RunsInOrder()
    {
        var fake = new FakeServices();
        var result = await Run(fake, default, false,
            new LaunchAppAction { Path = @"C:\obs\obs64.exe", WorkingDirectory = @"C:\obs" },
            new DelayAction { DurationMs = 5000 },
            new ObsAction { Command = ObsCommand.SetScene, SceneName = "Principale" },
            new PlaySoundAction { Path = @"C:\sons\jingle.mp3", Volume = 50 });

        Assert.Equal(SequenceStatus.Succeeded, result.Status);
        Assert.Equal(
        [
            @"launch:C:\obs\obs64.exe||C:\obs",
            "delay:5000",
            """obs:SetCurrentProgramScene {"sceneName":"Principale"}""",
            "play:jingle.mp3@0.50",
        ], fake.Calls);
        Assert.All(result.Steps, s => Assert.Equal(StepStatus.Succeeded, s.Status));
    }

    [Fact]
    public async Task ErrorWithStopPolicy_StopsSequence_AndSkipsRemaining()
    {
        var fake = new FakeServices { FailOn = c => c.StartsWith("open:") ? new ActionException("Fichier introuvable : x") : null };
        var result = await Run(fake, default, false,
            new OpenAction { Target = "x" },
            new DelayAction { DurationMs = 10 },
            new StopSoundsAction());

        Assert.Equal(SequenceStatus.Failed, result.Status);
        Assert.Equal("Fichier introuvable : x", result.FirstError);
        Assert.Equal([StepStatus.Failed, StepStatus.Skipped, StepStatus.Skipped], result.Steps.Select(s => s.Status));
        Assert.Equal(["open:x"], fake.Calls);
    }

    [Fact]
    public async Task ErrorWithContinuePolicy_ContinuesAndReportsErrors()
    {
        var fake = new FakeServices { FailOn = c => c.StartsWith("open:") ? new ActionException("échec") : null };
        var result = await Run(fake, default, false,
            new OpenAction { Target = "x", OnError = ErrorPolicy.Continue },
            new StopSoundsAction());

        Assert.Equal(SequenceStatus.CompletedWithErrors, result.Status);
        Assert.Equal(["open:x", "stopSounds"], fake.Calls);
        Assert.Contains("échec", result.Summary);
    }

    [Fact]
    public async Task UnexpectedException_IsCaught_AndNeverEscapes()
    {
        var fake = new FakeServices { FailOn = c => c == "stopSounds" ? new InvalidOperationException("boum") : null };
        var result = await Run(fake, default, false, new StopSoundsAction());
        Assert.Equal(SequenceStatus.Failed, result.Status);
        Assert.Equal("Erreur inattendue : boum", result.FirstError);
    }

    [Fact]
    public async Task DisabledStep_IsSkipped()
    {
        var fake = new FakeServices();
        var result = await Run(fake, default, false,
            new OpenAction { Target = "a", Enabled = false },
            new OpenAction { Target = "b" });
        Assert.Equal(SequenceStatus.Succeeded, result.Status);
        Assert.Equal(["open:b"], fake.Calls);
        Assert.Equal(StepStatus.Skipped, result.Steps[0].Status);
    }

    [Fact]
    public async Task InvalidConfiguration_FailsBeforeCallingServices()
    {
        var fake = new FakeServices();
        var result = await Run(fake, default, false, new LaunchAppAction { Path = "" });
        Assert.Equal(SequenceStatus.Failed, result.Status);
        Assert.Contains("programme", result.FirstError);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Cancellation_StopsAtCurrentStep_AndReportsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var fake = new FakeServices { FailOn = c => { if (c.StartsWith("delay")) cts.Cancel(); return null; } };
        var result = await Run(fake, cts.Token, false,
            new OpenAction { Target = "a" },
            new DelayAction { DurationMs = 60000 },
            new OpenAction { Target = "b" });

        Assert.Equal(SequenceStatus.Cancelled, result.Status);
        Assert.Equal([StepStatus.Succeeded, StepStatus.Cancelled, StepStatus.Skipped], result.Steps.Select(s => s.Status));
        Assert.DoesNotContain("open:b", fake.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledToken_RunsNothing()
    {
        var fake = new FakeServices();
        var result = await Run(fake, new CancellationToken(true), false, new OpenAction { Target = "a" });
        Assert.Equal(SequenceStatus.Cancelled, result.Status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task RealDelay_IsCancellable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var ctx = new FakeServices().Context() is var c
            ? new ActionContext
            {
                Shell = c.Shell, Input = c.Input, Audio = c.Audio, Obs = c.Obs, Profiles = c.Profiles, Focus = c.Focus,
                Delay = new TaskDelayService(),
            }
            : null!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await SequenceRunner.RunAsync([new DelayAction { DurationMs = 60_000 }], ctx, cts.Token);
        Assert.Equal(SequenceStatus.Cancelled, result.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task KeyboardActions_RestoreFocusFirst_ButMediaKeysDoNot()
    {
        var fake = new FakeServices();
        await Run(fake, default, true,
            new HotkeyAction { Keys = "Ctrl+V" },
            new HotkeyAction { Keys = "MediaPlayPause" },
            new TypeTextAction { Text = "Ça marche ✓ 😀" });

        Assert.Equal(
        [
            "focus:hide=True", "chord:Ctrl+V",
            "chord:MediaPlayPause",
            "focus:hide=True", "type:Ça marche ✓ 😀",
        ], fake.Calls);
    }

    [Fact]
    public async Task SwitchProfile_ToDeletedProfile_Fails()
    {
        var fake = new FakeServices();
        var known = Guid.NewGuid();
        fake.KnownProfiles.Add(known);
        var result = await Run(fake, default, false,
            new SwitchProfileAction { ProfileId = known, ProfileName = "Studio" },
            new SwitchProfileAction { Mode = ProfileSwitchMode.Next },
            new SwitchProfileAction { ProfileId = Guid.NewGuid(), ProfileName = "Supprimé" });

        Assert.Equal(SequenceStatus.Failed, result.Status);
        Assert.Contains("Supprimé", result.FirstError);
        Assert.Equal(StepStatus.Succeeded, result.Steps[1].Status);
    }

    [Fact]
    public async Task ObsFailure_IsReportedWithMessage()
    {
        var fake = new FakeServices
        {
            FailOn = c => c.StartsWith("obs:") ? new ObsException("Impossible de joindre OBS sur localhost:4455.") : null,
        };
        var result = await Run(fake, default, false, new ObsAction { Command = ObsCommand.StartRecord });
        Assert.Equal(SequenceStatus.Failed, result.Status);
        Assert.Contains("OBS", result.FirstError);
    }

    [Fact]
    public async Task Progress_IsReported_ForEachStep()
    {
        var fake = new FakeServices();
        var reports = new List<StepProgress>();
        var progress = new SyncProgress(reports.Add);
        await SequenceRunner.RunAsync([new StopSoundsAction(), new StopSoundsAction()], fake.Context(), default, progress);
        Assert.Equal(4, reports.Count);
        Assert.Equal(StepStatus.Running, reports[0].Status);
        Assert.Equal(StepStatus.Succeeded, reports[3].Status);
        Assert.Equal(2, reports[3].Count);
    }

    private sealed class SyncProgress(Action<StepProgress> report) : IProgress<StepProgress>
    {
        public void Report(StepProgress value) => report(value);
    }
}

public class ActionDefinitionTests
{
    [Theory]
    [InlineData(ObsCommand.StartRecord, "StartRecord", null)]
    [InlineData(ObsCommand.ToggleStream, "ToggleStream", null)]
    [InlineData(ObsCommand.ToggleRecordPause, "ToggleRecordPause", null)]
    [InlineData(ObsCommand.MuteInput, "SetInputMute", """{"inputName":"Mic/Aux","inputMuted":true}""")]
    [InlineData(ObsCommand.UnmuteInput, "SetInputMute", """{"inputName":"Mic/Aux","inputMuted":false}""")]
    [InlineData(ObsCommand.ToggleInputMute, "ToggleInputMute", """{"inputName":"Mic/Aux"}""")]
    [InlineData(ObsCommand.SaveReplayBuffer, "SaveReplayBuffer", null)]
    public void ObsCommands_MapToProtocolRequests(ObsCommand command, string type, string? data)
    {
        var (requestType, requestData) = new ObsAction { Command = command, InputName = " Mic/Aux " }.BuildRequest();
        Assert.Equal(type, requestType);
        Assert.Equal(data, requestData?.ToJsonString());
    }

    [Fact]
    public void ObsCustomRequest_ValidatesJson()
    {
        var bad = new ObsAction { Command = ObsCommand.Custom, RequestType = "SetCurrentPreviewScene", RequestData = "{ sceneName: " };
        Assert.Contains(bad.Validate(), p => p.Contains("JSON invalide"));
        var notObject = new ObsAction { Command = ObsCommand.Custom, RequestType = "X", RequestData = "[1]" };
        Assert.Contains(notObject.Validate(), p => p.Contains("objet JSON"));

        var ok = new ObsAction { Command = ObsCommand.Custom, RequestType = "SetCurrentPreviewScene", RequestData = """{ "sceneName": "B" }""" };
        Assert.Empty(ok.Validate());
        var (t, d) = ok.BuildRequest();
        Assert.Equal("SetCurrentPreviewScene", t);
        Assert.Equal("B", d!["sceneName"]!.GetValue<string>());
    }

    [Fact]
    public void Registry_ContainsAllBuiltInTypes_WithConsistentFieldMetadata()
    {
        string[] expected = ["launch", "open", "playSound", "stopSounds", "hotkey", "typeText", "obs", "delay", "switchProfile"];
        foreach (var id in expected) Assert.NotNull(ActionRegistry.Find(id));

        foreach (var info in ActionRegistry.All)
        foreach (var (prop, field) in DeckAction.GetFields(info.ClrType))
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Label));
            if (field.VisibleWhen is { } controlling)
                Assert.NotNull(info.ClrType.GetProperty(controlling)); // la propriété de contrôle doit exister
        }
    }

    [Fact]
    public void Describe_IsReadableFrench()
    {
        Assert.Equal("Attendre 1,5 s", new DelayAction { DurationMs = 1500 }.Describe().Replace('.', ','));
        Assert.Equal("Attendre 250 ms", new DelayAction { DurationMs = 250 }.Describe());
        Assert.Equal("Touches Ctrl + Maj + Échap", new HotkeyAction { Keys = "Ctrl+Shift+Esc" }.Describe());
        Assert.Equal("OBS : scène « Principale »", new ObsAction { SceneName = "Principale" }.Describe());
    }
}
