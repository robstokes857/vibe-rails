using System.Text;
using System.Text.Json;
using Moq;
using PyBridge;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using VibeRails.Services.PythonScripts;
using Xunit;

namespace Tests.Services;

/// <summary>
/// VIBE-56: the signed scripts library runs pwsh (.ps1) and bash (.sh) scripts as well as
/// Python, with the extension picking the interpreter. Signing, hashing and the authoring
/// rules are shared with Python and covered by <see cref="PythonScriptServiceTests"/>.
/// </summary>
public sealed class PythonScriptShellRuntimeTests : IDisposable
{
    private const string FakePwsh = "/fake/bin/pwsh";
    private const string FakeBash = "/fake/bin/bash";

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(), "vb-pyscript-shell-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_installDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData("deploy.ps1", JobScriptRuntime.PowerShell, "PowerShell")]
    [InlineData("backup.sh", JobScriptRuntime.Bash, "Bash")]
    [InlineData("report.py", JobScriptRuntime.Python, "Python")]
    public void TheExtensionPicksTheRuntime(string name, JobScriptRuntime runtime, string displayName)
    {
        Assert.Equal(runtime, PythonScriptService.RuntimeFor(name));
        Assert.Equal(displayName, PythonScriptService.RuntimeDisplayName(name));
    }

    [Fact]
    public async Task ListingShowsPythonPowerShellAndBashScriptsAndNothingElse()
    {
        var (service, _, _) = NewService();
        foreach (var name in new[] { "a.py", "b.ps1", "c.sh", "d.txt", "e.bat", ".f.sh.0123.tmp" })
            await WriteScriptAsync(name, "x\n");

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a.py", "b.ps1", "c.sh"], status.Scripts.Select(script => script.Name));
    }

    [Theory]
    [InlineData("test.sh")]
    [InlineData("Deploy Tools.ps1")]
    public async Task CreateAcceptsShellScriptNames(string name)
    {
        var (service, _, _) = NewService();

        var created = await service.CreateAsync(
            new PythonScriptSaveRequest(name, "echo hi\n"), TestContext.Current.CancellationToken);

        var script = Assert.Single(created.Scripts);
        Assert.Equal(name, script.Name);
        Assert.Equal(PythonScriptService.StatusUnapproved, script.Status);
    }

    [Theory]
    [InlineData("test.txt")]
    [InlineData("test.SH")]
    [InlineData("test.Ps1")]
    [InlineData("test.bash")]
    public async Task OtherExtensionsAreRefusedWithTheThreeAllowedOnesNamed(string name)
    {
        var (service, _, _) = NewService();

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(() => service.CreateAsync(
            new PythonScriptSaveRequest(name, "x\n"), TestContext.Current.CancellationToken));

        Assert.Contains("plain .py, .ps1 or .sh file name", error.Message);
    }

    [Fact]
    public async Task ImportKeepsTheOriginalFileNameAndPath()
    {
        var (service, _, _) = NewService();
        Directory.CreateDirectory(_installDirectory);
        var source = Path.Combine(_installDirectory, "Deploy.PS1");
        await File.WriteAllTextAsync(source, "Write-Output 1\n", TestContext.Current.CancellationToken);

        var imported = await service.ImportAsync(
            new PythonScriptImportRequest(source, null), TestContext.Current.CancellationToken);

        Assert.Contains(imported.Scripts, script => script.Name == "Deploy.PS1");
    }

