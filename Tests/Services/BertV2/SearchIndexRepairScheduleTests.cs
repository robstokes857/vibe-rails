using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// VIBE-322: the strict FTS check holds search.db's writer lock while it re-tokenizes every source. It ran
/// every 15 minutes in every root; now only after the index changed (at most every six hours) or daily.
/// </summary>
public sealed class SearchIndexRepairScheduleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vb-search-repair-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSearchIndexStore _index;
    private static readonly long Hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    private string SearchPath => Path.Combine(_root, "search.db");

    public SearchIndexRepairScheduleTests()
    {
        Directory.CreateDirectory(_root);
        _index = new SqliteSearchIndexStore(SearchPath, Path.Combine(_root, "state.db"));
        SearchDatabaseSchema.Ensure(SearchPath);
        Execute("INSERT INTO SearchDocuments(Id,Kind,ProjectPath,SessionId,Metadata,SeenEpoch) VALUES('doc','board','C:/repo',NULL,'{}',1)");
        AddSource("first", "the first source text");
    }

    [Fact]
    public void TheStrictCheckRunsAfterTheIndexChangedAtMostEverySixHoursAndOtherwiseDaily()
    {
        var start = 1_000 * Hour;
        Assert.False(_index.Repair(start));
        Assert.Equal(1, _index.FtsChecks);
        Assert.False(_index.Repair(start + 7 * Hour));
        Assert.Equal(1, _index.FtsChecks);

        AddSource("second", "a write reaches the index");
        Assert.False(_index.Repair(start + 2 * Hour));
        Assert.Equal(1, _index.FtsChecks);
        Assert.False(_index.Repair(start + 8 * Hour));
        Assert.Equal(2, _index.FtsChecks);

        Assert.False(_index.Repair(start + 20 * Hour));
        Assert.Equal(2, _index.FtsChecks);
        Assert.False(_index.Repair(start + 33 * Hour));
        Assert.Equal(3, _index.FtsChecks);
    }

    [Fact]
    public void DriftIsRepairedOnTheFirstCheckAndAgainOnceTheIndexChanges()
    {
        // A content change the triggers did not see: the strict check fails and rebuilds the index.
        Execute("DROP TRIGGER SearchSources_au; UPDATE SearchSources SET Text='changed behind the index' WHERE Id='first'");
        Assert.True(_index.Repair(10 * Hour));
        Assert.False(_index.Repair(17 * Hour));
        Execute("UPDATE SearchSources SET Text='changed again' WHERE Id='first'");
        AddSource("third", "the next write");
        Assert.True(_index.Repair(24 * Hour));
    }

    private void AddSource(string id, string text) => Execute(
        $"INSERT INTO SearchSources(Id,DocumentId,SourceKey,Text,Title,Hash) VALUES('{id}','doc','{id}','{text}','Title','{id}')");

    private void Execute(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SearchPath, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
