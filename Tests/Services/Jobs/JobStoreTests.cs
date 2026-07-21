using Microsoft.Data.Sqlite;
using Moq;
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
    public async Task TryDeleteEnvironmentIfUnusedAsync_RefusesReferencedEnvironment()
    {
        var environmentId = await InsertEnvironmentAsync("job-environment");
        var stageCalls = 0;
        var job = await _store.CreateJobAsync(
            new CreateJobRequest(
                "Uses environment",
                _directory,
                LLM.Codex,
                environmentId,
                "Review the repository.",
                JobExecutionMode.Review,
                TimeoutMinutes: 30,
                Enabled: false,
                Triggers: []),
            executablePath: "codex",
            TestContext.Current.CancellationToken);

        Assert.False(await _store.TryDeleteEnvironmentIfUnusedAsync(
            environmentId,
            () => stageCalls++,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, stageCalls);

        await _store.SoftDeleteJobAsync(job.Id, TestContext.Current.CancellationToken);
        Assert.True(await _store.TryDeleteEnvironmentIfUnusedAsync(
            environmentId,
            () => stageCalls++,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, stageCalls);
    }

    [Fact]
    public async Task TryDeleteEnvironmentIfUnusedAsync_StaleIdNeverInvokesFilesystemStage()
    {
        var oldEnvironmentId = await InsertEnvironmentAsync("recreated-environment");
        var firstStageCalls = 0;
        Assert.True(await _store.TryDeleteEnvironmentIfUnusedAsync(
            oldEnvironmentId,
            () => firstStageCalls++,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, firstStageCalls);

        var replacementEnvironmentId = await InsertEnvironmentAsync("recreated-environment");
        Assert.NotEqual(oldEnvironmentId, replacementEnvironmentId);

        var staleStageCalls = 0;
        Assert.False(await _store.TryDeleteEnvironmentIfUnusedAsync(
            oldEnvironmentId,
            () => staleStageCalls++,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, staleStageCalls);
    }

    [Fact]
    public async Task TryDeleteEnvironmentIfUnusedAsync_CallbackFailureRollsBackRowDeletion()
    {
        var environmentId = await InsertEnvironmentAsync("rollback-environment");

        await Assert.ThrowsAsync<IOException>(() =>
            _store.TryDeleteEnvironmentIfUnusedAsync(
                environmentId,
                () => throw new IOException("Simulated filesystem staging failure."),
                TestContext.Current.CancellationToken));

        var retryStageCalls = 0;
        Assert.True(await _store.TryDeleteEnvironmentIfUnusedAsync(
            environmentId,
            () => retryStageCalls++,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, retryStageCalls);
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
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["init"], _directory, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["config", "user.email", "tests@viberails.local"], _directory,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["config", "user.name", "VibeRails Tests"], _directory,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "changed.cs"),
            "class Original { }\n",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["add", "changed.cs"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["commit", "-m", "initial"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "changed.cs"),
            "class Staged { }\n",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["add", "changed.cs"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);

        await CreateJobAsync("VCA", [new JobTriggerRequest(JobTriggerKind.Vca)]);
        var output = new List<string>();
        var request = PreflightRequest(enqueueAutomatedJobs: true);
        var stagedSnapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _directory,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "changed.cs"),
            "class LaterIndex { }\n",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["add", "changed.cs"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);
        var vcaResult = new GitPreflightStepResult(
            VcaPreflightStep.Id,
            "VCA",
            vcaStatus,
            "VCA result",
            [],
            DurationMs: 0,
            Blocking: true);

        var workspaceService = new JobWorkspaceService(Path.Combine(_directory, "job-artifacts"));
        var result = await new AutomatedWorkflowsPreflightStep(_store, workspaceService).ExecuteAsync(
            new GitPreflightStepContext(
                $"run-{expectedOutcome}",
                request,
                stagedSnapshot,
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
        Assert.True(Guid.TryParseExact(
            context.RootElement.GetProperty(JobWorkspaceService.StagedSnapshotIdContextKey).GetString(),
            "N",
            out _));
        Assert.Matches(
            "^[0-9a-f]{40,64}$",
            context.RootElement.GetProperty(JobWorkspaceService.StagedSnapshotBaseCommitContextKey).GetString());
        Assert.Matches(
            "^[0-9a-f]{40,64}$",
            context.RootElement.GetProperty(JobWorkspaceService.StagedSnapshotBaseTreeContextKey).GetString());
        Assert.Matches(
            "^[0-9a-f]{40,64}$",
            context.RootElement.GetProperty(JobWorkspaceService.StagedSnapshotTreeContextKey).GetString());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_directory, "job-artifacts"), "*.staged.patch"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_directory, "job-artifacts"), "*.vca-snapshot.patch"));

        var workspace = await workspaceService.PrepareAsync(run, TestContext.Current.CancellationToken);
        try
        {
            var content = await File.ReadAllTextAsync(
                Path.Combine(workspace, "changed.cs"),
                TestContext.Current.CancellationToken);
            Assert.Equal("class Staged { }\n", content.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            workspaceService.Cleanup(run, workspace);
        }
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
    public async Task AutomatedWorkflowStep_EnqueueFailureReleasesArtifactForDbAwareCleanup()
    {
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["init"], _directory, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["config", "user.email", "tests@viberails.local"], _directory,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["config", "user.name", "VibeRails Tests"], _directory,
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "changed.cs"),
            "class Original { }\n",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["add", "changed.cs"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["commit", "-m", "initial"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "changed.cs"),
            "class Staged { }\n",
            TestContext.Current.CancellationToken);
        Assert.Equal(0, (await GitProcessRunner.RunAsync(
            ["add", "changed.cs"], _directory, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken)).ExitCode);
        var snapshot = await new GitStagedSnapshotProvider().CaptureAsync(
            _directory,
            TestContext.Current.CancellationToken);
        var failingStore = new Mock<IJobStore>();
        failingStore
            .Setup(store => store.EnqueueEventRunsAsync(
                It.IsAny<string>(),
                It.IsAny<JobTriggerKind>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("simulated enqueue failure"));
        var artifacts = Path.Combine(_directory, "failed-enqueue-artifacts");
        var workspaceService = new JobWorkspaceService(artifacts);

        var result = await new AutomatedWorkflowsPreflightStep(
            failingStore.Object,
            workspaceService).ExecuteAsync(
                new GitPreflightStepContext(
                    "failed-enqueue",
                    PreflightRequest(enqueueAutomatedJobs: true),
                    snapshot,
                    (_, _, _) => ValueTask.CompletedTask),
                TestContext.Current.CancellationToken);

        Assert.Equal(GitPreflightStepStatus.Warning, result.Status);
        Assert.Single(Directory.EnumerateFiles(artifacts, "*.vca-snapshot.patch"));
        Assert.Empty(Directory.EnumerateFiles(artifacts, "*.vca-snapshot.publishing"));
        using var sweepLease = Assert.IsType<JobWorkspaceService.ArtifactSweepLease>(
            workspaceService.TryAcquireArtifactSweepLease());
        workspaceService.SweepSnapshotArtifacts(
            sweepLease,
            new JobSnapshotArtifactReferences(
                new HashSet<string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal)));
        Assert.Empty(Directory.EnumerateFiles(artifacts, "*.vca-snapshot.patch"));
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
        var run = Assert.Single(await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken));
        using var context = JsonDocument.Parse(Assert.IsType<string>(run.TriggerContextJson));
        Assert.Equal(
            overdue.ToUniversalTime(),
            context.RootElement.GetProperty("scheduledUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(
            now.ToUniversalTime(),
            context.RootElement.GetProperty("coalescedAtUtc").GetDateTime().ToUniversalTime());
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
        await _store.CompleteRunAsync(first.Id, JobRunStatus.Succeeded, 0, "done", null, cancellationToken: TestContext.Current.CancellationToken);
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
        var runId = await _store.EnqueueManualRunAsync(
            job.Id,
            "{\"version\":\"original\"}",
            TestContext.Current.CancellationToken);
        Assert.NotNull(runId);
        var claimed = await _store.ClaimNextRunAsync("worker", 42, TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        await _store.CompleteRunAsync(claimed.Id, JobRunStatus.Failed, 7, null, "failed", cancellationToken: TestContext.Current.CancellationToken);

        var retryId = await _store.EnqueueRetryAsync(
            claimed.Id,
            "{\"version\":\"replacement\"}",
            cancellationToken: TestContext.Current.CancellationToken);
        var retry = await _store.GetRunAsync(retryId!, TestContext.Current.CancellationToken);

        Assert.NotNull(retry);
        Assert.Equal(JobTriggerKind.Manual, retry.TriggerKind);
        Assert.Equal(claimed.Prompt, retry.Prompt);
        Assert.Equal(JobRunStatus.Queued, retry.Status);
        using var retryContext = JsonDocument.Parse(retry.TriggerContextJson!);
        Assert.Equal("replacement", retryContext.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task ActiveSnapshotArtifactReferences_TracksOnlyQueuedAndRunningRuns()
    {
        var job = await CreateJobAsync("Snapshots", []);
        var snapshotId = Guid.NewGuid().ToString("N");
        var context = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [JobWorkspaceService.StagedSnapshotIdContextKey] = snapshotId
        });
        var runId = await _store.EnqueueManualRunAsync(
            job.Id,
            context,
            TestContext.Current.CancellationToken);
        Assert.NotNull(runId);

        var queued = await _store.GetActiveSnapshotArtifactReferencesAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(runId, queued.ActiveRunIds);
        Assert.Contains(snapshotId, queued.ActiveSnapshotIds);

        var claimed = await _store.ClaimNextRunAsync(
            "worker",
            42,
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        var running = await _store.GetActiveSnapshotArtifactReferencesAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(runId, running.ActiveRunIds);
        Assert.Contains(snapshotId, running.ActiveSnapshotIds);

        await _store.CompleteRunAsync(
            runId!,
            JobRunStatus.Succeeded,
            0,
            "done",
            null,
            cancellationToken: TestContext.Current.CancellationToken);
        var completed = await _store.GetActiveSnapshotArtifactReferencesAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(runId, completed.ActiveRunIds);
        Assert.DoesNotContain(snapshotId, completed.ActiveSnapshotIds);
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
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_directory, recursive: true);
        }
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

    private async Task<int> InsertEnvironmentAsync(string name)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Environments
                (CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC)
            VALUES
                ($name, $llm, '', '', '', $now, $now)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$llm", (int)LLM.Codex);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
