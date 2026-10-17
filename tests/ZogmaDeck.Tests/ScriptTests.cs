using System.Diagnostics;
using ZogmaDeck.Core.Actions;

namespace ZogmaDeck.Tests;

/// <summary>Exécution réelle de scripts (cmd, Windows PowerShell, Python si installé).</summary>
public class ScriptTests
{
    private static readonly ProcessScriptService Service = new();

    private static ScriptRequest Req(ScriptLanguage lang, string code, bool wait = true, int timeoutSeconds = 30, bool failOnError = true, string? args = null) =>
        new(lang, code, null, args, null, Hidden: true, wait, TimeSpan.FromSeconds(timeoutSeconds), failOnError);

    [Fact]
    public async Task Cmd_RunsWithAccents_AndArguments()
    {
        using var dir = new TempDir();
        var output = dir.File("sortie cmd.txt");
        await Service.RunAsync(Req(ScriptLanguage.Cmd, $"echo Café %1> \"{output}\"", args: "arg1"), CancellationToken.None);
        Assert.Equal("Café arg1", File.ReadAllText(output).Trim());
    }

    [Fact]
    public async Task PowerShell_RunsMultilineScript_WithUnicode()
    {
        using var dir = new TempDir();
        var output = dir.File("sortie.txt");
        var code = $"""
            $texte = 'Été ✓ ' + (2 + 3)
            Set-Content -LiteralPath '{output}' -Value $texte -Encoding UTF8
            """;
        await Service.RunAsync(Req(ScriptLanguage.PowerShell, code), CancellationToken.None);
        Assert.Equal("Été ✓ 5", File.ReadAllText(output).Trim().TrimStart('\uFEFF'));
    }

    [Fact]
    public async Task NonZeroExit_IsAnError_WithStderrDetail_UnlessDisabled()
    {
        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            Service.RunAsync(Req(ScriptLanguage.Cmd, "echo problème précis 1>&2\r\nexit /b 3"), CancellationToken.None));
        Assert.Contains("code 3", ex.Message);
        Assert.Contains("problème précis", ex.Message);

        await Service.RunAsync(Req(ScriptLanguage.Cmd, "exit /b 3", failOnError: false), CancellationToken.None);
    }

    [Fact]
    public async Task Timeout_KillsTheScript()
    {
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ActionException>(() =>
            Service.RunAsync(Req(ScriptLanguage.PowerShell, "Start-Sleep -Seconds 30", timeoutSeconds: 1), CancellationToken.None));
        Assert.Contains("durée maximale", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_StopsTheScript()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service.RunAsync(Req(ScriptLanguage.PowerShell, "Start-Sleep -Seconds 30"), cts.Token));
    }

    [Fact]
    public async Task TemporaryScriptFiles_AreDeleted()
    {
        var before = Directory.Exists(ProcessScriptService.TempDirectory) ? Directory.GetFiles(ProcessScriptService.TempDirectory).Length : 0;
        await Service.RunAsync(Req(ScriptLanguage.Cmd, "exit /b 0"), CancellationToken.None);
        var after = Directory.GetFiles(ProcessScriptService.TempDirectory).Length;
        Assert.True(after <= before);
    }

    [Fact]
    public async Task ExistingFile_UsesInterpreterFromExtension()
    {
        using var dir = new TempDir();
        var script = dir.File("test.ps1");
        var output = dir.File("ok.txt");
        File.WriteAllText(script, $"Set-Content -LiteralPath '{output}' -Value ok");
        await Service.RunAsync(new ScriptRequest(ScriptLanguage.File, null, script, null, null, true, true, TimeSpan.FromSeconds(30), true), CancellationToken.None);
        Assert.Equal("ok", File.ReadAllText(output).Trim());

        var missing = await Assert.ThrowsAsync<ActionException>(() =>
            Service.RunAsync(new ScriptRequest(ScriptLanguage.File, null, dir.File("absent.ps1"), null, null, true, true, TimeSpan.FromSeconds(5), true), CancellationToken.None));
        Assert.Contains("introuvable", missing.Message);
    }

    [Fact]
    public async Task Python_RunsWhenInstalled()
    {
        if (ProcessScriptService.FindOnPath("py.exe") is null && ProcessScriptService.FindOnPath("python.exe", skipStoreAlias: true) is null)
            return; // Python absent de ce poste : rien à vérifier
        using var dir = new TempDir();
        var output = dir.File("py.txt");
        var code = $"open(r'{output}', 'w', encoding='utf-8').write('Python ' + str(6 * 7) + ' é')";
        await Service.RunAsync(Req(ScriptLanguage.Python, code), CancellationToken.None);
        Assert.Equal("Python 42 é", File.ReadAllText(output));
    }

    [Theory]
    [InlineData(@"C:\Users\a b\x.sh", "/mnt/c/Users/a b/x.sh")]
    [InlineData(@"D:\x.sh", "/mnt/d/x.sh")]
    public void WslPath_IsConverted(string windows, string expected) => Assert.Equal(expected, ProcessScriptService.ToWslPath(windows));

    [Fact]
    public void Validation_RequiresCodeOrFile()
    {
        Assert.Contains("vide", new ScriptAction { Language = ScriptLanguage.Cmd }.Validate().Single());
        Assert.Contains("fichier", new ScriptAction { Language = ScriptLanguage.File }.Validate().Single());
        Assert.Empty(new ScriptAction { Language = ScriptLanguage.Python, Code = "print(1)" }.Validate());
    }
}
