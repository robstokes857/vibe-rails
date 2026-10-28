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
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        // A visible skip, not a silent pass: CI runners ship PowerShell 7, so the guard runs there.
        Assert.SkipWhen(pwsh is null, "PowerShell 7 (pwsh) is not on PATH.");

        var output = await RunScriptAsync(pwsh!, scriptName);
        Assert.Contains(expectedMessage, output);
    }

    /// <summary>
    /// install.ps1's publisher check under both hosts: `irm ... | iex` runs in Windows
    /// PowerShell 5.1 on a stock machine. Authenticode exists only on Windows.
    /// </summary>
    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("powershell.exe")]
    public async Task InstallerPublisherSignatureRegression_Passes(string hostExecutable)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Authenticode signatures are checked only on Windows.");
        var host = FindOnPath(hostExecutable);
        Assert.SkipWhen(host is null, $"{hostExecutable} is not on PATH.");

        var output = await RunScriptAsync(host!, "InstallerPublisherSignatureTests.ps1");
        Assert.Contains("No installation was run", output);
    }

    private static async Task<string> RunScriptAsync(string host, string scriptName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var script = Path.Combine(FindRepositoryRoot(), "Tests", "Deployment", scriptName);
        Assert.True(File.Exists(script), $"Missing {script}");

        var startInfo = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Let each host build its own module path: Windows PowerShell 5.1 fails to load
        // Microsoft.PowerShell.Security when it inherits PowerShell 7's PSModulePath.
        startInfo.Environment.Remove("PSModulePath");
        // Process-scoped Bypass: Windows PowerShell 5.1 defaults to Restricted on client SKUs.
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }
            : new[] { "-NoProfile", "-NonInteractive", "-File", script };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await stdout;
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(host)} exited {process.ExitCode}.\n{output}\n{await stderr}");
        Assert.Contains("Passed:", output);
        return output;
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
