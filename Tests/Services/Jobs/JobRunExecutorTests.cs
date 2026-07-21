using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobRunExecutorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viberails_job_runner_{Guid.NewGuid():N}");
    private readonly JobStore _store;

    public JobRunExecutorTests()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = $"Data Source={Path.Combine(_directory, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        CreateEnvironmentTable(connectionString);
        _store = new JobStore(connectionString);
    }

    [Fact]
    public void BuildStartInfo_CodexForcesFiniteReviewArgumentsAndEnvironmentIsolation()
    {
        var run = Run(
            LLM.Codex,
            JobExecutionMode.Review,
            environmentArgs: "--sandbox danger-full-access --add-dir C:\\outside --json --no-alt-screen --model gpt-5",
            environmentName: "nightly",
            environmentPath: _directory,
            triggerContextJson: "{\"commit\":\"abc\"}");

        var startInfo = JobRunExecutor.BuildStartInfo(run, "codex", _directory);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("--ask-for-approval", arguments[0]);
        Assert.Equal("never", arguments[1]);
        Assert.Equal("exec", arguments[2]);
        Assert.Contains("--model", arguments);
        Assert.Contains("gpt-5", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--json"));
        Assert.Contains("--ephemeral", arguments);
        Assert.Equal("read-only", arguments[Array.IndexOf(arguments, "--sandbox") + 1]);
        Assert.Equal(0, Array.IndexOf(arguments, "--ask-for-approval"));
        Assert.DoesNotContain("danger-full-access", arguments);
        Assert.DoesNotContain("--add-dir", arguments);
        Assert.DoesNotContain("C:\\outside", arguments);
        Assert.DoesNotContain("--no-alt-screen", arguments);
        Assert.Contains("VibeRails trigger metadata follows", arguments[^1]);
        Assert.Equal(Path.Combine(_directory, "codex"), startInfo.Environment["CODEX_HOME"]);
    }

    [Fact]
    public void BuildStartInfo_ClaudeForcesPrintModeAndWritePermissions()
    {
        var run = Run(
            LLM.Claude,
            JobExecutionMode.LiveWrite,
            environmentArgs: "--permission-mode bypassPermissions --output-format text --add-dir /outside /also-outside --model opus --dangerously-skip-permissions",
            environmentName: "reviewer",
            environmentPath: _directory);

        var startInfo = JobRunExecutor.BuildStartInfo(run, "claude", _directory);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Contains("--model", arguments);
        Assert.Contains("opus", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--print"));
        Assert.Equal("stream-json", arguments[Array.IndexOf(arguments, "--output-format") + 1]);
        Assert.Equal("acceptEdits", arguments[Array.IndexOf(arguments, "--permission-mode") + 1]);
        Assert.Contains("--no-session-persistence", arguments);
        Assert.DoesNotContain("bypassPermissions", arguments);
        Assert.DoesNotContain("dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--add-dir", arguments);
        Assert.DoesNotContain("/outside", arguments);
        Assert.DoesNotContain("/also-outside", arguments);
        Assert.Equal(Path.Combine(_directory, "claude"), startInfo.Environment["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void BuildStartInfo_AntigravityReviewForcesFiniteSandboxedPrintMode()
    {
        var run = Run(
            LLM.Antigravity,
            JobExecutionMode.Review,
            environmentArgs: "--dangerously-skip-permissions --prompt-interactive old-prompt --add-dir C:\\outside --model \"Gemini 3.5 Flash (High)\"",
            environmentName: "agy-review",
            environmentPath: _directory);

        var inheritedXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var startInfo = JobRunExecutor.BuildStartInfo(run, "agy", _directory);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Contains("--model", arguments);
        Assert.Contains("Gemini 3.5 Flash (High)", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--sandbox"));
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--prompt-interactive", arguments);
        Assert.DoesNotContain("old-prompt", arguments);
        Assert.DoesNotContain("--add-dir", arguments);
        Assert.DoesNotContain("C:\\outside", arguments);
        Assert.Equal("30m", arguments[Array.IndexOf(arguments, "--print-timeout") + 1]);
        Assert.Equal("Review this repository.", arguments[Array.IndexOf(arguments, "--print") + 1]);
        Assert.Equal(inheritedXdgConfigHome, GetEnvironmentValue(startInfo, "XDG_CONFIG_HOME"));
    }

    [Fact]
    public void BuildStartInfo_AntigravityWriteForcesNonBlockingPermissions()
    {
        var run = Run(
            LLM.Antigravity,
            JobExecutionMode.IsolatedWrite,
            environmentArgs: "--sandbox --dangerously-skip-permissions --model test-model");

        var arguments = JobRunExecutor.BuildStartInfo(run, "agy", _directory).ArgumentList.ToArray();

        Assert.Equal(1, arguments.Count(argument => argument == "--sandbox"));
        Assert.Equal(1, arguments.Count(argument => argument == "--dangerously-skip-permissions"));
        Assert.Equal(1, arguments.Count(argument => argument == "--print"));
    }

    [Fact]
    public void BuildStartInfo_CopilotReviewOverridesSavedModeAndYoloFlags()
    {
        var run = Run(
            LLM.Copilot,
            JobExecutionMode.Review,
            environmentArgs: "--mode autopilot --yolo --interactive old-prompt --allow-all-paths --model gpt-5.4",
            environmentName: "copilot-review",
            environmentPath: _directory);

        var inheritedXdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var startInfo = JobRunExecutor.BuildStartInfo(run, "copilot", _directory);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("plan", arguments[Array.IndexOf(arguments, "--mode") + 1]);
        Assert.Equal(1, arguments.Count(argument => argument == "--allow-all-tools"));
        Assert.Contains("--no-ask-user", arguments);
        Assert.Contains("--no-remote", arguments);
        Assert.Equal("json", arguments[Array.IndexOf(arguments, "--output-format") + 1]);
        Assert.Equal("Review this repository.", arguments[Array.IndexOf(arguments, "--prompt") + 1]);
        Assert.DoesNotContain("--yolo", arguments);
        Assert.DoesNotContain("--allow-all-paths", arguments);
        Assert.DoesNotContain("old-prompt", arguments);
        Assert.Contains("gpt-5.4", arguments);
        Assert.Equal(inheritedXdgConfigHome, GetEnvironmentValue(startInfo, "XDG_CONFIG_HOME"));
    }

    [Fact]
    public void BuildStartInfo_CopilotWriteForcesAutopilotPromptMode()
    {
        var run = Run(
            LLM.Copilot,
            JobExecutionMode.LiveWrite,
            environmentArgs: "--plan --allow-all --prompt old-prompt --model claude-sonnet-4.5");

        var arguments = JobRunExecutor.BuildStartInfo(run, "copilot", _directory).ArgumentList.ToArray();

        Assert.Equal("autopilot", arguments[Array.IndexOf(arguments, "--mode") + 1]);
        Assert.Equal(1, arguments.Count(argument => argument == "--allow-all-tools"));
        Assert.DoesNotContain("--plan", arguments);
        Assert.DoesNotContain("--allow-all", arguments);
        Assert.DoesNotContain("old-prompt", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--prompt"));
    }

    [Fact]
    public void BuildStartInfo_OpenCodeReviewUsesRunModeAndEnvironmentConfigRoot()
    {
        var run = Run(
            LLM.OpenCode,
            JobExecutionMode.Review,
            environmentArgs: "--auto --agent build --prompt=old-prompt --dir C:\\outside --model openai/gpt-5.1-codex --pure",
            environmentName: "open-code-review",
            environmentPath: _directory);

        var startInfo = JobRunExecutor.BuildStartInfo(run, "opencode", _directory);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("run", arguments[0]);
        Assert.Equal("plan", arguments[Array.IndexOf(arguments, "--agent") + 1]);
        Assert.DoesNotContain("--auto", arguments);
        Assert.DoesNotContain("old-prompt", arguments);
        Assert.DoesNotContain("--dir", arguments);
        Assert.DoesNotContain("C:\\outside", arguments);
        Assert.Contains("openai/gpt-5.1-codex", arguments);
        Assert.Contains("--pure", arguments);
        Assert.Equal("json", arguments[Array.IndexOf(arguments, "--format") + 1]);
        Assert.Equal("Review this repository.", arguments[^1]);
        Assert.Equal(_directory, startInfo.Environment["XDG_CONFIG_HOME"]);
    }

    [Theory]
    [InlineData(LLM.OpenCode, JobExecutionMode.LiveWrite, "--agent plan --auto --model anthropic/claude-sonnet-4-5")]
    [InlineData(LLM.Glm52, JobExecutionMode.IsolatedWrite, "--agent=plan --auto")]
    [InlineData(LLM.KimiK3, JobExecutionMode.LiveWrite, "--agent plan")]
    public void BuildStartInfo_OpenCodeFamilyWriteOverridesSavedAgentAndForcesAutoApproval(
        LLM llm,
        JobExecutionMode executionMode,
        string environmentArgs)
    {
        var run = Run(
            llm,
            executionMode,
            environmentArgs: environmentArgs);

        var arguments = JobRunExecutor.BuildStartInfo(run, "opencode", _directory).ArgumentList.ToArray();

        Assert.Equal("build", arguments[Array.IndexOf(arguments, "--agent") + 1]);
        Assert.Equal(1, arguments.Count(argument => argument == "--agent"));
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--agent=", StringComparison.Ordinal));
        Assert.DoesNotContain("plan", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--auto"));
    }

    [Theory]
    [InlineData(LLM.Glm52, "--model=zai/glm-5.2")]
    [InlineData(LLM.KimiK3, "--model=moonshotai/kimi-k3")]
    public void BuildStartInfo_OpenCodePseudoCliBaseLaunchPinsModel(LLM llm, string modelArgument)
    {
        var run = Run(llm, JobExecutionMode.Review, environmentArgs: "");

        var arguments = JobRunExecutor.BuildStartInfo(run, "opencode", _directory).ArgumentList.ToArray();

        Assert.Contains(modelArgument, arguments);
        Assert.Equal(1, arguments.Count(argument => argument.StartsWith("--model", StringComparison.Ordinal)));
    }

    [Fact]
    public void BuildStartInfo_CustomPseudoCliDoesNotDuplicatePinnedModel()
    {
        var run = Run(
            LLM.Glm52,
            JobExecutionMode.Review,
            environmentArgs: "--model wrong/provider-model",
            environmentName: "glm-review",
            environmentPath: _directory);

        var arguments = JobRunExecutor.BuildStartInfo(run, "opencode", _directory).ArgumentList.ToArray();

        Assert.Equal(1, arguments.Count(argument => argument.StartsWith("--model", StringComparison.Ordinal)));
        Assert.Contains("--model=zai/glm-5.2", arguments);
        Assert.DoesNotContain("wrong/provider-model", arguments);
        Assert.Equal(_directory, JobRunExecutor.BuildStartInfo(run, "opencode", _directory).Environment["XDG_CONFIG_HOME"]);
    }

    [Theory]
    [InlineData(LLM.Copilot, "{\"type\":\"assistant.message\",\"data\":{\"content\":\"Copilot result\"}}", "Copilot result")]
    [InlineData(LLM.OpenCode, "{\"type\":\"text\",\"part\":{\"text\":\"OpenCode result\"}}", "OpenCode result")]
    [InlineData(LLM.Glm52, "{\"type\":\"text\",\"text\":\"GLM result\"}", "GLM result")]
    public void TryExtractResult_ReadsAdditionalProviderJson(LLM llm, string line, string expected)
    {
        Assert.Equal(expected, JobRunExecutor.TryExtractResult(llm, line));
    }

    [Fact]
    public void BuildStartInfo_RejectsTraversalInLegacyEnvironmentNameWhenPathIsMissing()
    {
        var run = Run(
            LLM.Codex,
            JobExecutionMode.Review,
            environmentArgs: "",
            environmentName: "../outside",
            environmentPath: null);

        Assert.Throws<ArgumentException>(() =>
            JobRunExecutor.BuildStartInfo(run, "codex", _directory));
    }

    [Fact]
    public async Task ExecuteAsync_KillsRunningProcessWhenCancellationIsRequested()
    {
        var markerPath = Path.Combine(_directory, "worker-started.marker");
        var executablePath = await CreateLongRunningExecutableAsync(markerPath);
        var job = await _store.CreateJobAsync(
            new CreateJobRequest(
                "Cancellation",
                _directory,
                LLM.Codex,
                EnvironmentId: null,
                Prompt: "Wait for cancellation.",
                JobExecutionMode.Review,
                TimeoutMinutes: 30,
                Enabled: true,
                Triggers: []),
            executablePath,
            TestContext.Current.CancellationToken);
        var runId = await _store.EnqueueManualRunAsync(
            job.Id,
            cancellationToken: TestContext.Current.CancellationToken);
        var run = await _store.ClaimNextRunAsync(
            "worker",
            Environment.ProcessId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(runId);
        Assert.NotNull(run);

        var executor = new JobRunExecutor(_store, new MissingExecutableResolver(), new JobWorkspaceService());
        var execution = executor.ExecuteAsync(run, TestContext.Current.CancellationToken);
        try
        {
            await WaitForFileAsync(markerPath, TestContext.Current.CancellationToken);
            Assert.True(await _store.RequestCancelAsync(run.Id, TestContext.Current.CancellationToken));
            await execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var completed = await _store.GetRunAsync(run.Id, TestContext.Current.CancellationToken);
            Assert.NotNull(completed);
            Assert.Equal(JobRunStatus.Cancelled, completed.Status);
            Assert.Equal("Job was cancelled.", completed.ErrorMessage);
            Assert.True(completed.CancelRequested);
        }
        finally
        {
            // Kill the fake CLI even if an assertion above fails, so Dispose's recursive delete does
            // not throw over a locked file and mask the real failure.
            await _store.RequestCancelAsync(run.Id, CancellationToken.None);
            try { await execution.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
            catch { /* best-effort drain; Dispose is the backstop */ }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a lingering fake CLI can still hold a file handle.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private async Task<string> CreateLongRunningExecutableAsync(string markerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // A PowerShell shim (not .cmd): the executor only launches CLIs via pwsh -File now, so
            // this mirrors how a real npm-installed CLI's .ps1 shim is invoked.
            var path = Path.Combine(_directory, "fake-codex.ps1");
            var escapedMarker = markerPath.Replace("'", "''", StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                path,
                $"Set-Content -LiteralPath '{escapedMarker}' -Value ''\r\nStart-Sleep -Seconds 30\r\n",
                TestContext.Current.CancellationToken);
            return path;
        }

        var scriptPath = Path.Combine(_directory, "fake-codex");
        await File.WriteAllTextAsync(
            scriptPath,
            $"#!/bin/sh\ntouch '{markerPath.Replace("'", "'\\''", StringComparison.Ordinal)}'\nsleep 30\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(50, cancellationToken);
        Assert.True(File.Exists(path), "The fake LLM process did not start within five seconds.");
    }

    private static string? GetEnvironmentValue(System.Diagnostics.ProcessStartInfo startInfo, string name) =>
        startInfo.Environment.TryGetValue(name, out var value) ? value : null;

    private JobRunRecord Run(
        LLM llm,
        JobExecutionMode executionMode,
        string environmentArgs,
        string? environmentName = null,
        string? environmentPath = null,
        string? triggerContextJson = null) => new(
            Id: "run",
            JobId: 1,
            TriggerId: null,
            TriggerKind: JobTriggerKind.Manual,
            TriggerKey: "manual:run",
            Status: JobRunStatus.Running,
            JobName: "Test",
            ProjectPath: _directory,
            Llm: llm,
            EnvironmentId: environmentName is null ? null : 1,
            EnvironmentName: environmentName,
            EnvironmentPath: environmentPath,
            EnvironmentArgs: environmentArgs,
            Prompt: "Review this repository.",
            ExecutionMode: executionMode,
            TimeoutMinutes: 30,
            ExecutablePath: null,
            TriggerContextJson: triggerContextJson,
            QueuedUtc: DateTime.UtcNow,
            StartedUtc: DateTime.UtcNow,
            EndedUtc: null,
            ExitCode: null,
            ResultText: null,
            ErrorMessage: null,
            WorkspacePath: null,
            CancelRequested: false,
            OwnerInstanceId: "worker",
            OwnerProcessId: Environment.ProcessId);

    private static void CreateEnvironmentTable(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Environments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomName TEXT NOT NULL,
                LLM INTEGER NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                CustomArgs TEXT NOT NULL DEFAULT '',
                CustomPrompt TEXT NOT NULL DEFAULT '',
                CreatedUTC TEXT NOT NULL,
                LastUsedUTC TEXT NOT NULL,
                UNIQUE(CustomName, LLM)
            );
            """;
        command.ExecuteNonQuery();
    }

    private sealed class MissingExecutableResolver : IJobExecutableResolver
    {
        public string? Resolve(LLM llm) => null;
    }
}
