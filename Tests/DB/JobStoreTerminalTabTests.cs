using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.DB;

public sealed class JobStoreTerminalTabTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"job-tabs-{Guid.NewGuid():N}.db");
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    [Fact]
    public async Task FullWorkflowRecordingSurvivesWorkerSessionBacklinkAndHistoryRemovalRetainsBoth()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        var store = new JobStore(ConnectionString);
        var repository = new Repository(ConnectionString);
        var token = TestContext.Current.CancellationToken;
        var job = await store.CreateJobAsync(new CreateJobRequest("workflow", Path.GetTempPath(), LLM.NotSet,
            null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python)],
            LaunchInTerminalTab: true), token);
        var runId = (await store.EnqueueManualRunAsync(job.Id, token))!;
        Assert.NotNull(runId);
        await repository.CreateSessionAsync("workflow-session", "shell", null, job.ProjectPath, Environment.ProcessId);
        await store.LinkRunTerminalSessionAsync(runId, "workflow-session", token);
        Assert.Equal("workflow-session", (await store.GetRunAsync(runId, token))!.SessionId);
        await repository.CreateSessionAsync("worker-session", "claude", null, job.ProjectPath, Environment.ProcessId, runId);

        var run = await store.GetRunAsync(runId, token);
        Assert.Equal("worker-session", run!.SessionId);
        Assert.Equal("workflow-session", run.TerminalSessionId);
        await store.StartRunAsync(runId, Environment.ProcessId, token);
        await store.CompleteRunAsync(runId, JobRunStatus.Succeeded, 0, null, token);
        Assert.Equal((1, 0), await store.SoftDeleteRunsAsync([runId], token));
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM Sessions WHERE Id IN ('workflow-session','worker-session') AND JobRunId IS NULL;";
        Assert.Equal(2L, query.ExecuteScalar());
    }

    [Fact]
    public async Task ManualRunsAndRetriesUseNativeRegardlessOfRetiredPreference()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        var store = new JobStore(ConnectionString);
        var token = TestContext.Current.CancellationToken;
        var request = new CreateJobRequest("scripts", Path.GetTempPath(), LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python)],
            LaunchInTerminalTab: true);
        var job = await store.CreateJobAsync(request, token);
        Assert.True(job.LaunchInTerminalTab);
        var runId = (await store.EnqueueManualRunAsync(job.Id, token))!;
        Assert.False((await store.GetRunAsync(runId, token))!.LaunchInTerminalTab);

        var update = new UpdateJobRequest(job.Name, job.ProjectPath, job.Llm, null, "", null, true, [], Actions: request.Actions);
        Assert.True((await store.UpdateJobAsync(job.Id, update, token))!.LaunchInTerminalTab); // old client omits preference
        Assert.False((await store.UpdateJobAsync(job.Id, update with { LaunchInTerminalTab = false }, token))!.LaunchInTerminalTab);
        await store.StartRunAsync(runId, Environment.ProcessId, token);
        await store.CompleteRunAsync(runId, JobRunStatus.Failed, 1, "test", token);
        var retry = (await store.EnqueueRetryAsync(runId, token))!;
        Assert.False((await store.GetRunAsync(retry, token))!.LaunchInTerminalTab);
    }

    [Fact]
    public void ExistingDatabaseAutomaticallyAddsCompatibleColumnsWithNativeDefault()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        _ = new JobStore(ConnectionString);
        using (var connection = SqliteConnectionFactory.Open(ConnectionString))
        using (var command = connection.CreateCommand())
        {
            // Disposable fixture reproduces the schema predating this additive migration.
            command.CommandText = """
                ALTER TABLE Jobs DROP COLUMN LaunchInTerminalTab;
                ALTER TABLE JobRuns DROP COLUMN LaunchInTerminalTab;
                ALTER TABLE JobRuns DROP COLUMN TerminalSessionId;
                DELETE FROM SchemaMigrations WHERE Component = 'jobs-terminal-tabs';
                INSERT INTO Jobs (Name, ProjectPath, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC)
                VALUES ('older', 'project', 0, 1, '2026-09-23T00:00:00Z', '2026-09-23T00:00:00Z');
                """;
            command.ExecuteNonQuery();
        }
        _ = new JobStore(ConnectionString);
        using var upgraded = SqliteConnectionFactory.Open(ConnectionString);
        using var query = upgraded.CreateCommand();
        query.CommandText = "SELECT LaunchInTerminalTab FROM Jobs WHERE Name = 'older';";
        Assert.Equal(0L, query.ExecuteScalar());
        // An older binary can continue inserting Jobs without the newly added column.
        query.CommandText = "INSERT INTO Jobs (Name, ProjectPath, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC) SELECT 'old writer', ProjectPath, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC FROM Jobs LIMIT 1;";
        Assert.Equal(1, query.ExecuteNonQuery());
    }

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            try { File.Delete(path); } catch (IOException) { }
    }
}
