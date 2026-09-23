using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.DB;

/// <summary>
/// <c>Jobs.ImportedFromJobId</c> (VB-33) against real SQLite: written once at creation, read back
/// by every Job projection, untouched by an edit, and adopted on a database that a pre-VB-33 build
/// created — where the <c>"jobs"/1</c> receipt already exists and can never add the column itself.
/// </summary>
public sealed class JobStoreImportOriginTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "jobstore-import-origin-" + Guid.NewGuid().ToString("N") + ".db");

    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString();

    [Fact]
    public async Task CreateJobAsync_PersistsTheOrigin_AndUpdateJobAsyncLeavesItAlone()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        var store = new JobStore(ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var original = await store.CreateJobAsync(Request("Nightly review"), cancellationToken);
        var copy = await store.CreateJobAsync(
            Request("Nightly review") with { ImportedFromJobId = original.Id }, cancellationToken);

        Assert.Null(original.ImportedFromJobId);
        Assert.Equal(original.Id, copy.ImportedFromJobId);
        Assert.Equal(original.Id, (await store.GetJobAsync(copy.Id, cancellationToken))!.ImportedFromJobId);
        var listed = await store.GetJobsAsync(projectPath: null, includeDeleted: false, cancellationToken);
        Assert.Equal(original.Id, Assert.Single(listed, job => job.Id == copy.Id).ImportedFromJobId);
        Assert.Null(Assert.Single(listed, job => job.Id == original.Id).ImportedFromJobId);

        var updated = await store.UpdateJobAsync(copy.Id, new UpdateJobRequest(
            "Renamed", copy.ProjectPath, LLM.NotSet, null, string.Empty, null, Enabled: true, [],
            LaunchMinimized: true, Actions: [ScriptAction()]), cancellationToken);

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated!.Name);
        Assert.Equal(original.Id, updated.ImportedFromJobId);
    }

    [Fact]
    public void AnOlderDatabaseGainsTheColumnThroughItsOwnReceipt()
    {
        StateDatabaseSchema.Ensure(ConnectionString);
        _ = new JobStore(ConnectionString);
        // Roll the file back to what a pre-VB-33 build leaves behind: "jobs"/1 recorded, and neither
        // the column nor its receipt. Re-running AdoptSchema's ALTER list is not an option there.
        Execute("""
            ALTER TABLE Jobs DROP COLUMN ImportedFromJobId;
            DELETE FROM SchemaMigrations WHERE Component = 'jobs-import-origin';
            """);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM pragma_table_info('Jobs') WHERE name = 'ImportedFromJobId';"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'jobs';"));

        _ = new JobStore(ConnectionString);

        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM pragma_table_info('Jobs') WHERE name = 'ImportedFromJobId';"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'jobs-import-origin';"));

        // Idempotent: a further open changes nothing.
        var version = Scalar("PRAGMA schema_version;");
        _ = new JobStore(ConnectionString);
        Assert.Equal(version, Scalar("PRAGMA schema_version;"));
    }

    private static CreateJobRequest Request(string name) => new(
        name, Path.GetTempPath(), LLM.NotSet, null, string.Empty, null, Enabled: false, [],
        Actions: [ScriptAction()]);

    private static JobActionRequest ScriptAction() => new(
        null, JobActionKind.Script, null, "scripts/check.py", JobScriptRuntime.Python, [], null, null,
        new string('a', 64));

    private object? Scalar(string sql)
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var query = connection.CreateCommand();
        query.CommandText = sql;
        return query.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var connection = SqliteConnectionFactory.Open(ConnectionString);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}
