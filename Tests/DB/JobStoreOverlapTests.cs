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

    /// <summary>
    /// Creates the Environments row the run insert INNER JOINs against, then a Job pointing at it.
    /// </summary>
    private async Task<(JobStore Store, long JobId)> SeedJobAsync(int? timeoutMinutes = null, int? intervalMinutes = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new JobStore(_connectionString);
        var environmentId = await InsertEnvironmentAsync(cancellationToken);

        var triggers = intervalMinutes is null
            ? new List<JobTriggerRequest>()
            : [new JobTriggerRequest(JobTriggerKind.Schedule, JobScheduleKind.Interval, intervalMinutes)];

        var job = await store.CreateJobAsync(new CreateJobRequest(
            Name: "Nightly review",
            ProjectPath: Path.GetTempPath(),
            Llm: LLM.Claude,
            EnvironmentId: environmentId,
            Prompt: "Run the nightly review.",
            TimeoutMinutes: timeoutMinutes,
            Enabled: true,
            Triggers: triggers), cancellationToken);

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
}
