using System.Diagnostics;
using Xunit;

namespace Tests.Deployment;

/// <summary>
/// Runs isolated PowerShell regressions for deployment payload validation and runtime bundling.
/// These scripts never install the application or touch its user data.
/// </summary>
public sealed class LocalDeployPayloadTests
{
    [Theory]
    [InlineData("LocalDeployPayloadTests.ps1", "No deployment was run")]
    [InlineData("WindowsRuntimePayloadTests.ps1", "No installation was run")]
    public async Task DeploymentPayloadRegression_Passes(string scriptName, string expectedMessage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = Path.Combine(FindRepositoryRoot(), "Tests", "Deployment", scriptName);
        Assert.True(File.Exists(script), $"Missing {script}");

        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        // A visible skip, not a silent pass: CI runners ship PowerShell 7, so the guard runs there.
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) is not on PATH.");

        var startInfo = new ProcessStartInfo(pwsh!)
        {
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

        var output = await stdout;
        Assert.True(process.ExitCode == 0, $"pwsh exited {process.ExitCode}.\n{output}\n{await stderr}");
        Assert.Contains("Passed:", output);
        Assert.Contains(expectedMessage, output);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("VibeRails.slnx was not found above the test output directory.");
    }

    private static string? FindOnPath(string executable)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
