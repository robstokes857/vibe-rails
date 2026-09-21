using System.Diagnostics;
using Xunit;

namespace Tests.Deployment;

/// <summary>
/// Runs the PowerShell regression test for deploy/local_deploy.ps1's payload validator under
/// dotnet test, so the "never overwrite state.db or board.db" guard cannot regress silently.
/// The .ps1 loads only the validator function; it never executes the deployment itself.
/// </summary>
public sealed class LocalDeployPayloadTests
{
    [Fact]
    public async Task DeployPayloadValidator_RejectsEveryDatabaseFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = Path.Combine(FindRepositoryRoot(), "Tests", "Deployment", "LocalDeployPayloadTests.ps1");
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
        Assert.Contains("No deployment was run", output);
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
