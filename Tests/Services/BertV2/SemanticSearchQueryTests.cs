using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// VIBE-170 #4: the semantic query scans vec_search_chunks once instead of looking up each chunk's
/// vector by primary key. The ranked rows, scores included, must match the query it replaced.
/// </summary>
public sealed class SemanticSearchQueryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "semantic-scan-" + Guid.NewGuid().ToString("N"));
    private string SearchPath => Path.Combine(root, "search.db");
    private string ProjectA => BoardPaths.NormalizeProjectPath(Path.Combine(root, "project-a"));
    private string ProjectB => BoardPaths.NormalizeProjectPath(Path.Combine(root, "project-b"));

    public SemanticSearchQueryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ScanFirstQuery_ReturnsTheSameRankedRowsAsThePerChunkLookup()
    {
        var topics = Seed(new Random(170));
        var index = new SqliteSearchIndexStore(SearchPath, Path.Combine(root, "state.db"));
        var excluded = new[] { "board-0-3", "input-1-5", "session-0-7", "board-1-2" };
        var compared = 0;
        foreach (var topic in topics)
        foreach (var kind in new[] { "board", "input", "session" })
        foreach (var scope in new[] { null, ProjectA })
        foreach (var exclude in new[] { null, excluded })
        foreach (var count in new[] { 3, int.MaxValue })
        {
            var expected = Reference(kind, topic, count, scope, exclude);
            var actual = index.Search(kind, "", topic, count, scope, exclude)
                .Select(match => (match.Document.Id, match.Text, match.Semantic, match.Chunk)).ToList();
            Assert.Equal(expected, actual);
            compared += expected.Count;
        }

        // The fixture must reach past the board threshold and the ineligible rows, or equality proves little.
        Assert.True(compared > 500, $"only {compared} rows compared");
        Assert.NotEmpty(Reference("board", topics[0], int.MaxValue, null, null));
    }

    /// <summary>The query this replaced, verbatim apart from the inlined eligibility clause.</summary>
    private List<(string, string, double, int)> Reference(string kind, float[] vector, int count,
        string? scope, IReadOnlyCollection<string>? excluded)
    {
        var collation = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "NOCASE" : "BINARY";
        using var db = BertVectorDatabase.Open(SearchPath, readOnly: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            WITH distances AS MATERIALIZED (
                SELECT d.Id,c.Id AS ChunkId,c.Position,
                    1-vec_distance_cosine(v.Embedding,$vector) AS score
                FROM SearchDocuments d JOIN SearchSources s ON s.DocumentId=d.Id
                JOIN SearchChunks c ON c.SourceId=s.Id JOIN vec_search_chunks v ON v.Id=c.Id
                WHERE d.Kind=$kind AND ($scope IS NULL OR d.ProjectPath=$scope COLLATE {collation})
                    AND ($excluded IS NULL OR d.Id NOT IN (SELECT value FROM json_each($excluded)))
                    AND c.ModelVersion=$model AND s.ChunkVersion=$chunks
            ), ranked AS (
                SELECT *,row_number() OVER(PARTITION BY Id ORDER BY score DESC,Position) AS rank FROM distances
            ) SELECT d.Id,c.Text,score,ranked.Position FROM ranked
                JOIN SearchDocuments d ON d.Id=ranked.Id JOIN SearchChunks c ON c.Id=ranked.ChunkId
                WHERE rank=1 AND score>=$threshold ORDER BY score DESC,ranked.Id LIMIT $count
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$scope", (object?)scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$excluded", excluded is null ? DBNull.Value : JsonSerializer.Serialize(excluded.ToList(),
            StorageJsonSerializerContext.Default.ListString));
        cmd.Parameters.AddWithValue("$vector", BertVectorDatabase.Serialize(vector));
        cmd.Parameters.AddWithValue("$model", SearchIndexVersions.Model);
        cmd.Parameters.AddWithValue("$chunks", SearchIndexVersions.Chunks);
        cmd.Parameters.AddWithValue("$threshold", kind == "board" ? .45 : -1);
        cmd.Parameters.AddWithValue("$count", count);
        var rows = new List<(string, string, double, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetInt32(3)));
        return rows;
    }

    /// <summary>
    /// Documents in two projects whose chunk vectors mix a few topics, so similarities spread across
    /// the board threshold. Some chunks are stale (old model or chunker version), some have no vector,
    /// and some vectors have no chunk; none of those may match.
    /// </summary>
    private float[][] Seed(Random random)
    {
        SearchDatabaseSchema.Ensure(SearchPath);
        SearchDatabaseSchema.EnsureVectors(SearchPath);
        var topics = Enumerable.Range(0, 4).Select(_ => Gaussian(random)).ToArray();
        using var db = BertVectorDatabase.Open(SearchPath);
        using var tx = db.BeginTransaction();
        void Run(string sql, params (string, object?)[] parameters)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        foreach (var kind in new[] { "board", "input", "session" })
        for (var p = 0; p < 2; p++)
        for (var n = 0; n < 12; n++)
        {
            var id = $"{kind}-{p}-{n}";
            var project = p == 0 ? ProjectA : ProjectB;
            Run("INSERT INTO SearchDocuments(Id,Kind,ProjectPath,SessionId,Metadata,SeenEpoch) VALUES($id,$kind,$project,NULL,$meta,1)",
                ("$id", id), ("$kind", kind), ("$project", project),
                ("$meta", JsonSerializer.Serialize(new SearchDocument(id, kind, project, []), StorageJsonSerializerContext.Default.SearchDocument)));
            for (var s = 0; s < 1 + random.Next(3); s++)
            {
                var source = $"{id}/s{s}";
                var chunkVersion = random.Next(8) == 0 ? "old-chunker" : SearchIndexVersions.Chunks;
                Run("INSERT INTO SearchSources(Id,DocumentId,SourceKey,Text,Title,Hash,ChunkVersion) VALUES($id,$doc,$key,'text','',$id,$version)",
                    ("$id", source), ("$doc", id), ("$key", "s" + s), ("$version", chunkVersion));
                for (var c = 0; c < 1 + random.Next(4); c++)
                {
                    var chunk = $"{source}:{c}";
                    var model = random.Next(8) switch { 0 => "old-model", 1 => null, _ => SearchIndexVersions.Model };
                    Run("INSERT INTO SearchChunks(Id,SourceId,Position,Text,Hash,ModelVersion) VALUES($id,$source,$position,$text,$id,$model)",
                        ("$id", chunk), ("$source", source), ("$position", c), ("$text", "chunk " + chunk), ("$model", model));
                    if (random.Next(10) != 0)
                        Run("INSERT INTO vec_search_chunks(Id,Embedding) VALUES($id,$vector)",
                            ("$id", chunk), ("$vector", BertVectorDatabase.Serialize(Mix(random, topics))));
                }
            }
        }
        for (var orphan = 0; orphan < 5; orphan++)
            Run("INSERT INTO vec_search_chunks(Id,Embedding) VALUES($id,$vector)",
                ("$id", "orphan-" + orphan), ("$vector", BertVectorDatabase.Serialize(Mix(random, topics))));
        tx.Commit();
        return topics;
    }

    private static float[] Mix(Random random, float[][] topics)
    {
        var vector = Gaussian(random);
        var topic = topics[random.Next(topics.Length)];
        var weight = (float)(random.NextDouble() * 4);
        for (var i = 0; i < vector.Length; i++) vector[i] = vector[i] * .15f + topic[i] * weight;
        return vector;
    }

    private static float[] Gaussian(Random random)
    {
        var vector = new float[384];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));
        return vector;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
