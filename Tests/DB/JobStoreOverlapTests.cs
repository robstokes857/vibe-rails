using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.DB;

/// <summary>
/// Pins the self-overlap guard and the launch claim against real SQLite.
///
/// These matter more than usual: a Job timeout is opt-in, so a run can legitimately stay open for
/// hours. The overlap guard is therefore the primary thing standing between a slow job on a short
/// schedule and a screen full of terminal windows. It lives in the run-insert statement precisely so
/// every trigger path (schedule, commit, manual, retry) inherits it.
/// </summary>
public sealed class JobStoreOverlapTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"viberails-jobstore-overlap-test-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;

    public JobStoreOverlapTests()
    {
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate";
    }

    public void Dispose()
    {
        // Release pooled connections before deleting, or the file handle survives on Windows.
        // Scoped to this class's connection string: a process-wide ClearAllPools() disposes handles
        // out from under DB test classes running in parallel.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        try { File.Delete(_dbPath); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task EnqueueManualRunAsync_RefusesASecondRun_WhileTheFirstIsStillQueued()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        var first = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        var second = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task EnqueueManualRunAsync_RefusesASecondRun_WhileTheFirstIsRunning()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        Assert.True(await store.StartRunAsync(first!, 4242, cancellationToken));

        var second = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        Assert.Null(second);
    }

    [Fact]
    public async Task EnqueueManualRunAsync_AllowsANewRun_OnceThePreviousOneFinished()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        await store.CompleteRunAsync(first!, JobRunStatus.Succeeded, 0, null, cancellationToken);

        var second = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task CompleteIdleRunAsync_AtomicallyPrefersAPendingUserCancellation()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        Assert.True(await store.StartRunAsync(runId!, 4242, cancellationToken));
        var action = Assert.Single((await store.GetRunAsync(runId!, cancellationToken))!.Actions!);
        Assert.True(await store.StartRunActionAsync(runId!, action.Id, cancellationToken));
        Assert.True(await store.RequestCancelAsync(runId!, cancellationToken));

        var status = await store.CompleteIdleRunAsync(runId!, cancellationToken);
        var run = await store.GetRunAsync(runId!, cancellationToken);

        Assert.Equal(JobRunStatus.Cancelled, status);
        Assert.Equal(JobRunStatus.Cancelled, run!.Status);
        Assert.Equal(3, run.ExitCode);
        Assert.Equal("Automation was cancelled.", run.ErrorMessage);
        var completedAction = Assert.Single(run.Actions!);
        Assert.Equal(JobRunActionStatus.Cancelled, completedAction.Status);
        Assert.Equal(3, completedAction.ExitCode);
        Assert.Equal("Automation was cancelled.", completedAction.ErrorMessage);
        Assert.NotNull(completedAction.EndedUtc);
    }

    [Fact]
    public async Task CompleteIdleRunAsync_SucceedsWhenNoCancellationIsPending()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        Assert.True(await store.StartRunAsync(runId!, 4242, cancellationToken));
        var action = Assert.Single((await store.GetRunAsync(runId!, cancellationToken))!.Actions!);
        Assert.True(await store.StartRunActionAsync(runId!, action.Id, cancellationToken));

        var status = await store.CompleteIdleRunAsync(runId!, cancellationToken);
        var run = await store.GetRunAsync(runId!, cancellationToken);

        Assert.Equal(JobRunStatus.Succeeded, status);
        Assert.Equal(JobRunStatus.Succeeded, run!.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Null(run.ErrorMessage);
        var completedAction = Assert.Single(run.Actions!);
        Assert.Equal(JobRunActionStatus.Succeeded, completedAction.Status);
        Assert.Equal(0, completedAction.ExitCode);
        Assert.NotNull(completedAction.EndedUtc);
    }

    [Theory]
    [InlineData(JobActionKind.CodeQuality, false)]
    [InlineData(JobActionKind.Vca, false)]
    [InlineData(JobActionKind.CodeQuality, true)]
    [InlineData(JobActionKind.Vca, true)]
    public async Task CompleteIdleRunAsync_PreservesFailedCheckWhenLastWorkerIdles(
        JobActionKind checkKind, bool cancelRequested)
    {
        var (store, seedJobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var environmentId = (await store.GetJobAsync(seedJobId, cancellationToken))!.EnvironmentId;
        var job = await store.CreateJobAsync(AnotherJob("Checked review", environmentId) with
        {
            Actions = [new(null, checkKind, Arguments: ["unpushed"]), new(null, JobActionKind.Worker, environmentId)]
        }, cancellationToken);
        var runId = (await store.EnqueueManualRunAsync(job.Id, cancellationToken))!;
        Assert.True(await store.StartRunAsync(runId, 4242, cancellationToken));
        var actions = await store.GetRunActionsAsync(runId, cancellationToken);
        var check = Assert.Single(actions, action => action.Kind == checkKind);
        var worker = Assert.Single(actions, action => action.Kind == JobActionKind.Worker);
        const string failure = "No upstream is configured for unpushed scope.";
        Assert.True(await store.StartRunActionAsync(runId, check.Id, cancellationToken));
        await store.CompleteRunActionAsync(runId, check.Id, JobRunActionStatus.Failed, 23,
            failure, "Saved check evidence", "Scope capture failed", cancellationToken);
        var completedCheck = (await store.GetRunActionsAsync(runId, cancellationToken))[0];
        Assert.True(await store.StartRunActionAsync(runId, worker.Id, cancellationToken));
        if (cancelRequested)
            Assert.True(await store.RequestCancelAsync(runId, cancellationToken));

        // This is the atomic finalizer used when the last Worker's idle shutdown wedges.
        // Exercise it directly without launching a Worker or the process-killing fallback.
        var expected = cancelRequested ? JobRunStatus.Cancelled : JobRunStatus.Failed;
        Assert.Equal(expected, await store.CompleteIdleRunAsync(runId, cancellationToken));
        await store.CompleteRunAsync(runId, JobRunStatus.Succeeded, 0, null, cancellationToken);
        var reopened = new JobStore(_connectionString);
        Assert.Equal(expected, await reopened.CompleteIdleRunAsync(runId, cancellationToken));
        var run = (await reopened.GetRunAsync(runId, cancellationToken))!;

        Assert.Equal(expected, run.Status);
        Assert.Equal(cancelRequested ? JobRunOutcome.ToExitCode(JobRunStatus.Cancelled) : 23, run.ExitCode);
        Assert.Equal(cancelRequested ? JobRunOutcome.CancelledMessage : failure, run.ErrorMessage);
        Assert.NotNull(run.EndedUtc);
        var persistedCheck = Assert.Single(run.Actions!, action => action.Id == check.Id);
        Assert.Equal(JobRunActionStatus.Failed, persistedCheck.Status);
        Assert.Equal(23, persistedCheck.ExitCode);
        Assert.Equal(failure, persistedCheck.ErrorMessage);
        Assert.Equal(completedCheck.EndedUtc, persistedCheck.EndedUtc);
        Assert.Equal("Saved check evidence", persistedCheck.StandardOutput);
        Assert.Equal("Scope capture failed", persistedCheck.StandardError);
        var completedWorker = Assert.Single(run.Actions!, action => action.Id == worker.Id);
        Assert.Equal(cancelRequested ? JobRunActionStatus.Cancelled : JobRunActionStatus.Succeeded, completedWorker.Status);
        Assert.Equal(cancelRequested ? JobRunOutcome.ToExitCode(JobRunStatus.Cancelled) : 0, completedWorker.ExitCode);
        Assert.NotNull(completedWorker.EndedUtc);
    }

    [Fact]
    public async Task EnqueueDueSchedulesAsync_DoesNotStackRuns_WhenThePreviousOccurrenceIsStillActive()
    {
        // The failure this prevents: a 5-minute schedule on a job whose agent runs for an hour.
        var (store, jobId) = await SeedJobAsync(intervalMinutes: 5);
        var cancellationToken = TestContext.Current.CancellationToken;

        // Force the trigger due, twice over, without waiting five real minutes.
        var firstBatch = await store.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(10), cancellationToken);
        var secondBatch = await store.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(20), cancellationToken);

        Assert.Single(firstBatch);
        Assert.Empty(secondBatch);
    }

    [Fact]
    public async Task TryClaimLaunchAsync_LetsExactlyOneCallerSpawnTheTerminal()
    {
        // A scheduler lease handoff, or two windows of one project, can leave two callers looking
        // at the same queued run.
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        var winner = await store.TryClaimLaunchAsync(runId!, 3, cancellationToken);
        var loser = await store.TryClaimLaunchAsync(runId!, 3, cancellationToken);

        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.Claimed, 1), winner);
        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.NotLaunchable, 1), loser);
    }

    [Fact]
    public async Task TryClaimLaunchAsync_CountsClaimedAndRunningTerminalsAgainstTheCapInTheClaimItself()
    {
        // VIBE-2 review: every open root launches its own project's Board runs now, so the count
        // and the claim must be one transaction, and a claimed run whose terminal is still
        // starting must already hold its slot. Three jobs, a cap of two.
        var (store, first) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var environmentId = (await store.GetJobAsync(first, cancellationToken))!.EnvironmentId;
        var second = (await store.CreateJobAsync(AnotherJob("Second review", environmentId), cancellationToken)).Id;
        var third = (await store.CreateJobAsync(AnotherJob("Third review", environmentId), cancellationToken)).Id;
        var run1 = (await store.EnqueueManualRunAsync(first, cancellationToken))!;
        var run2 = (await store.EnqueueManualRunAsync(second, cancellationToken))!;
        var run3 = (await store.EnqueueManualRunAsync(third, cancellationToken))!;

        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.Claimed, 1), await store.TryClaimLaunchAsync(run1, 2, cancellationToken));
        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.Claimed, 2), await store.TryClaimLaunchAsync(run2, 2, cancellationToken));
        // Neither claimed run has started, yet both occupy the cap.
        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.CapReached, 2), await store.TryClaimLaunchAsync(run3, 2, cancellationToken));
        Assert.True(await store.StartRunAsync(run1, 4242, cancellationToken));
        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.CapReached, 2), await store.TryClaimLaunchAsync(run3, 2, cancellationToken));
        // A refused claim leaves the run launchable for the next tick.
        Assert.Contains(await store.GetLaunchableRunsAsync(cancellationToken), run => run.Id == run3);

        await store.CompleteRunAsync(run1, JobRunStatus.Succeeded, 0, null, cancellationToken);
        Assert.Equal(new JobLaunchClaim(JobLaunchClaimOutcome.Claimed, 2), await store.TryClaimLaunchAsync(run3, 2, cancellationToken));
    }

    [Fact]
    public async Task ProjectRoots_ArePresentUntilTheyExpireOrAreReleased()
    {
        // A root records its window every scheduler cycle; the lease holder leaves a project's
        // Board runs to any window still present (VIBE-2 review).
        var (store, _) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var project = Path.Combine(Path.GetTempPath(), "app");

        await store.RecordProjectRootAsync("1:a", project + Path.DirectorySeparatorChar, now, TimeSpan.FromMinutes(1), cancellationToken);
        await store.RecordProjectRootAsync("2:b", Path.Combine(Path.GetTempPath(), "other"), now, TimeSpan.FromMinutes(1), cancellationToken);

        Assert.Contains(project, await store.GetOpenProjectRootsAsync(now, cancellationToken));
        Assert.Contains(project, await store.GetOpenProjectRootsAsync(now.AddSeconds(59), cancellationToken));
        Assert.DoesNotContain(project, await store.GetOpenProjectRootsAsync(now.AddSeconds(60), cancellationToken));

        // Refreshing extends the row; releasing removes it at once.
        await store.RecordProjectRootAsync("1:a", project, now.AddSeconds(50), TimeSpan.FromMinutes(1), cancellationToken);
        Assert.Contains(project, await store.GetOpenProjectRootsAsync(now.AddSeconds(100), cancellationToken));
        await store.ReleaseProjectRootAsync("1:a", cancellationToken);
        Assert.DoesNotContain(project, await store.GetOpenProjectRootsAsync(now.AddSeconds(50), cancellationToken));
        Assert.Single(await store.GetOpenProjectRootsAsync(now.AddSeconds(50), cancellationToken));
    }

    [Fact]
    public async Task GetLaunchableRunsAsync_ExcludesRunsWhoseTerminalWasAlreadySpawned()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        Assert.Single(await store.GetLaunchableRunsAsync(cancellationToken));
        await store.TryClaimLaunchAsync(runId!, 3, cancellationToken);
        Assert.Empty(await store.GetLaunchableRunsAsync(cancellationToken));
    }

    [Fact]
    public async Task FailStalledLaunchesAsync_SurfacesALaunchThatNeverStarted()
    {
        // This is the detector for a terminal that was spawned but never appeared — most importantly
        // a native terminal launch failing without starting its run, where nothing else would report
        // a problem and the job would just silently never happen.
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        await store.TryClaimLaunchAsync(runId!, 3, cancellationToken);

        // Zero grace: anything already launched is overdue.
        var failed = await store.FailStalledLaunchesAsync(TimeSpan.Zero, cancellationToken);

        Assert.Equal(1, failed);
        var run = await store.GetRunAsync(runId!, cancellationToken);
        Assert.Equal(JobRunStatus.Failed, run!.Status);
        Assert.Contains("interactive desktop", run.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        var action = Assert.Single(run.Actions!);
        Assert.Equal(JobRunActionStatus.Skipped, action.Status);
        Assert.Contains("interactive desktop", action.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestCancelAsync_CancelsEveryPendingActionBeforeTheRunStarts()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        Assert.True(await store.RequestCancelAsync(runId!, cancellationToken));

        var run = await store.GetRunAsync(runId!, cancellationToken);
        Assert.Equal(JobRunStatus.Cancelled, run!.Status);
        var action = Assert.Single(run.Actions!);
        Assert.Equal(JobRunActionStatus.Cancelled, action.Status);
        Assert.Equal("Cancelled before start.", action.ErrorMessage);
        Assert.NotNull(action.EndedUtc);
    }

    [Fact]
    public async Task FailStalledLaunchesAsync_LeavesARunAloneOnceItHasClaimedItself()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        await store.TryClaimLaunchAsync(runId!, 3, cancellationToken);
        await store.StartRunAsync(runId!, 4242, cancellationToken);

        var failed = await store.FailStalledLaunchesAsync(TimeSpan.Zero, cancellationToken);

        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task CreateJobAsync_RoundTripsAnAbsentTimeoutAsNull()
    {
        // Stored as 0 in SQLite (the column is NOT NULL), surfaced as null — "no time limit".
        var (store, jobId) = await SeedJobAsync();
        var job = await store.GetJobAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Null(job!.TimeoutMinutes);
    }

    [Fact]
    public async Task CreateJobAsync_RoundTripsAnOptedInTimeout()
    {
        var (store, jobId) = await SeedJobAsync(timeoutMinutes: 45);
        var job = await store.GetJobAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal(45, job!.TimeoutMinutes);
    }

    [Fact]
    public async Task LaunchMinimized_RoundTripsAndIsSnapshottedOntoQueuedRuns()
    {
        var (store, jobId) = await SeedJobAsync(launchMinimized: true);
        var cancellationToken = TestContext.Current.CancellationToken;

        var job = await store.GetJobAsync(jobId, cancellationToken);
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        var run = await store.GetRunAsync(runId!, cancellationToken);

        Assert.True(job!.LaunchMinimized);
        Assert.True(run!.LaunchMinimized);
    }

    [Fact]
    public async Task EnqueueAndRetry_PreserveTheOriginalOrderedActionSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();
        var originalActions = new List<JobActionRequest>
        {
            new(
                firstId,
                JobActionKind.Script,
                ScriptPath: "scripts/first.py",
                ScriptRuntime: JobScriptRuntime.Python,
                Arguments: ["--value", "one two"],
                WorkingDirectory: "scripts",
                TimeoutSeconds: 30,
                ApprovedHash: new string('a', 64)),
            new(
                secondId,
                JobActionKind.Script,
                ScriptPath: "scripts/second.sh",
                ScriptRuntime: JobScriptRuntime.Bash,
                Arguments: ["literal;argument"],
                TimeoutSeconds: 45,
                ApprovedHash: new string('b', 64))
        };
        var (store, jobId) = await SeedScriptJobAsync(originalActions);
        var sourceRunId = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        var source = await store.GetRunAsync(sourceRunId!, cancellationToken);
        var sourceActions = source!.Actions!;
        Assert.Equal(["scripts/first.py", "scripts/second.sh"], sourceActions.Select(action => action.ScriptPath));
        Assert.Equal([firstId, secondId], sourceActions.Select(action => action.SourceActionId));
        Assert.Equal([0, 1], sourceActions.Select(action => action.Position));
        Assert.Equal(["one two"], sourceActions[0].Arguments.Skip(1));

        await store.UpdateJobAsync(
            jobId,
            new UpdateJobRequest(
                "Changed definition",
                Path.GetTempPath(),
                LLM.NotSet,
                null,
                string.Empty,
                null,
                true,
                [],
                Actions:
                [
                    new JobActionRequest(
                        Guid.NewGuid().ToString(),
                        JobActionKind.Script,
                        ScriptPath: "scripts/new.py",
                        ScriptRuntime: JobScriptRuntime.Python,
                        ApprovedHash: new string('c', 64))
                ]),
            cancellationToken);
        await store.CompleteRunAsync(sourceRunId!, JobRunStatus.Succeeded, 0, null, cancellationToken);

        var retryId = await store.EnqueueRetryAsync(sourceRunId!, cancellationToken);
        var retry = await store.GetRunAsync(retryId!, cancellationToken);

        Assert.NotNull(retry);
        var retryActions = retry.Actions!;
        Assert.Equal(["scripts/first.py", "scripts/second.sh"], retryActions.Select(action => action.ScriptPath));
        Assert.Equal([firstId, secondId], retryActions.Select(action => action.SourceActionId));
        Assert.All(retryActions, action => Assert.Equal(JobRunActionStatus.Pending, action.Status));
        Assert.Equal(new string('a', 64), retryActions[0].ApprovedHash);
        Assert.Equal(new string('b', 64), retryActions[1].ApprovedHash);
    }

    [Fact]
    public async Task Initialize_MigratesLegacyWorkerJobsAndRunsToActionRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = SqlStrings.CreateEnvironmentsTable + ";\n" + """
                INSERT INTO Environments
                    (Id, CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC)
                VALUES
                    (1, 'legacy-worker', 2, '', '', 'Review the repository.', $now, $now);

                CREATE TABLE Jobs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ProjectPath TEXT NOT NULL,
                    EnvironmentId INTEGER,
                    TimeoutMinutes INTEGER NOT NULL DEFAULT 60,
                    Enabled INTEGER NOT NULL DEFAULT 0,
                    CreatedUTC TEXT NOT NULL,
                    UpdatedUTC TEXT NOT NULL,
                    DeletedUTC TEXT
                );
                CREATE TABLE JobRuns (
                    Id TEXT PRIMARY KEY,
                    JobId INTEGER NOT NULL,
                    TriggerKind INTEGER NOT NULL,
                    TriggerKey TEXT NOT NULL UNIQUE,
                    Status INTEGER NOT NULL,
                    JobName TEXT NOT NULL,
                    ProjectPath TEXT NOT NULL,
                    Llm INTEGER NOT NULL,
                    EnvironmentId INTEGER,
                    EnvironmentName TEXT,
                    TimeoutMinutes INTEGER NOT NULL,
                    SessionId TEXT,
                    QueuedUTC TEXT NOT NULL,
                    StartedUTC TEXT,
                    EndedUTC TEXT,
                    ExitCode INTEGER,
                    ErrorMessage TEXT,
                    CancelRequested INTEGER NOT NULL DEFAULT 0,
                    OwnerProcessId INTEGER
                );
                INSERT INTO Jobs
                    (Id, Name, ProjectPath, EnvironmentId, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC)
                VALUES
                    (7, 'Legacy worker job', $projectPath, 1, 60, 1, $now, $now);
                INSERT INTO JobRuns
                    (Id, JobId, TriggerKind, TriggerKey, Status, JobName, ProjectPath, Llm,
                     EnvironmentId, EnvironmentName, TimeoutMinutes, SessionId, QueuedUTC,
                     StartedUTC, EndedUTC, ExitCode, CancelRequested)
                VALUES
                    ('legacy-run', 7, 3, 'manual:legacy', 2, 'Legacy worker job', $projectPath,
                     2, 1, 'legacy-worker', 60, 'legacy-session', $now, $now, $now, 0, 0);
                """;
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$projectPath", Path.GetTempPath());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var store = new JobStore(_connectionString);
        var job = await store.GetJobAsync(7, cancellationToken);
        var run = await store.GetRunAsync("legacy-run", cancellationToken);

        var jobAction = Assert.Single(job!.Actions!);
        Assert.Equal(JobActionKind.Worker, jobAction.Kind);
        Assert.Equal(1, jobAction.EnvironmentId);
        var runAction = Assert.Single(run!.Actions!);
        Assert.Equal(jobAction.Id, runAction.SourceActionId);
        Assert.Equal(JobActionKind.Worker, runAction.Kind);
        Assert.Equal(JobRunActionStatus.Succeeded, runAction.Status);
        Assert.Equal("legacy-session", runAction.SessionId);
        Assert.Equal(0, runAction.ExitCode);
        await using var migrated = new SqliteConnection(_connectionString);
        await migrated.OpenAsync(cancellationToken);
        await using var schemaVersion = migrated.CreateCommand();
        schemaVersion.CommandText = "PRAGMA schema_version;";
        var version = await schemaVersion.ExecuteScalarAsync(cancellationToken);
        var reopened = new JobStore(_connectionString);
        Assert.Equal(version, await schemaVersion.ExecuteScalarAsync(cancellationToken));
        Assert.Equal(jobAction.Id, Assert.Single((await reopened.GetJobAsync(7, cancellationToken))!.Actions!).Id);
        Assert.Equal(runAction.Id, Assert.Single((await reopened.GetRunAsync("legacy-run", cancellationToken))!.Actions!).Id);
    }

    [Fact]
    public async Task SessionInsert_AtomicallyLinksTheSessionIdToItsJobRun()
    {
        await CreateSessionsSchemaAsync(TestContext.Current.CancellationToken);
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        const string sessionId = "job-session-123";

        await InsertSessionAsync(sessionId, runId!, cancellationToken);

        var run = await store.GetRunAsync(runId!, cancellationToken);
        Assert.Equal(sessionId, run!.SessionId);
    }

    [Fact]
    public async Task SessionInsert_AbortsWhenItsJobRunDoesNotExist()
    {
        await CreateSessionsSchemaAsync(TestContext.Current.CancellationToken);
        var (store, _) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<SqliteException>(
            () => InsertSessionAsync("orphaned-job-session", "missing-run", cancellationToken));

        Assert.Contains("no longer exists", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Sessions WHERE Id = 'orphaned-job-session';";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(cancellationToken))!);

        // Keep the store live through the assertion: the trigger failure must not poison later
        // connections or the JobStore's schema.
        Assert.Empty(await store.GetRunsAsync(cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task GetRunsPageAsync_ReturnsBoundedPagesAndTheVisibleTotal()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;

        for (var index = 0; index < 5; index++)
        {
            var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
            await store.CompleteRunAsync(runId!, JobRunStatus.Succeeded, 0, null, cancellationToken);
        }

        var first = await store.GetRunsPageAsync(jobId, page: 1, pageSize: 2, cancellationToken: cancellationToken);
        var second = await store.GetRunsPageAsync(jobId, page: 2, pageSize: 2, cancellationToken: cancellationToken);
        var third = await store.GetRunsPageAsync(jobId, page: 3, pageSize: 2, cancellationToken: cancellationToken);

        Assert.Equal(5, first.TotalRuns);
        Assert.Equal(2, first.Runs.Count);
        Assert.Equal(2, second.Runs.Count);
        Assert.Single(third.Runs);
        Assert.Equal(5, first.Runs.Concat(second.Runs).Concat(third.Runs).Select(run => run.Id).Distinct().Count());
    }

    [Fact]
    public async Task SoftDeleteRunsAsync_PreservesTriggerDeduplicationAndHidesTheRun()
    {
        var (store, _) = await SeedJobAsync(includeCommitTrigger: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstBatch = await store.EnqueueEventRunsAsync(
            Path.GetTempPath(), JobTriggerKind.Commit, "commit-abc", cancellationToken);
        var runId = Assert.Single(firstBatch);
        await store.CompleteRunAsync(runId, JobRunStatus.Succeeded, 0, null, cancellationToken);

        var (deleted, skipped) = await store.SoftDeleteRunsAsync([runId], cancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal(0, skipped);
        Assert.Null(await store.GetRunAsync(runId, cancellationToken));
        Assert.Empty(await store.GetRunsAsync(cancellationToken: cancellationToken));

        // The same native hook can be delivered more than once. Keeping the row and its unique
        // TriggerKey means removing it from history must not queue the commit again.
        var repeatedBatch = await store.EnqueueEventRunsAsync(
            Path.GetTempPath(), JobTriggerKind.Commit, "commit-abc", cancellationToken);
        Assert.Empty(repeatedBatch);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DeletedUTC, TriggerKey FROM JobRuns WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.False(reader.IsDBNull(0));
        Assert.Contains("commit-abc", reader.GetString(1), StringComparison.Ordinal);
    }

    /// <summary>
    /// Startup skips the trigger DROP/CREATE when the installed definition already matches, so it
    /// does not take a schema-write lock on every boot. That comparison is against the text SQLite
    /// stores, which is not the text we submit — it drops the statement terminator and strips
    /// <c>IF NOT EXISTS</c>. If the normalization stops accounting for that the gate silently never
    /// matches, so assert the store recognises its own freshly-written trigger.
    /// </summary>
    [Fact]
    public async Task Initialize_LeavesTheSessionLinkTriggerRecognisableSoRestartsSkipRecreatingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateSessionsSchemaAsync(cancellationToken);
        await SeedJobAsync();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        Assert.True(JobStore.IsLinkTriggerCurrent(connection));
    }

    [Fact]
    public async Task SoftDeleteRunsAsync_KeepsTheRecordedSessionAndReturnsItToChatHistory()
    {
        await CreateSessionsSchemaAsync(TestContext.Current.CancellationToken);
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        const string sessionId = "retained-job-session";
        await InsertSessionAsync(sessionId, runId!, cancellationToken);
        await InsertSessionLogAsync(sessionId, cancellationToken);
        await store.CompleteRunAsync(runId!, JobRunStatus.Succeeded, 0, null, cancellationToken);

        var result = await store.SoftDeleteRunsAsync([runId!], cancellationToken);

        Assert.Equal((1, 0), result);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.SessionId, s.JobRunId,
                   (SELECT COUNT(*) FROM SessionLogs WHERE SessionId = s.Id)
            FROM JobRuns r
            JOIN Sessions s ON s.Id = r.SessionId
            WHERE r.Id = $runId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(sessionId, reader.GetString(0));
        Assert.True(reader.IsDBNull(1)); // SelectChatHistoryBase now includes this retained session.
        Assert.Equal(1, reader.GetInt32(2));
    }

    [Fact]
    public async Task GetRunSummariesAsync_IsProjectScopedAndExcludesSoftDeletedRuns()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        await store.CompleteRunAsync(runId!, JobRunStatus.Succeeded, 0, null, cancellationToken);

        Assert.Single(await store.GetRunSummariesAsync(Path.GetTempPath(), cancellationToken));
        Assert.Empty(await store.GetRunSummariesAsync(Path.Combine(Path.GetTempPath(), "another-project"), cancellationToken));

        await store.SoftDeleteRunsAsync([runId!], cancellationToken);
        Assert.Empty(await store.GetRunSummariesAsync(Path.GetTempPath(), cancellationToken));
    }

    /// <summary>
    /// Creates the Environments row the run insert INNER JOINs against, then a Job pointing at it.
    /// </summary>
    [Fact]
    public async Task EnqueueEventRunsAsync_PreCommit_DoesNotEnqueueAfterCommitJobs()
    {
        var (store, commitJobId) = await SeedJobAsync(includeCommitTrigger: true);
        var cancellationToken = TestContext.Current.CancellationToken;
        var commitJob = await store.GetJobAsync(commitJobId, cancellationToken);
        await store.CreateJobAsync(new CreateJobRequest(
            Name: "Before-commit review",
            ProjectPath: Path.GetTempPath(),
            Llm: LLM.Claude,
            EnvironmentId: commitJob!.EnvironmentId,
            Prompt: "Review staged changes.",
            TimeoutMinutes: 30,
            Enabled: true,
            Triggers: [new JobTriggerRequest(JobTriggerKind.PreCommit)]), cancellationToken);

        var preCommit = await store.EnqueueEventRunsAsync(
            Path.GetTempPath(), JobTriggerKind.PreCommit, "preflight-run", cancellationToken);
        var afterCommit = await store.EnqueueEventRunsAsync(
            Path.GetTempPath(), JobTriggerKind.Commit, "commit-abc", cancellationToken);

        Assert.Single(preCommit);
        Assert.Single(afterCommit);
        Assert.NotEqual(preCommit[0], afterCommit[0]);

        var preRun = await store.GetRunAsync(preCommit[0], cancellationToken);
        var afterRun = await store.GetRunAsync(afterCommit[0], cancellationToken);
        Assert.Equal(JobTriggerKind.PreCommit, preRun!.TriggerKind);
        Assert.Equal(JobTriggerKind.Commit, afterRun!.TriggerKind);
        Assert.StartsWith("precommit:", preRun.TriggerKey, StringComparison.Ordinal);
        Assert.StartsWith("commit:", afterRun.TriggerKey, StringComparison.Ordinal);
    }

    private async Task<(JobStore Store, long JobId)> SeedJobAsync(
        int? timeoutMinutes = null,
        int? intervalMinutes = null,
        bool launchMinimized = false,
        bool includeCommitTrigger = false)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new JobStore(_connectionString);
        var environmentId = await InsertEnvironmentAsync(cancellationToken);

        var triggers = new List<JobTriggerRequest>();
        if (intervalMinutes is not null)
            triggers.Add(new JobTriggerRequest(JobTriggerKind.Schedule, JobScheduleKind.Interval, intervalMinutes));
        if (includeCommitTrigger)
            triggers.Add(new JobTriggerRequest(JobTriggerKind.Commit));

        var job = await store.CreateJobAsync(new CreateJobRequest(
            Name: "Nightly review",
            ProjectPath: Path.GetTempPath(),
            Llm: LLM.Claude,
            EnvironmentId: environmentId,
            Prompt: "Run the nightly review.",
            TimeoutMinutes: timeoutMinutes,
            Enabled: true,
            Triggers: triggers,
            LaunchMinimized: launchMinimized), cancellationToken);

        return (store, job.Id);
    }

    private static CreateJobRequest AnotherJob(string name, int? environmentId) => new(
        Name: name,
        ProjectPath: Path.GetTempPath(),
        Llm: LLM.Claude,
        EnvironmentId: environmentId,
        Prompt: "Run the review.",
        TimeoutMinutes: null,
        Enabled: true,
        Triggers: [],
        LaunchMinimized: false);

    private async Task<(JobStore Store, long JobId)> SeedScriptJobAsync(
        IReadOnlyList<JobActionRequest> actions)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new JobStore(_connectionString);
        _ = await InsertEnvironmentAsync(cancellationToken);
        var job = await store.CreateJobAsync(new CreateJobRequest(
            Name: "Script workflow",
            ProjectPath: Path.GetTempPath(),
            Llm: LLM.NotSet,
            EnvironmentId: null,
            Prompt: string.Empty,
            TimeoutMinutes: null,
            Enabled: true,
            Triggers: [],
            Actions: actions.ToList()), cancellationToken);
        return (store, job.Id);
    }

    private async Task<int> InsertEnvironmentAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = SqlStrings.CreateEnvironmentsTable;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Environments (CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC)
            VALUES ('nightly', $llm, '', '--model opus', 'Run the nightly review.', $now, $now);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$llm", (int)LLM.Claude);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task CreateSessionsSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = SqlStrings.CreateSessionsTable + ";\n" + SqlStrings.CreateSessionLogsTable;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertSessionAsync(
        string sessionId,
        string jobRunId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions
                (Id, Cli, EnvironmentName, WorkingDirectory, ProjectDisplayName, StartedUTC,
                 OwnerPid, OwnershipTracked, JobRunId)
            VALUES
                ($id, 'claude', 'nightly', $workDir, 'test', $startedUtc, 4242, 1, $jobRunId);
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$workDir", Path.GetTempPath());
        command.Parameters.AddWithValue("$startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$jobRunId", jobRunId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertSessionLogAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SessionLogs (SessionId, Timestamp, Content, IsError)
            VALUES ($sessionId, $timestamp, $content, 0);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$content", new byte[] { 1, 2, 3 });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
