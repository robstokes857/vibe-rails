using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.DB;

public sealed class JobDescriptionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "job-description-" + Guid.NewGuid().ToString("N"));
    private string ConnectionString => $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DescriptionRoundTripsAndOmittedUpdatesPreserveItWhileEmptyClearsIt()
    {
        Directory.CreateDirectory(root);
        StateDatabaseSchema.Ensure(ConnectionString);
        var jobs = new JobStore(ConnectionString);
        var job = await jobs.CreateJobAsync(new("review", root, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python)],
            Description: "Review security and regression coverage."), Ct);
        Assert.Equal("Review security and regression coverage.", job.Description);
        var boards = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", ConnectionString);
        var definition = Assert.Single(await boards.DescribeLaneAutomationsAsync(root, [job.Id], Ct));
        Assert.Equal(job.Description, definition.Description);
        Assert.Contains(job.Description!, BoardService.Describe(definition).Summary);
        var update = new UpdateJobRequest("renamed", root, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py", ScriptRuntime: JobScriptRuntime.Python)]);
        Assert.Equal(job.Description, (await jobs.UpdateJobAsync(job.Id, update, Ct))!.Description);
        Assert.Equal("", (await jobs.UpdateJobAsync(job.Id, update with { Description = "" }, Ct))!.Description);
    }

    [Fact]
    public async Task UpgradeKeepsOldRowsAndOldWritersAndBoardReadsAnOlderStateSchema()
    {
        Directory.CreateDirectory(root);
        StateDatabaseSchema.Ensure(ConnectionString);
        _ = new JobStore(ConnectionString);
        using (var connection = SqliteConnectionFactory.Open(ConnectionString))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE Jobs DROP COLUMN Description;
                DELETE FROM SchemaMigrations WHERE Component = 'jobs-description';
                INSERT INTO Jobs (Name, ProjectPath, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC)
                VALUES ('older', $project, 0, 1, '2026-09-23T00:00:00Z', '2026-09-23T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$project", root);
            command.ExecuteNonQuery();
        }
        var boards = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", ConnectionString);
        Assert.Null(Assert.Single(await boards.DescribeLaneAutomationsAsync(root, [1], Ct)).Description);
        var jobs = new JobStore(ConnectionString);
        Assert.Null((await jobs.GetJobAsync(1, Ct))!.Description);
        using var upgraded = SqliteConnectionFactory.Open(ConnectionString);
        using var query = upgraded.CreateCommand();
        query.CommandText = "INSERT INTO Jobs (Name, ProjectPath, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC) SELECT 'old writer', ProjectPath, TimeoutMinutes, Enabled, CreatedUTC, UpdatedUTC FROM Jobs LIMIT 1;";
        Assert.Equal(1, query.ExecuteNonQuery());
        Assert.Equal(2, (await jobs.GetJobsAsync(root, cancellationToken: Ct)).Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
