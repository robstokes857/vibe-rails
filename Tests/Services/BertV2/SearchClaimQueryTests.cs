using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// VIBE-321 #2: work claims use the indexable <c>IS NULL OR &lt; OR &gt;</c> version predicate. The
/// claimed rows, in order, must match the <c>IS NULL OR &lt;&gt;</c> query it replaced, and the plan
/// that Microsoft.Data.Sqlite's SQLite picks must start from the work index, not a source scan.
/// </summary>
public sealed class SearchClaimQueryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "search-claim-" + Guid.NewGuid().ToString("N"));
    private const long Now = 1_000_000;
    private string SearchPath => Path.Combine(root, "search.db");

    public SearchClaimQueryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void IndexableClaim_ReturnsTheSameRowsInTheSameOrderAsTheReference()
    {
        Seed(new Random(321), documents: 40);
        using var db = Open();
        var compared = 0;
        foreach (var source in new[] { true, false })
        foreach (var kind in new[] { "board", "input", "session", "missing" })
        foreach (var version in new[] { "v1", "v2", "v3", "" })
        foreach (var count in new[] { 1, 4, 100 })
        foreach (var now in new[] { Now, Now - 500, Now + 500 })
        {
            var expected = Rows(db, Reference(source), kind, version, count, now);
            Assert.Equal(expected, Rows(db, SqliteSearchIndexStore.ClaimSql(source), kind, version, count, now));
            Assert.Equal(expected.Count > 0, Rows(db, SqliteSearchIndexStore.ClaimSql(source, probe: true), kind, version, count, now).Count > 0);
            compared += expected.Count;
        }
        // The fixture must reach past leased, delayed and current rows, or equality proves little.
        Assert.True(compared > 1000, $"only {compared} rows compared");
    }

    [Theory]
    [InlineData(true, "IX_SearchSources_Work")]
    [InlineData(false, "IX_SearchChunks_Work")]
    public void ClaimPlan_StartsFromTheWorkIndex(bool source, string index)
    {
        Seed(new Random(170), documents: 400, pending: false);
        using var db = Open();
        var plan = Plan(db, SqliteSearchIndexStore.ClaimSql(source));
        TestContext.Current.TestOutputHelper?.WriteLine(plan);
        Assert.Contains(index, plan);
        Assert.DoesNotContain("SCAN w", plan);
        Assert.Contains(index, Plan(db, SqliteSearchIndexStore.ClaimSql(source, probe: true)));
    }

    [Fact]
    public void IdleClaimReturnsNothingAndPendingWorkIsStillClaimedOnce()
    {
        Seed(new Random(5), documents: 20, pending: false);
        var index = new SqliteSearchIndexStore(SearchPath, Path.Combine(root, "state.db"));
        Assert.Empty(index.ClaimSources("board", 10, "v2"));
        Assert.Empty(index.ClaimChunks("board", 10, "v2"));

        using (var db = Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE SearchSources SET ChunkVersion=NULL WHERE Id IN (
                    SELECT s.Id FROM SearchSources s JOIN SearchDocuments d ON d.Id=s.DocumentId WHERE d.Kind='board' LIMIT 3)
                """;
            cmd.ExecuteNonQuery();
        }
        var claimed = index.ClaimSources("board", 10, "v2");
        Assert.Equal(3, claimed.Count);
        Assert.Empty(index.ClaimSources("board", 10, "v2"));
        foreach (var work in claimed) index.Release(work, true);
        Assert.Equal(3, index.ClaimSources("board", 10, "v2").Count);
    }

    /// <summary>The query this replaced, verbatim apart from the inlined table and column names.</summary>
    private static string Reference(bool source)
    {
        var table = source ? "SearchSources" : "SearchChunks";
        var versionColumn = source ? "ChunkVersion" : "ModelVersion";
        return $"""
            SELECT w.Id,w.Text,{(source ? "w.Title" : "''")},w.Hash
            FROM {table} w
            {(source ? "" : "JOIN SearchSources s ON s.Id=w.SourceId")}
            JOIN SearchDocuments d ON d.Id={(source ? "w" : "s")}.DocumentId
            WHERE d.Kind=$kind AND (w.{versionColumn} IS NULL OR w.{versionColumn}<>$version)
                AND w.LeaseUntil<=$now AND w.RetryAt<=$now
                {(source ? "" : "AND s.ChunkVersion=$chunkVersion")}
            ORDER BY w.Attempts,w.Id LIMIT $count
            """;
    }

    private static List<string> Rows(SqliteConnection db, string sql, string kind, string version, int count, long now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$chunkVersion", "v2");
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(reader.FieldCount == 1 ? "probe" : reader.GetString(0) + "|" + reader.GetString(3));
        return rows;
    }

    private static string Plan(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var name in new[] { "$kind", "$version", "$chunkVersion" }) cmd.Parameters.AddWithValue(name, "x");
        foreach (var name in new[] { "$now", "$count" }) cmd.Parameters.AddWithValue(name, 1);
        var lines = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) lines.Add(reader.GetString(3));
        return string.Join("\n", lines);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SearchPath, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    /// <summary>
    /// Documents of three kinds whose sources and chunks carry null, older, current and newer versions,
    /// some leased or delayed around <see cref="Now"/>, with varied attempts so ordering matters.
    /// Without <paramref name="pending"/> every row is current, as on an idle machine.
    /// </summary>
    private void Seed(Random random, int documents, bool pending = true)
    {
        SearchDatabaseSchema.Ensure(SearchPath);
        using var db = Open();
        using var tx = db.BeginTransaction();
        void Run(string sql, params (string, object?)[] parameters)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        string? Version() => !pending ? "v2" : random.Next(5) switch { 0 => null, 1 => "v1", 2 => "v3", _ => "v2" };
        long Time() => pending && random.Next(4) == 0 ? Now + random.Next(-1000, 1000) : 0;

        foreach (var kind in new[] { "board", "input", "session" })
        for (var n = 0; n < documents; n++)
        {
            var id = $"{kind}-{n}";
            Run("INSERT INTO SearchDocuments(Id,Kind,ProjectPath,SessionId,Metadata,SeenEpoch) VALUES($id,$kind,'p',NULL,$meta,1)",
                ("$id", id), ("$kind", kind),
                ("$meta", JsonSerializer.Serialize(new SearchDocument(id, kind, "p", []), StorageJsonSerializerContext.Default.SearchDocument)));
            for (var s = 0; s < 1 + random.Next(3); s++)
            {
                var source = $"{id}/s{s}";
                Run("""
                    INSERT INTO SearchSources(Id,DocumentId,SourceKey,Text,Title,Hash,ChunkVersion,Attempts,RetryAt,LeaseUntil)
                    VALUES($id,$doc,$key,$text,'',$hash,$version,$attempts,$retry,$lease)
                    """, ("$id", source), ("$doc", id), ("$key", "s" + s), ("$text", new string('x', 3000)),
                    ("$hash", "h" + random.Next()), ("$version", Version()), ("$attempts", random.Next(3)),
                    ("$retry", Time()), ("$lease", Time()));
                for (var c = 0; c < 1 + random.Next(4); c++)
                    Run("""
                        INSERT INTO SearchChunks(Id,SourceId,Position,Text,Hash,ModelVersion,Attempts,RetryAt,LeaseUntil)
                        VALUES($id,$source,$position,'chunk',$hash,$model,$attempts,$retry,$lease)
                        """, ("$id", $"{source}:{c}"), ("$source", source), ("$position", c), ("$hash", "h" + random.Next()),
                        ("$model", Version()), ("$attempts", random.Next(3)), ("$retry", Time()), ("$lease", Time()));
            }
        }
        tx.Commit();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
