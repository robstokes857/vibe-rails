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
            environmentArgs: "--sandbox danger-full-access --json --model gpt-5",
            environmentName: "nightly",
            environmentPath: _directory,
            triggerContextJson: "{\"commit\":\"abc\"}");

        var startInfo = JobRunExecutor.BuildStartInfo(run, "codex", _directory);
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal("exec", arguments[0]);
        Assert.Contains("--model", arguments);
        Assert.Contains("gpt-5", arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--json"));
        Assert.Contains("--ephemeral", arguments);
        Assert.Equal("read-only", arguments[Array.IndexOf(arguments, "--sandbox") + 1]);
        Assert.Equal("never", arguments[Array.IndexOf(arguments, "--ask-for-approval") + 1]);
        Assert.DoesNotContain("danger-full-access", arguments);
        Assert.Contains("VibeRails trigger metadata follows", arguments[^1]);
        Assert.Equal(Path.Combine(_directory, "codex"), startInfo.Environment["CODEX_HOME"]);
    }

    [Fact]
    public void BuildStartInfo_ClaudeForcesPrintModeAndWritePermissions()
    {
        var run = Run(
            LLM.Claude,
            JobExecutionMode.LiveWrite,
            environmentArgs: "--permission-mode bypassPermissions --output-format text --model opus --dangerously-skip-permissions",
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
        Assert.Equal(Path.Combine(_directory, "claude"), startInfo.Environment["CLAUDE_CONFIG_DIR"]);
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
        await WaitForFileAsync(markerPath, TestContext.Current.CancellationToken);
        Assert.True(await _store.RequestCancelAsync(run.Id, TestContext.Current.CancellationToken));
        await execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var completed = await _store.GetRunAsync(run.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(completed);
        Assert.Equal(JobRunStatus.Cancelled, completed.Status);
        Assert.Equal("Job was cancelled.", completed.ErrorMessage);
        Assert.True(completed.CancelRequested);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> CreateLongRunningExecutableAsync(string markerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(_directory, "fake-codex.cmd");
            await File.WriteAllTextAsync(
                path,
                $"@echo off\r\ntype nul > \"{markerPath}\"\r\nping 127.0.0.1 -n 31 > nul\r\n",
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