    [Fact]
    public async Task PowerShellRunsTheVerifiedCopyWithPwshAndTheAutomationSwitches()
    {
        var (service, runner, factoryOptions) = NewService();
        await SignAsync(service, "deploy.ps1", "param($Target)\r\nWrite-Output \"to $Target\"\r\n");
        IReadOnlyList<string>? arguments = null;
        string? standardInput = null;
        byte[]? executedBytes = null;
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                arguments = (IReadOnlyList<string>)invocation.Arguments[0];
                standardInput = (string?)invocation.Arguments[1];
                executedBytes = File.ReadAllBytes(arguments[6]);
            }))
            .ReturnsAsync(Completed("to prod\n{\"ok\":true}"));

        var result = await service.RunAsync(
            "deploy.ps1", ["-Target", "prod"], "piped", TestContext.Current.CancellationToken);

        var options = Assert.Single(factoryOptions);
        Assert.Equal(FakePwsh, options.PythonExecutable);
        Assert.Equal(service.GetScriptsDirectory(), options.WorkingDirectory);
        Assert.False(options.UseUtf8Io, "PYTHON* variables mean nothing to pwsh");
        Assert.False(options.UseUnbufferedOutput);
        Assert.NotNull(arguments);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"],
            arguments.Take(6));
        // pwsh only accepts -File for a .ps1, and the copy is never the writable original.
        Assert.EndsWith(".ps1", arguments[6], StringComparison.Ordinal);
        Assert.NotEqual(ScriptPath("deploy.ps1"), arguments[6]);
        Assert.Equal(["-Target", "prod"], arguments.Skip(7));
        Assert.Equal("piped", standardInput);
        // PowerShell runs the exact signed bytes, CRLF and all.
        Assert.Equal(File.ReadAllBytes(ScriptPath("deploy.ps1")), executedBytes);
        Assert.False(File.Exists(arguments[6]), "the verified copy is removed after the run");
        Assert.Equal("{\"ok\":true}", result.ReturnJson);
        Assert.Equal(Assert.Single((await service.GetStatusAsync(TestContext.Current.CancellationToken)).Scripts).Id, Assert.Single(service.GetRunHistory().Runs).Name);
    }

    [Fact]
    public async Task BashRunsTheCanonicalLfTextFromARelativeSlashPath()
    {
        var (service, runner, factoryOptions) = NewService();
        await SignAsync(service, "backup.sh", "﻿#!/usr/bin/env bash\r\necho one\r\necho two\r\n");
        IReadOnlyList<string>? arguments = null;
        byte[]? executedBytes = null;
        runner
            .Setup(item => item.RunAsync(
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<string?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                arguments = (IReadOnlyList<string>)invocation.Arguments[0];
                executedBytes = File.ReadAllBytes(
                    Path.GetFullPath(Path.Combine(service.GetScriptsDirectory(), arguments[0])));
            }))
            .ReturnsAsync(Completed("one\ntwo\n"));

        await service.RunAsync("backup.sh", ["--dry-run"], TestContext.Current.CancellationToken);

        Assert.Equal(FakeBash, Assert.Single(factoryOptions).PythonExecutable);
        Assert.NotNull(arguments);
        Assert.Equal(2, arguments.Count);
        // Git Bash and POSIX bash both read a relative slash path; a drive path is not portable.
        Assert.DoesNotContain('\\', arguments[0]);
        Assert.False(Path.IsPathRooted(arguments[0]));
        Assert.EndsWith(".sh", arguments[0], StringComparison.Ordinal);
        Assert.Equal("--dry-run", arguments[1]);
        // The signed text with the BOM and every '\r' removed: bash would read them as commands.
        Assert.Equal("#!/usr/bin/env bash\necho one\necho two\n", Encoding.UTF8.GetString(executedBytes!));
    }

    [Fact]
    public async Task AMissingInterpreterIsAnActionableErrorAndNothingRuns()
    {
        var resolver = new Mock<IJobExecutableResolver>(MockBehavior.Strict);
        resolver.Setup(item => item.Resolve(JobScriptRuntime.PowerShell)).Returns((JobExecutable?)null);
        var (service, runner, _) = NewService(resolver.Object);
        await SignAsync(service, "deploy.ps1", "Write-Output 1\n");

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("deploy.ps1", TestContext.Current.CancellationToken));

        Assert.Contains("PowerShell 7 (pwsh)", error.Message);
        runner.VerifyNoOtherCalls();
        Assert.Empty(service.GetRunHistory().Runs);
    }

    [Fact]
    public async Task AnUnsignedShellScriptNeverReachesItsInterpreter()
    {
        var (service, runner, _) = NewService();
        await WriteScriptAsync("backup.sh", "echo hi\n");
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<PythonScriptValidationException>(
            () => service.RunAsync("backup.sh", TestContext.Current.CancellationToken));

        Assert.Contains("not signed", error.Message);
        runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ARealPowerShellScriptRunsWithArgumentsAndReturnsJson()
    {
        Assert.SkipWhen(new JobExecutableResolver().Resolve(JobScriptRuntime.PowerShell) is null,
            "PowerShell 7 (pwsh) is not installed on this machine.");
        var service = new PythonScriptService(installDirectory: _installDirectory);
        await SignAsync(service, "greet.ps1",
            "param([string]$Name = 'nobody')\r\n"
            + "Write-Output \"hello $Name\"\r\n"
            + "@{ name = $Name } | ConvertTo-Json -Compress\r\n");

        var result = await service.RunAsync(
            "greet.ps1", ["-Name", "viberails"], TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("hello viberails", result.StandardOutput);
        Assert.NotNull(result.ReturnJson);
        using var json = JsonDocument.Parse(result.ReturnJson);
        Assert.Equal("viberails", json.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ARealBashScriptWithCrlfLineEndingsRunsWithArgumentsAndStdin()
    {
        Assert.SkipWhen(new JobExecutableResolver().Resolve(JobScriptRuntime.Bash) is null,
            "Bash (Git Bash on Windows) is not installed on this machine.");
        var service = new PythonScriptService(installDirectory: _installDirectory);
        await SignAsync(service, "greet.sh",
            "#!/usr/bin/env bash\r\n"
            + "set -euo pipefail\r\n"
            + "read -r line || true\r\n"
            + "echo \"hello $1 ($line)\"\r\n"
            + "printf '{\"count\": %d}\\n' \"$#\"\r\n");

        var result = await service.RunAsync(
            "greet.sh", ["viberails"], "from stdin\n", TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("hello viberails (from stdin)", result.StandardOutput);
        Assert.Equal("{\"count\": 1}", result.ReturnJson);
    }

    [Fact]
    public async Task ARealPowerShellScriptRunsInteractivelyAndReportsItsExitCode()
    {
        Assert.SkipWhen(new JobExecutableResolver().Resolve(JobScriptRuntime.PowerShell) is null,
            "PowerShell 7 (pwsh) is not installed on this machine.");
        var service = new PythonScriptService(installDirectory: _installDirectory);
        await SignAsync(service, "leave.ps1", "exit 7\n");

        var exitCode = await service.RunInteractiveAsync("leave.ps1", TestContext.Current.CancellationToken);

        Assert.Equal(7, exitCode);
        Assert.Equal(7, Assert.Single(service.GetRunHistory().Runs).ExitCode);
    }

    private static PythonResult Completed(string standardOutput) => new()
    {
        ExitCode = 0,
        StandardOutput = standardOutput,
        StandardError = "",
        RunTime = TimeSpan.FromMilliseconds(9),
        Executable = "fake",
        CommandLine = "fake script"
    };

    private (PythonScriptService Service, Mock<IPythonRunner> Runner, List<PythonRunnerOptions> FactoryOptions)
        NewService(IJobExecutableResolver? resolver = null)
    {
        var runner = new Mock<IPythonRunner>(MockBehavior.Loose);
        runner.SetupGet(item => item.Options).Returns(new PythonRunnerOptions());
        var factoryOptions = new List<PythonRunnerOptions>();
        if (resolver is null)
        {
            var fake = new Mock<IJobExecutableResolver>(MockBehavior.Strict);
            fake.Setup(item => item.Resolve(JobScriptRuntime.PowerShell)).Returns(new JobExecutable(FakePwsh, []));
            fake.Setup(item => item.Resolve(JobScriptRuntime.Bash)).Returns(new JobExecutable(FakeBash, []));
            resolver = fake.Object;
        }

        var capturedRunner = runner.Object;
        var service = new PythonScriptService(
            capturedRunner,
            _installDirectory,
            runnerFactory: options =>
            {
                factoryOptions.Add(options);
                return capturedRunner;
            },
            executableResolver: resolver);
        return (service, runner, factoryOptions);
    }

    private async Task SignAsync(PythonScriptService service, string name, string content)
    {
        await WriteScriptAsync(name, content);
        await service.SetPinAsync(
            new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(
            new PythonScriptApprovalRequest(name, "1234"), TestContext.Current.CancellationToken);
    }

    private string ScriptPath(string name) =>
        Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory, name);

    private async Task WriteScriptAsync(string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory));
        File.WriteAllText(ScriptPath(name), content);
        if (new[] { ".py", ".ps1", ".sh" }.Contains(Path.GetExtension(name)))
        {
            var library = new PythonScriptService(installDirectory: _installDirectory);
            if (!(await library.GetStatusAsync(TestContext.Current.CancellationToken)).Scripts.Any(item => item.Path == ScriptPath(name)))
                await library.ImportAsync(new PythonScriptImportRequest(ScriptPath(name)), TestContext.Current.CancellationToken);
        }
    }
}
