using Microsoft.Data.Sqlite;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viberails_jobs_{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly JobStore _store;

    public JobStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "state.db");
        _connectionString = $"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared";
        CreateEnvironmentTable();
        _store = new JobStore(_connectionString);
    }

    [Fact]
    public async Task GetJobsAsync_LoadsTriggersForEveryJob()
    {
        await CreateJobAsync("One", [new JobTriggerRequest(JobTriggerKind.Vca)]);
        await CreateJobAsync("Two", [new JobTriggerRequest(JobTriggerKind.Commit)]);

        var jobs = await _store.GetJobsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job => Assert.Single(job.Triggers));
        Assert.Contains(jobs, job => job.Triggers[0].Kind == JobTriggerKind.Vca);
        Assert.Contains(jobs, job => job.Triggers[0].Kind == JobTriggerKind.Commit);
    }

    [Fact]
    public async Task EnqueueEventRunsAsync_DeduplicatesSameEventButQueuesDistinctEvents()
    {
        await CreateJobAsync("VCA", [new JobTriggerRequest(JobTriggerKind.Vca)]);

        var first = await _store.EnqueueEventRunsAsync(
            _directory, JobTriggerKind.Vca, "preflight-1", contextJson: null,
            cancellationToken: TestContext.Current.CancellationToken);
        var duplicate = await _store.EnqueueEventRunsAsync(
            _directory, JobTriggerKind.Vca, "preflight-1", contextJson: null,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await _store.EnqueueEventRunsAsync(
            _directory, JobTriggerKind.Vca, "preflight-2", contextJson: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(first);
        Assert.Empty(duplicate);
        Assert.Single(second);
        Assert.Equal(2, (await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken)).Count);
    }

    [Theory]
    [InlineData(GitPreflightStepStatus.Passed, "passed")]
    [InlineData(GitPreflightStepStatus.Blocked, "blocked")]
    public async Task AutomatedWorkflowStep_QueuesRealVcaOutcome(
        GitPreflightStepStatus vcaStatus,
        string expectedOutcome)
    {
        await CreateJobAsync("VCA", [new JobTriggerRequest(JobTriggerKind.Vca)]);
        var output = new List<string>();
        var request = PreflightRequest(enqueueAutomatedJobs: true);
        var vcaResult = new GitPreflightStepResult(
            VcaPreflightStep.Id,
            "VCA",
            vcaStatus,
            "VCA result",
            [],
            DurationMs: 0,
            Blocking: true);

        var result = await new AutomatedWorkflowsPreflightStep(_store).ExecuteAsync(
            new GitPreflightStepContext(
                $"run-{expectedOutcome}",
                request,
                Snapshot(),
                (message, _, _) =>
                {
                    output.Add(message);
                    return ValueTask.CompletedTask;
                },
                CompletedSteps: [vcaResult]),
            TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Passed, result.Status);
        Assert.Contains(output, message => message.Contains(expectedOutcome, StringComparison.Ordinal));
        var run = Assert.Single(await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JobTriggerKind.Vca, run.TriggerKind);
        using var context = JsonDocument.Parse(Assert.IsType<string>(run.TriggerContextJson));
        Assert.Equal(expectedOutcome, context.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("changed.cs", context.RootElement.GetProperty("stagedFiles").GetString());
    }

    [Fact]
    public async Task AutomatedWorkflowStep_PreviewNeverQueuesConfiguredJob()
    {
        await CreateJobAsync("VCA", [new JobTriggerRequest(JobTriggerKind.Vca)]);

        var result = await new AutomatedWorkflowsPreflightStep(_store).ExecuteAsync(
            new GitPreflightStepContext(
                "preview",
                PreflightRequest(enqueueAutomatedJobs: false),
                Snapshot(),
                (_, _, _) => ValueTask.CompletedTask),
            TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Empty(await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostCommitProcessHost_DeduplicatesCommitHash()
    {
        var init = await GitProcessRunner.RunAsync(
            ["init"],
            _directory,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, init.ExitCode);
        await CreateJobAsync("Commit", [new JobTriggerRequest(JobTriggerKind.Commit)]);
        string[] arguments =
        [
            "--job-trigger", "post-commit",
            "--workdir", _directory,
            "--commit", "ABC123"
        ];

        Assert.Equal(0, await JobTriggerProcessHost.RunAsync(
            arguments, _store, TestContext.Current.CancellationToken));
        Assert.Equal(0, await JobTriggerProcessHost.RunAsync(
            arguments, _store, TestContext.Current.CancellationToken));

        var run = Assert.Single(await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JobTriggerKind.Commit, run.TriggerKind);
        Assert.EndsWith(":abc123", run.TriggerKey, StringComparison.Ordinal);
        using var context = JsonDocument.Parse(Assert.IsType<string>(run.TriggerContextJson));
        Assert.Equal("ABC123", context.RootElement.GetProperty("commit").GetString());
    }

    [Fact]
    public async Task EnqueueDueSchedulesAsync_CoalescesMissedTimerToOneRunAndAdvancesFromNow()
    {
        var job = await CreateJobAsync("Timer", [new JobTriggerRequest(
            JobTriggerKind.Schedule,
            JobScheduleKind.Interval,
            IntervalMinutes: 5)]);
        var overdue = DateTime.UtcNow.AddHours(-2);
        await SetNextRunAsync(job.Triggers[0].Id, overdue);
        var now = DateTime.UtcNow;

        Assert.Equal(1, await _store.EnqueueDueSchedulesAsync(now, TestContext.Current.CancellationToken));
        Assert.Equal(0, await _store.EnqueueDueSchedulesAsync(now, TestContext.Current.CancellationToken));

        var refreshed = await _store.GetJobAsync(job.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.Triggers[0].NextRunUtc > now);
        Assert.Single(await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClaimNextRunAsync_PreventsSameJobOverlap()
    {
        var job = await CreateJobAsync("Manual", []);
        await _store.EnqueueManualRunAsync(job.Id, cancellationToken: TestContext.Current.CancellationToken);
        await _store.EnqueueManualRunAsync(job.Id, cancellationToken: TestContext.Current.CancellationToken);

        var first = await _store.ClaimNextRunAsync("worker", 42, TestContext.Current.CancellationToken);
        var overlapping = await _store.ClaimNextRunAsync("worker", 42, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Null(overlapping);
        await _store.CompleteRunAsync(first.Id, JobRunStatus.Succeeded, 0, "done", null, TestContext.Current.CancellationToken);
        Assert.NotNull(await _store.ClaimNextRunAsync("worker", 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManualRun_CanQueueForDisabledJob()
    {
        var job = await CreateJobAsync("Disabled", [], enabled: false);

        var runId = await _store.EnqueueManualRunAsync(job.Id, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(runId);
        Assert.NotNull(await _store.ClaimNextRunAsync("worker", 42, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retry_CopiesCompletedRunSnapshot()
    {
        var job = await CreateJobAsync("Retry", []);
        var runId = await _store.EnqueueManualRunAsync(job.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(runId);
        var claimed = await _store.ClaimNextRunAsync("worker", 42, TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        await _store.CompleteRunAsync(claimed.Id, JobRunStatus.Failed, 7, null, "failed", TestContext.Current.CancellationToken);

        var retryId = await _store.EnqueueRetryAsync(claimed.Id, TestContext.Current.CancellationToken);
        var retry = await _store.GetRunAsync(retryId!, TestContext.Current.CancellationToken);

        Assert.NotNull(retry);
        Assert.Equal(JobTriggerKind.Manual, retry.TriggerKind);
        Assert.Equal(claimed.Prompt, retry.Prompt);
        Assert.Equal(JobRunStatus.Queued, retry.Status);
    }

    [Fact]
    public async Task WorkerLease_RejectsSecondFreshOwnerAndAcceptsStaleReplacement()
    {
        var now = DateTime.UtcNow;
        var first = new JobWorkerLeaseRecord("first", 1, "1.0", now, now);
        var second = new JobWorkerLeaseRecord("second", 2, "1.0", now, now);

        Assert.True(await _store.TryAcquireWorkerLeaseAsync(first, now.AddSeconds(-30), TestContext.Current.CancellationToken));
        Assert.False(await _store.TryAcquireWorkerLeaseAsync(second, now.AddSeconds(-30), TestContext.Current.CancellationToken));
        Assert.True(await _store.TryAcquireWorkerLeaseAsync(second, now.AddSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal("second", (await _store.GetWorkerLeaseAsync(TestContext.Current.CancellationToken))?.InstanceId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task<JobDefinitionRecord> CreateJobAsync(
        string name,
        List<JobTriggerRequest> triggers,
        bool enabled = true)
    {
        return await _store.CreateJobAsync(new CreateJobRequest(
            name,
            _directory,
            LLM.Codex,
            EnvironmentId: null,
            Prompt: "Review the repository.",
            JobExecutionMode.Review,
            TimeoutMinutes: 30,
            enabled,
            triggers),
            executablePath: "codex",
            TestContext.Current.CancellationToken);
    }

    private GitPreflightRequest PreflightRequest(bool enqueueAutomatedJobs) => new(
        _directory,
        new VcaHookInvocation(
            VcaHookKind.PreCommit,
            CommitMessagePath: null,
            WorkingDirectory: _directory,
            DemoUi: false,
            DemoDuration: TimeSpan.Zero,
            PromptForAcknowledgment: false),
        EnqueueAutomatedJobs: enqueueAutomatedJobs);

    private GitStagedSnapshot Snapshot() => new(
        _directory,
        [new GitStagedFileSnapshot(
            "changed.cs",
            Path.Combine(_directory, "changed.cs"),
            GitStagedChangeKind.Modified,
            ExistsInIndex: true,
            IsBinary: false,
            ChangedLineCount: 1,
            Content: "class Changed { }")],
        AgentFiles: []);

    private void CreateEnvironmentTable()
    {
        using var connection = new SqliteConnection(_connectionString);
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

    private async Task SetNextRunAsync(long triggerId, DateTime value)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE JobTriggers SET NextRunUTC = $value WHERE Id = $id;";
        command.Parameters.AddWithValue("$value", value.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$id", triggerId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
