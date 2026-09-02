using System.Security.Cryptography;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class AutomationScriptServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"viberails-automation-script-{Guid.NewGuid():N}");
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), $"viberails-automation-workspace-{Guid.NewGuid():N}");
    private readonly Mock<IJobExecutableResolver> _resolver = new();

    public AutomationScriptServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scripts", "tools"));
        Directory.CreateDirectory(_workspace);
        _resolver
            .Setup(candidate => candidate.Resolve(JobScriptRuntime.Python))
            .Returns(new JobExecutable("python-test", ["-I"]));
        _resolver
            .Setup(candidate => candidate.Resolve(JobScriptRuntime.PowerShell))
            .Returns(new JobExecutable("pwsh-test", []));
        _resolver
            .Setup(candidate => candidate.Resolve(JobScriptRuntime.Bash))
            .Returns(new JobExecutable("bash-test", []));
    }

    [Fact]
    public async Task NormalizeAsync_PersistsPortableRelativePathsAndPinsTheCurrentBytes()
    {
        var scriptPath = WriteScript("scripts/check.py", "print('checked')\n");
        var workingDirectory = Path.Combine(_root, "scripts", "tools");
        var arguments = new List<string> { "--name", "value with spaces", "; literal shell text" };

        var normalized = await Service().NormalizeAsync(
            _root,
            new JobActionRequest(
                "not-a-guid",
                JobActionKind.Script,
                EnvironmentId: 42,
                ScriptPath: scriptPath,
                ScriptRuntime: JobScriptRuntime.Python,
                Arguments: arguments,
                WorkingDirectory: workingDirectory,
                TimeoutSeconds: 45),
            TestContext.Current.CancellationToken);

        Assert.True(Guid.TryParse(normalized.Id, out _));
        Assert.Null(normalized.EnvironmentId);
        Assert.Equal("scripts/check.py", normalized.ScriptPath);
        Assert.Equal("scripts/tools", normalized.WorkingDirectory);
        Assert.Equal(arguments, normalized.Arguments);
        Assert.Equal(45, normalized.TimeoutSeconds);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(
                scriptPath,
                TestContext.Current.CancellationToken))),
            normalized.ApprovedHash);
    }

    [Fact]
    public async Task NormalizeAsync_RejectsAScriptOutsideTheRepository()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(outside, "print('outside')", TestContext.Current.CancellationToken);

        try
        {
            var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
                Service().NormalizeAsync(
                    _root,
                    ScriptAction(outside),
                    TestContext.Current.CancellationToken));

            Assert.Contains("inside the current repository", error.Message);
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public async Task NormalizeAsync_RejectsARuntimeWhoseExtensionDoesNotMatch()
    {
        var scriptPath = WriteScript("scripts/check.sh", "#!/usr/bin/env bash\n");

        var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
            Service().NormalizeAsync(
                _root,
                ScriptAction(scriptPath, JobScriptRuntime.PowerShell),
                TestContext.Current.CancellationToken));

        Assert.Contains(".ps1", error.Message);
    }

    [Fact]
    public async Task NormalizeAsync_WhenApprovalIsDisabled_RejectsChangedBytes()
    {
        var scriptPath = WriteScript("scripts/check.py", "print('one')\n");
        var first = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(scriptPath, "print('two')\n", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
            Service().NormalizeAsync(
                _root,
                first,
                TestContext.Current.CancellationToken,
                approveCurrentVersion: false));

        Assert.Contains("has changed", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_UsesAnExplicitInterpreterAndKeepsArgumentsDiscrete()
    {
        var scriptPath = WriteScript("scripts/check.py", "print('checked')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath) with
            {
                Arguments = ["--name", "value with spaces", "$(still-literal)"],
                WorkingDirectory = "scripts/tools"
            },
            TestContext.Current.CancellationToken);

        var prepared = await Service().PrepareAsync(
            _root,
            _root,
            RunAction(normalized),
            TestContext.Current.CancellationToken);

        Assert.Equal("python-test", prepared.Executable);
        Assert.Equal(
            ["-I", Path.GetFullPath(scriptPath), "--name", "value with spaces", "$(still-literal)"],
            prepared.Arguments);
        Assert.Equal(Path.Combine(_root, "scripts", "tools"), prepared.WorkingDirectory);
        Assert.Equal("run-1", prepared.EnvironmentVariables["VIBERAILS_JOB_RUN_ID"]);
        Assert.Equal(normalized.Id, prepared.EnvironmentVariables["VIBERAILS_ACTION_ID"]);
        Assert.Equal(Path.GetFullPath(_root), prepared.EnvironmentVariables["VIBERAILS_WORKSPACE_ROOT"]);
    }

    [Fact]
    public async Task PrepareAsync_RejectsAWorkspaceCopyWhoseBytesNoLongerMatchTheSnapshot()
    {
        var scriptPath = WriteScript("scripts/check.py", "print('approved')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(scriptPath, "print('changed')\n", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
            Service().PrepareAsync(
                _root,
                _root,
                RunAction(normalized),
                TestContext.Current.CancellationToken));

        Assert.Contains("approve the current script", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_RunsTheApprovedProjectCopyWhenTheWorkspaceCopyLagsBehind()
    {
        // A PerRun workspace is cloned from HEAD and a Persistent one from its first launch, so
        // either can hold older bytes than the tree the hash was pinned from. The approved project
        // copy must run instead — from the workspace's working directory, where the Worker's
        // changes live.
        var scriptPath = WriteScript("scripts/check.py", "print('approved')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);
        WriteFile(Path.Combine(_workspace, "scripts", "check.py"), "print('stale clone')\n");

        var prepared = await Service().PrepareAsync(
            _workspace,
            _root,
            RunAction(normalized),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(scriptPath), prepared.Arguments[1]);
        Assert.Equal(Path.GetFullPath(_workspace), prepared.WorkingDirectory);
        Assert.Equal(Path.GetFullPath(_workspace), prepared.EnvironmentVariables["VIBERAILS_WORKSPACE_ROOT"]);
    }

    [Fact]
    public async Task PrepareAsync_RunsTheApprovedProjectCopyWhenTheWorkspaceLacksTheScript()
    {
        // An uncommitted script never reaches a HEAD-only clone at all.
        var scriptPath = WriteScript("scripts/check.py", "print('approved')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);

        var prepared = await Service().PrepareAsync(
            _workspace,
            _root,
            RunAction(normalized),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(scriptPath), prepared.Arguments[1]);
        Assert.Equal(Path.GetFullPath(_workspace), prepared.WorkingDirectory);
    }

    [Fact]
    public async Task PrepareAsync_PrefersTheWorkspaceCopyWhenItCarriesTheApprovedBytes()
    {
        var scriptPath = WriteScript("scripts/check.py", "print('approved')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);
        var workspaceCopy = Path.Combine(_workspace, "scripts", "check.py");
        WriteFile(workspaceCopy, "print('approved')\n");

        var prepared = await Service().PrepareAsync(
            _workspace,
            _root,
            RunAction(normalized),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(workspaceCopy), prepared.Arguments[1]);
    }

    [Fact]
    public async Task PrepareAsync_FailsClosedWhenNeitherCopyMatchesTheApprovedBytes()
    {
        // A Worker that edits the script in its clone while the user also edits the project copy
        // leaves no approved bytes anywhere; nothing may run.
        var scriptPath = WriteScript("scripts/check.py", "print('approved')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);
        WriteFile(Path.Combine(_workspace, "scripts", "check.py"), "print('edited by the worker')\n");
        await File.WriteAllTextAsync(scriptPath, "print('edited by the user')\n", TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
            Service().PrepareAsync(
                _workspace,
                _root,
                RunAction(normalized),
                TestContext.Current.CancellationToken));

        Assert.Contains("does not match the version approved", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_ReportsAMissingScriptRatherThanAReparsePoint()
    {
        var scriptPath = WriteScript("scripts/check.py", "print('approved')\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath),
            TestContext.Current.CancellationToken);
        File.Delete(scriptPath);

        var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
            Service().PrepareAsync(
                _workspace,
                _root,
                RunAction(normalized),
                TestContext.Current.CancellationToken));

        Assert.Contains("was not found", error.Message);
        Assert.DoesNotContain("reparse point", error.Message);
    }

    [Fact]
    public async Task PrepareAsync_RunsPowerShellWithTheMachinePolicyBypassed()
    {
        // The hash pin is the trust decision; Restricted/AllSigned or a Zone.Identifier on the
        // file must not veto an approved script, and a prompt must fail rather than hang.
        var scriptPath = WriteScript("scripts/check.ps1", "Write-Output 'checked'\n");
        var normalized = await Service().NormalizeAsync(
            _root,
            ScriptAction(scriptPath, JobScriptRuntime.PowerShell) with { Arguments = ["-Mode", "strict"] },
            TestContext.Current.CancellationToken);

        var prepared = await Service().PrepareAsync(
            _root,
            _root,
            RunAction(normalized),
            TestContext.Current.CancellationToken);

        Assert.Equal("pwsh-test", prepared.Executable);
        Assert.Equal(
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.GetFullPath(scriptPath), "-Mode", "strict"],
            prepared.Arguments);
    }

    [Fact]
    public async Task NormalizeAsync_RejectsSymbolicLinksInsideTheRepository()
    {
        var target = WriteScript("scripts/target.py", "print('target')\n");
        var link = Path.Combine(_root, "scripts", "linked.py");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("This platform does not permit creating file symbolic links.");
        }

        var error = await Assert.ThrowsAsync<AutomationScriptValidationException>(() =>
            Service().NormalizeAsync(
                _root,
                ScriptAction(link),
                TestContext.Current.CancellationToken));

        Assert.Contains("symbolic link or reparse point", error.Message);
    }

    private AutomationScriptService Service() => new(_resolver.Object);

    private JobActionRequest ScriptAction(
        string scriptPath,
        JobScriptRuntime runtime = JobScriptRuntime.Python) => new(
        Guid.NewGuid().ToString(),
        JobActionKind.Script,
        ScriptPath: scriptPath,
        ScriptRuntime: runtime,
        Arguments: [],
        TimeoutSeconds: 30);

    private static JobRunActionRecord RunAction(JobActionRequest action) => new(
        action.Id!,
        "run-1",
        action.Id,
        0,
        JobActionKind.Script,
        JobRunActionStatus.Pending,
        null,
        null,
        LLM.NotSet,
        action.ScriptPath,
        action.ScriptRuntime,
        action.Arguments ?? [],
        action.WorkingDirectory,
        action.TimeoutSeconds,
        action.ApprovedHash,
        null,
        null,
        null,
        null,
        null,
        string.Empty,
        string.Empty);

    private string WriteScript(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        WriteFile(path, content);
        return path;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }
}
