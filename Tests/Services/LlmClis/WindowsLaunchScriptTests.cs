using System.Diagnostics;
using System.Text;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.LlmClis;

/// <summary>
/// The external Windows launch hands ShellExecute no working directory (CodeQL #38): the launch
/// script enters the project itself. These pin that the native command still starts there with its
/// argv intact, that no path or argument text reaches PowerShell's parser, and that a directory the
/// script cannot enter stops the launch instead of running vb elsewhere.
/// </summary>
public sealed class WindowsLaunchScriptTests
{
    // A legal Windows directory name. PowerShell ends a single-quoted string at the curly quotes as
    // well as at the ASCII apostrophe, so quoting that only doubles the apostrophe runs the $(...)
    // before vb starts (VIBE-152 review F1).
    private const string Hostile = "a\u2019 + $(Write-Host INJECTED) + \u2018b [1] it's";

    [Fact]
    public void ScriptCarriesNoValueTextForPowerShellToParse()
    {
        var script = BaseLlmCliLauncher.BuildWindowsLaunchScript(
            "C:\\vb " + Hostile + "\\vb.exe", ["--workdir", Hostile], "C:\\" + Hostile);

        Assert.DoesNotContain("INJECTED", script);
        Assert.DoesNotContain(script, c => c is '\u2018' or '\u2019' or '\u201A' or '\u201B');
        var enter = script.IndexOf("Set-Location -LiteralPath $dir -ErrorAction Stop", StringComparison.Ordinal);
        Assert.True(enter >= 0, script);
        Assert.True(enter < script.IndexOf("& $exe @argv", StringComparison.Ordinal), script);
    }

    [Fact]
    public async Task NativeCommandStartsInTheProjectDirectoryWithItsArgvIntact()
    {
        var pwsh = FindPwsh();
        var project = Directory.CreateTempSubdirectory(Hostile + " ").FullName;
        var echo = Path.Combine(Path.GetTempPath(), $"viberails-launch-echo-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            echo,
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" +
            "[Environment]::CurrentDirectory\n" +
            "$args | ForEach-Object { \"ARG:$_\" }\n",
            new UTF8Encoding(false));
        try
        {
            var (exitCode, stdout, stderr) = await RunScriptAsync(
                pwsh, project, ["-NoProfile", "-NonInteractive", "-File", echo, Hostile]);

            Assert.True(exitCode == 0, stdout + stderr);
            // The name and argument carry "INJECTED" as text; only a run payload prints it alone.
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.DoesNotContain("INJECTED", lines);
            Assert.Equal(Path.TrimEndingDirectorySeparator(project), lines[^2], ignoreCase: true);
            Assert.Equal("ARG:" + Hostile, lines[^1]);
        }
        finally
        {
            File.Delete(echo);
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task MissingProjectDirectoryStopsTheScriptBeforeVb()
    {
        var pwsh = FindPwsh();
        var missing = Path.Combine(Path.GetTempPath(), "viberails-missing-" + Guid.NewGuid().ToString("N"));

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            pwsh, missing, ["-NoProfile", "-NonInteractive", "-Command", "'vb-started'"]);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("vb-started", stdout + stderr);
    }

    private static string FindPwsh()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The external terminal launch script is Windows-only.");
        string? pwsh = null;
        try { pwsh = ShellDefaults.ResolveWindowsCommandShellPath(); }
        catch (InvalidOperationException) { }
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) is not on PATH.");
        return pwsh!;
    }

    /// <summary>Runs the launch script with pwsh standing in for vb, given <paramref name="argv"/>.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunScriptAsync(
        string pwsh, string workingDirectory, string[] argv)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = Path.Combine(Path.GetTempPath(), $"viberails-launch-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            script,
            BaseLlmCliLauncher.BuildWindowsLaunchScript(pwsh, argv, workingDirectory),
            new UTF8Encoding(false));
        try
        {
            var startInfo = new ProcessStartInfo(pwsh)
            {
                // Somewhere other than the project, as the launch's own process directory is.
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally { File.Delete(script); }
    }
}
