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
        SqliteConnection.ClearAllPools();
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
        Assert.True(await store.RequestCancelAsync(runId!, cancellationToken));

        var status = await store.CompleteIdleRunAsync(runId!, cancellationToken);
        var run = await store.GetRunAsync(runId!, cancellationToken);

        Assert.Equal(JobRunStatus.Cancelled, status);
        Assert.Equal(JobRunStatus.Cancelled, run!.Status);
        Assert.Equal(3, run.ExitCode);
        Assert.Equal("Automation was cancelled.", run.ErrorMessage);
    }

    [Fact]
    public async Task CompleteIdleRunAsync_SucceedsWhenNoCancellationIsPending()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        Assert.True(await store.StartRunAsync(runId!, 4242, cancellationToken));

        var status = await store.CompleteIdleRunAsync(runId!, cancellationToken);
        var run = await store.GetRunAsync(runId!, cancellationToken);

        Assert.Equal(JobRunStatus.Succeeded, status);
        Assert.Equal(JobRunStatus.Succeeded, run!.Status);
        Assert.Equal(0, run.ExitCode);
        Assert.Null(run.ErrorMessage);
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
    public async Task TryMarkLaunchedAsync_LetsExactlyOneCallerSpawnTheTerminal()
    {
        // A scheduler lease handoff can briefly leave two callers looking at the same queued run.
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        var winner = await store.TryMarkLaunchedAsync(runId!, cancellationToken);
        var loser = await store.TryMarkLaunchedAsync(runId!, cancellationToken);

        Assert.True(winner);
        Assert.False(loser);
    }

    [Fact]
    public async Task GetLaunchableRunsAsync_ExcludesRunsWhoseTerminalWasAlreadySpawned()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);

        Assert.Single(await store.GetLaunchableRunsAsync(cancellationToken));
        await store.TryMarkLaunchedAsync(runId!, cancellationToken);
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
        await store.TryMarkLaunchedAsync(runId!, cancellationToken);

        // Zero grace: anything already launched is overdue.
        var failed = await store.FailStalledLaunchesAsync(TimeSpan.Zero, cancellationToken);

        Assert.Equal(1, failed);
        var run = await store.GetRunAsync(runId!, cancellationToken);
        Assert.Equal(JobRunStatus.Failed, run!.Status);
        Assert.Contains("interactive desktop", run.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailStalledLaunchesAsync_LeavesARunAloneOnceItHasClaimedItself()
    {
        var (store, jobId) = await SeedJobAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = await store.EnqueueManualRunAsync(jobId, cancellationToken);
        await store.TryMarkLaunchedAsync(runId!, cancellationToken);
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
