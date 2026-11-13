using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// VIBE-321 #4: the keyword query lowers each row's text once and skips the text filter for Boards; exact
/// lookups match identities alone; a cancelled token interrupts SQLite. The ranked rows, scores and
/// snippets must match the query it replaced, which this class keeps as the reference.
/// </summary>
public sealed class KeywordSearchQueryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "keyword-search-" + Guid.NewGuid().ToString("N"));
    private string SearchPath => Path.Combine(root, "search.db");
    private string ProjectA => BoardPaths.NormalizeProjectPath(Path.Combine(root, "project-a"));
    private string ProjectB => BoardPaths.NormalizeProjectPath(Path.Combine(root, "project-b"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Vocabulary =
        ["needle", "Haystack", "alpha", "BETA", "gamma", "deploy", "Ünïcode", "straße", "the", "card", "50%_off",
         "\"quoted\"", "migration", "Schema", "lane", "review", "x", "VB-7", "tile", "poll"];

    public KeywordSearchQueryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void KeywordQuery_ReturnsTheSameRankedRowsAsTheReference()
    {
        var keys = Seed(new Random(321), documents: 30);
        var index = new SqliteSearchIndexStore(SearchPath, Path.Combine(root, "state.db"));
        string[] queries = ["", "needle", "NEEDLE haystack", "alpha beta", "the card", "x", "ünïcode", "STRASSE", "straße",
            "50%_off", "\"quoted\"", "deploy migration schema lane review tile poll", "absent", "VB-7", "card_", keys[0], keys[1].ToLowerInvariant(),
            keys[2], "a"];
        var compared = 0;
        foreach (var kind in new[] { "board", "input", "session" })
        foreach (var query in queries)
        foreach (var scope in new[] { null, ProjectA })
        foreach (var excluded in new[] { null, new[] { "board-0-3", "input-1-5", "session-0-7" } })
        foreach (var count in new[] { 3, int.MaxValue })
        {
            var expected = Reference(kind, query, count, scope, excluded);
            var actual = index.Search(kind, query, null, count, scope, excluded, ct: Ct)
                .Select(match => (match.Document.Id, match.Text, match.Lexical, match.Chunk)).ToList();
            Assert.Equal(expected, actual);
            compared += expected.Count;
        }
        Assert.True(compared > 2000, $"only {compared} rows compared");
    }

    [Fact]
    public async Task ExactOnlyLookup_MatchesTheFullQueryThroughTheBoardSearchService()
    {
        var keys = Seed(new Random(170), documents: 30);
        var index = new SqliteSearchIndexStore(SearchPath, Path.Combine(root, "state.db"));
        // The same store with identity-only matching switched off: the behaviour before VIBE-321.
        var full = new Mock<ISearchIndexStore>();
        full.Setup(s => s.Search(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]?>(), It.IsAny<int>(),
                It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string kind, string query, float[]? vector, int count, string? scope, IReadOnlyCollection<string>? excluded, bool _, CancellationToken ct) =>
                index.Search(kind, query, vector, count, scope, excluded, identityOnly: false, ct));
        var model = Mock.Of<IBertV2BgeEmbedder>();
        var identity = new BoardSearchService(index, model, NullLogger<BoardSearchService>.Instance);
        var reference = new BoardSearchService(full.Object, model, NullLogger<BoardSearchService>.Instance);
        var found = 0;
        foreach (var query in keys.Concat(keys.Select(key => key.ToUpperInvariant())).Concat(["board-1-4", "VB-3", "TASK-5", "needle", "", "VB-999"]))
        foreach (var current in new[] { ProjectA, ProjectB })
        {
            var expected = await reference.SearchAsync(current, query, 50, ct: Ct, exactOnly: true);
            var actual = await identity.SearchAsync(current, query, 50, ct: Ct, exactOnly: true);
            Assert.Equal(expected, actual);
            found += expected.Count;
        }
        Assert.True(found > 20, $"only {found} exact hits compared");
    }

    [Fact]
    public void ACancelledTokenInterruptsTheRunningStatement()
    {
        Seed(new Random(9), documents: 150, textLength: 6000);
        var index = new SqliteSearchIndexStore(SearchPath, Path.Combine(root, "state.db"));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => index.Search("board", "needle", null, 10, ct: cancelled.Token));
        }

        var timer = Stopwatch.StartNew();
        index.Search("board", "needle alpha beta gamma deploy migration", null, int.MaxValue, ct: Ct);
        var duration = timer.Elapsed;
        // Cancel at points spread across one full query until one lands inside SQLite.
        var interrupted = false;
        for (var attempt = 1; attempt <= 40 && !interrupted; attempt++)
        {
            using var source = new CancellationTokenSource();
            var delay = duration * attempt / 45;
            // Timer callbacks are too coarse on Windows (about 15 ms); spin for the delay instead.
            var canceller = Task.Run(() => { var wait = Stopwatch.StartNew(); while (wait.Elapsed < delay) Thread.SpinWait(20); source.Cancel(); });
            try { index.Search("board", "needle alpha beta gamma deploy migration", null, int.MaxValue, ct: source.Token); }
            catch (OperationCanceledException ex) { interrupted = ex.InnerException is SqliteException { SqliteErrorCode: 9 }; }
            canceller.Wait(Ct);
        }
        Assert.True(interrupted, $"no cancellation interrupted a {duration.TotalMilliseconds:F0} ms query");
    }

    /// <summary>The lexical query this replaced, verbatim apart from the inlined eligibility clause.</summary>
    private List<(string, string, double, int)> Reference(string kind, string query, int count, string? scope,
        IReadOnlyCollection<string>? excluded)
    {
        var collation = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? "NOCASE" : "BINARY";
        var words = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{N}]+").Select(match => match.Value)
            .Where(word => word.Length > 1 && !StopWords.Contains(word)).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        var fts = words.Length == 0 ? "\"\"" : string.Join(" OR ", words.Select(word => "\"" + word.Replace("\"", "\"\"") + "\""));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SearchPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            WITH candidates AS (
                SELECT d.Id,s.Id AS SourceId,c.Id AS ChunkId,coalesce(c.Position,0) AS Position,
                    CASE WHEN $query='' THEN 0 ELSE
                        3*(instr(lower(coalesce(c.Text,s.Text)),lower($query))>0)+
                        6*(instr(lower(s.Title),lower($query))>0)+
                        (SELECT count(*) FROM json_each($words) WHERE instr(lower(coalesce(c.Text,s.Text)),lower(value))>0)
                    END AS score,
                    CASE WHEN lower(d.Id)=lower($query) OR
                        lower(json_extract(d.Metadata,'$.board.key'))=lower($query) OR
                        lower(json_extract(d.Metadata,'$.board.displayId'))=lower($query) OR
                        lower(json_extract(d.Metadata,'$.board.keyPrefix')||'-'||json_extract(d.Metadata,'$.board.number'))=lower($query) OR
                        lower('VB-'||json_extract(d.Metadata,'$.board.number'))=lower($query) THEN 1 ELSE 0 END AS identityMatch
                FROM SearchDocuments d JOIN SearchSources s ON s.DocumentId=d.Id
                LEFT JOIN SearchChunks c ON d.Kind='session' AND c.SourceId=s.Id AND s.ChunkVersion=$chunks
                WHERE d.Kind=$kind AND ($scope IS NULL OR d.ProjectPath=$scope COLLATE {collation})
                  AND ($excluded IS NULL OR d.Id NOT IN (SELECT value FROM json_each($excluded)))
                  AND ($query='' OR instr(lower(coalesce(c.Text,s.Text)),lower($query))>0 OR d.Kind='board'
                    OR s.rowid IN (SELECT rowid FROM SearchSources_fts WHERE SearchSources_fts MATCH $fts))
            ), ranked AS (
                SELECT *,row_number() OVER(PARTITION BY Id ORDER BY score DESC,Position) AS rank FROM candidates
            ) SELECT d.Id,substr(coalesce(c.Text,s.Text),max(1,coalesce(
                    (SELECT min(nullif(instr(lower(coalesce(c.Text,s.Text)),lower(value)),0)) FROM json_each($words)),1)-70),520),score,ranked.Position
                FROM ranked JOIN SearchDocuments d ON d.Id=ranked.Id JOIN SearchSources s ON s.Id=ranked.SourceId
                LEFT JOIN SearchChunks c ON c.Id=ranked.ChunkId
                WHERE rank=1 AND ($query='' OR score>0 OR identityMatch=1)
                ORDER BY identityMatch DESC,score DESC,json_extract(d.Metadata,'$.board.updatedUtc') DESC,ranked.Id LIMIT $count
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$scope", (object?)scope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$excluded", excluded is null ? DBNull.Value
            : JsonSerializer.Serialize(excluded.ToList(), StorageJsonSerializerContext.Default.ListString));
        cmd.Parameters.AddWithValue("$query", query);
        cmd.Parameters.AddWithValue("$words", JsonSerializer.Serialize(words.ToList(), StorageJsonSerializerContext.Default.ListString));
        cmd.Parameters.AddWithValue("$fts", fts);
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$chunks", SearchIndexVersions.Chunks);
        var rows = new List<(string, string, double, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetInt32(3)));
        return rows;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "card", "cards", "where", "what", "that", "this", "about", "with", "did", "was", "for", "how", "have", "from", "and", "are", "can", "to", "of" };

    /// <summary>
    /// Board, input and session documents in two projects. Sources mix vocabulary words in random case;
    /// session sources have chunks, some at an old chunker version. Board metadata carries keys, display
    /// ids and prefixes, so identity matches and ties on updatedUtc both occur. Returns some board keys.
    /// </summary>
    private List<string> Seed(Random random, int documents, int textLength = 400)
    {
        SearchDatabaseSchema.Ensure(SearchPath);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = SearchPath, Pooling = false }.ToString());
        db.Open();
        using var tx = db.BeginTransaction();
        void Run(string sql, params (string, object?)[] parameters)
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        string Text(int length)
        {
            var text = new System.Text.StringBuilder();
            while (text.Length < length)
            {
                var word = Vocabulary[random.Next(Vocabulary.Length)];
                text.Append(random.Next(3) switch { 0 => word.ToUpperInvariant(), 1 => word.ToLowerInvariant(), _ => word })
                    .Append(random.Next(12) == 0 ? "\n" : " ").Append(random.Next(4) == 0 ? "filler " : "");
            }
            return text.ToString();
        }

        var keys = new List<string>();
        foreach (var kind in new[] { "board", "input", "session" })
        for (var p = 0; p < 2; p++)
        for (var n = 0; n < documents; n++)
        {
            var id = $"{kind}-{p}-{n}";
            var project = p == 0 ? ProjectA : ProjectB;
            BoardSearchDocument? board = null;
            if (kind == "board")
            {
                var prefix = p == 0 ? "VIBE" : "TASK";
                var key = BoardKeys.Format(prefix, n + 1) + (n % 3 == 0 ? "" : "-" + random.Next(1000, 9999));
                board = new(id, key, n % 4 == 0 ? $"DISP-{n}" : key, "Card " + n, "b" + p, "Board", "c", "Ready", project,
                    n + 1, prefix, new DateTime(2026, 11, 1 + n % 5, 0, 0, 0, DateTimeKind.Utc), "task", "medium", null, false, false, []);
                if (keys.Count < 12) keys.Add(key);
            }
            Run("INSERT INTO SearchDocuments(Id,Kind,ProjectPath,SessionId,Metadata,SeenEpoch) VALUES($id,$kind,$project,NULL,$meta,1)",
                ("$id", id), ("$kind", kind), ("$project", project),
                ("$meta", JsonSerializer.Serialize(new SearchDocument(id, kind, project, [], board), StorageJsonSerializerContext.Default.SearchDocument)));
            for (var s = 0; s < 1 + random.Next(3); s++)
            {
                var source = $"{id}/s{s}";
                var chunkVersion = random.Next(4) == 0 ? "old-chunker" : SearchIndexVersions.Chunks;
                Run("INSERT INTO SearchSources(Id,DocumentId,SourceKey,Text,Title,Hash,ChunkVersion) VALUES($id,$doc,$key,$text,$title,$id,$version)",
                    ("$id", source), ("$doc", id), ("$key", "s" + s), ("$text", Text(textLength + random.Next(textLength))),
                    ("$title", random.Next(3) == 0 ? Text(20) : "Title " + n), ("$version", chunkVersion));
                if (kind == "session")
                    for (var c = 0; c < 1 + random.Next(4); c++)
                        Run("INSERT INTO SearchChunks(Id,SourceId,Position,Text,Hash,ModelVersion) VALUES($id,$source,$position,$text,$id,NULL)",
                            ("$id", $"{source}:{c}"), ("$source", source), ("$position", c), ("$text", Text(150)));
            }
        }
        tx.Commit();
        return keys;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
