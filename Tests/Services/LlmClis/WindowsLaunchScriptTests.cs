using System.Diagnostics;
using System.Text;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.LlmClis;

/// <summary>
/// The external Windows launch hands ShellExecute no working directory (CodeQL #38): the launch
/// script enters the project itself. These pin that the native command still starts there, and
/// that a directory the script cannot enter stops the launch instead of running vb elsewhere.
/// </summary>
public sealed class WindowsLaunchScriptTests
{
    [Fact]
    public void ScriptEntersTheLiteralProjectDirectoryBeforeRunningVb()
    {
        var script = BaseLlmCliLauncher.BuildWindowsLaunchScript(
            "C:\\vb\\vb.exe", ["--env", "claude", "--workdir", "C:\\it's [1]"], "C:\\it's [1]");

        var enter = script.IndexOf("Set-Location -LiteralPath 'C:\\it''s [1]' -ErrorAction Stop", StringComparison.Ordinal);
        Assert.True(enter >= 0, script);
        Assert.True(enter < script.IndexOf("& $exe @argv", StringComparison.Ordinal), script);
    }

    [Fact]
    public async Task NativeCommandStartsInTheProjectDirectory()
    {
        var pwsh = FindPwsh();
        var project = Directory.CreateTempSubdirectory("viberails launch 'it's' [1] ").FullName;
        try
        {
            var (exitCode, stdout, stderr) = await RunScriptAsync(pwsh, project, "[Environment]::CurrentDirectory");

            Assert.True(exitCode == 0, stdout + stderr);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(project),
                Path.TrimEndingDirectorySeparator(stdout.Trim().Split('\n')[^1].Trim()),
                ignoreCase: true);
        }
        finally { Directory.Delete(project, recursive: true); }
    }

    [Fact]
    public async Task MissingProjectDirectoryStopsTheScriptBeforeVb()
    {
        var pwsh = FindPwsh();
        var missing = Path.Combine(Path.GetTempPath(), "viberails-missing-" + Guid.NewGuid().ToString("N"));

        var (exitCode, stdout, stderr) = await RunScriptAsync(pwsh, missing, "'vb-started'");

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

    /// <summary>Runs the launch script with pwsh standing in for vb, printing <paramref name="command"/>.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunScriptAsync(
        string pwsh, string workingDirectory, string command)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = Path.Combine(Path.GetTempPath(), $"viberails-launch-test-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(
            script,
            BaseLlmCliLauncher.BuildWindowsLaunchScript(pwsh, ["-NoProfile", "-NonInteractive", "-Command", command], workingDirectory),
            new UTF8Encoding(false));
        try
        {
            var startInfo = new ProcessStartInfo(pwsh)
            {
                // Somewhere other than the project, as the launch's own process directory is.
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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
