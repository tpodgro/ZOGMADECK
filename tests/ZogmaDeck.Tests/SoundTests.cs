using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using ZogmaDeck.Core.Actions;
using ZogmaDeck.Services;

namespace ZogmaDeck.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("83.5", 83.5)]
    [InlineData("83,5", 83.5)]
    [InlineData("1:23.5", 83.5)]
    [InlineData("0:05", 5)]
    [InlineData("1:02:03", 3723)]
    public void Parse_AcceptsSecondsAndMinutes(string text, double expected)
    {
        Assert.True(TimeFormat.TryParse(text, out var s));
        Assert.Equal(expected, s, 3);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1.5:20")]
    [InlineData("-3")]
    [InlineData("1:2:3:4")]
    public void Parse_RejectsInvalid(string text) => Assert.False(TimeFormat.TryParse(text, out _));

    [Theory]
    [InlineData(83.5, "1:23.5")]
    [InlineData(5, "0:05")]
    [InlineData(3723, "1:02:03")]
    [InlineData(0, "0:00")]
    public void Format_RoundTrips(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Format(seconds));
        Assert.True(TimeFormat.TryParse(expected, out var back));
        Assert.Equal(seconds, back, 3);
    }
}

public class PlaySoundActionTests
{
    [Fact]
    public void Segment_IsValidated_AndTranslatedToRequest()
    {
        var a = new PlaySoundAction { Path = "assets/sounds/klaxon.wav", Volume = 50, StartSeconds = 10, EndSeconds = 12.5, OnRepeat = SoundRepeatMode.Stop };
        Assert.Empty(a.Validate());
        var r = a.ToRequest(@"C:\Data");
        Assert.Equal(@"C:\Data\assets\sounds\klaxon.wav", r.Path);
        Assert.Equal(0.5, r.Volume);
        Assert.Equal(TimeSpan.FromSeconds(10), r.Start);
        Assert.Equal(TimeSpan.FromSeconds(12.5), r.End);
        Assert.Same(a, r.Owner);
        Assert.Equal(SoundRepeatMode.Stop, r.Repeat);
        Assert.Contains("0:10 → 0:12.5", a.Describe());

        a.EndSeconds = 5;
        Assert.Contains(a.Validate(), e => e.Contains("après son début"));
        a.EndSeconds = 0;
        Assert.Empty(a.Validate());
        Assert.Null(a.ToRequest(@"C:\Data").End); // jusqu'à la fin du fichier
    }

    [Fact]
    public void OldConfig_WithoutNewFields_KeepsDefaults()
    {
        var json = """{"schemaVersion":2,"settings":{},"profiles":[{"name":"P","buttons":[{"label":"S","actions":[{"type":"playSound","path":"a.wav","volume":70}]}]}]}""";
        var config = Core.Storage.ConfigSerializer.Deserialize(json, []);
        var a = Assert.IsType<PlaySoundAction>(config.Profiles[0].Buttons[0].Actions[0]);
        Assert.Equal(SoundRepeatMode.Restart, a.OnRepeat);
        Assert.Equal(0, a.StartSeconds);
        Assert.Equal(0, a.EndSeconds);
    }
}

/// <summary>Lecture réelle (Media Foundation) d'un fichier WAV silencieux, sur un thread d'interface.</summary>
public class AudioServiceTests
{
    private static void WriteSilentWav(string path, double seconds)
    {
        const int rate = 8000;
        var samples = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2); w.Write(new byte[samples * 2]);
    }

    private static void RunOnDispatcher(Func<Task> body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
            {
                try { await body(); }
                catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Délai dépassé");
        error?.Throw();
    }

    [Fact]
    public void Segment_StopsAtItsEnd()
    {
        using var dir = new TempDir();
        var wav = dir.File("silence.wav");
        WriteSilentWav(wav, 4);
        RunOnDispatcher(async () =>
        {
            var audio = new AudioService();
            var sw = Stopwatch.StartNew();
            await audio.PlayAsync(new SoundRequest(wav, 0, WaitForEnd: true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.6)), CancellationToken.None);
            sw.Stop();
            // 0,6 s d'extrait (plus l'ouverture du fichier), bien moins que les 4 s du fichier
            Assert.InRange(sw.Elapsed.TotalSeconds, 0.45, 2.5);
            Assert.Equal(0, audio.ActiveCount);
        });
    }

    [Fact]
    public void RepeatModes_StopOrRestartTheSameSource()
    {
        using var dir = new TempDir();
        var wav = dir.File("silence.wav");
        WriteSilentWav(wav, 5);
        RunOnDispatcher(async () =>
        {
            var audio = new AudioService();
            var owner = new object();
            var stopMode = new SoundRequest(wav, 0, false, Owner: owner, Repeat: SoundRepeatMode.Stop);
            await audio.PlayAsync(stopMode, CancellationToken.None);
            Assert.True(audio.IsPlaying(owner));
            await audio.PlayAsync(stopMode, CancellationToken.None); // second appui : arrêt
            Assert.False(audio.IsPlaying(owner));
            await audio.PlayAsync(stopMode, CancellationToken.None); // troisième : rejoue
            Assert.True(audio.IsPlaying(owner));

            var restart = stopMode with { Repeat = SoundRepeatMode.Restart };
            await audio.PlayAsync(restart, CancellationToken.None);
            Assert.Equal(1, audio.ActiveCount);

            var overlap = stopMode with { Repeat = SoundRepeatMode.Overlap };
            await audio.PlayAsync(overlap, CancellationToken.None);
            Assert.Equal(2, audio.ActiveCount);

            audio.StopAll();
            Assert.Equal(0, audio.ActiveCount);
        });
    }

    [Fact]
    public void StartBeyondDuration_GivesClearError()
    {
        using var dir = new TempDir();
        var wav = dir.File("court.wav");
        WriteSilentWav(wav, 1);
        RunOnDispatcher(async () =>
        {
            var audio = new AudioService();
            var ex = await Assert.ThrowsAsync<ActionException>(() =>
                audio.PlayAsync(new SoundRequest(wav, 0, false, TimeSpan.FromSeconds(30)), CancellationToken.None));
            Assert.Contains("dépasse la durée", ex.Message);
            Assert.Equal(0, audio.ActiveCount);
        });
    }
}
