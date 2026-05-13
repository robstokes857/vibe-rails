using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// End-to-end tests for the new <see cref="UnifiedSearchService"/>: builds a synthetic
/// state.db (real schema, real FTS5 triggers, a few user inputs) and a real vector
/// store, then drives the unified search and validates the four returned groups
/// (per-message, per-session, lexical via FTS5, fused via RRF).
/// </summary>
public class UnifiedSearchServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runtimeDir;
    private readonly string _stateDbPath;
    private readonly BertV2BgeEmbedder _embedder;
    private readonly BertV2VectorStore _store;
    private readonly BertV2SessionVectorStore _sessionStore;
    private readonly BertV2InputService _inputService;
    private readonly BertV2SessionEmbeddingService _sessionService;
    private readonly BertSearchDbService _searchDb;
    private readonly UnifiedSearchService _unifiedSearch;

    public UnifiedSearchServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"unified_search_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _runtimeDir = Path.Combine(_tempDir, "runtime");
        BertV2TestAssets.MaterializeRuntimeFilesAsync(_runtimeDir).GetAwaiter().GetResult();

        _stateDbPath = Path.Combine(_tempDir, "state.db");
        InitializeStateDb(_stateDbPath);

        _embedder = new BertV2BgeEmbedder(
            Path.Combine(_runtimeDir, "model.onnx"),
            Path.Combine(_runtimeDir, "vocab.txt"));

        var vectorDbPath = Path.Combine(_tempDir, "bert_user_text_vectors.db");
        _store = new BertV2VectorStore(vectorDbPath);
        _sessionStore = new BertV2SessionVectorStore(vectorDbPath);
        _inputService = new BertV2InputService(_embedder, _store);
        _sessionService = new BertV2SessionEmbeddingService(_embedder, _sessionStore);
        _searchDb = new BertSearchDbService(
            new TestBertSettings(_runtimeDir, _tempDir),
            stateDatabasePathOverride: _stateDbPath);
        _unifiedSearch = new UnifiedSearchService(_embedder, _searchDb, new BertDocumentResponseMapper());
    }

    [Fact]
    public void Search_ReturnsFourGroups_InExpectedOrder()
    {
        SeedUserInput("s1", 1, "How do I reset my password?");
        _inputService.Capture("s1", 1, "How do I reset my password?");

        var response = _unifiedSearch.Search("password reset", topK: 5);

        Assert.Equal(4, response.Groups.Count);
        Assert.Equal("per-message-semantic", response.Groups[0].Key);
        Assert.Equal("per-session-semantic", response.Groups[1].Key);
        Assert.Equal("lexical", response.Groups[2].Key);
        Assert.Equal("fused", response.Groups[3].Key);
    }

    [Fact]
    public void Search_PerMessageSemantic_FindsCapturedDocument()
    {
        SeedUserInput("s1", 1, "How do I reset my password?");
        _inputService.Capture("s1", 1, "How do I reset my password?");

        var response = _unifiedSearch.Search("forgot login", topK: 5);

        var perMessage = response.Groups[0];
        Assert.NotEmpty(perMessage.Hits);
        Assert.Equal("s1:1", perMessage.Hits[0].DocumentId);
        Assert.Equal("input", perMessage.Hits[0].Kind);
    }

    [Fact]
    public void Search_LexicalGroup_UsesFts5_AgainstStateDb()
    {
        // Two messages, one with the literal token, one without.
        SeedUserInput("s1", 1, "billing invoice summary");
        SeedUserInput("s1", 2, "customer account settings");
        _inputService.Capture("s1", 1, "billing invoice summary");
        _inputService.Capture("s1", 2, "customer account settings");

        var response = _unifiedSearch.Search("invoice", topK: 5);

        var lexical = response.Groups[2];
        Assert.NotEmpty(lexical.Hits);
        Assert.Contains(lexical.Hits, h => h.DocumentId == "s1:1");
        Assert.DoesNotContain(lexical.Hits, h => h.DocumentId == "s1:2");
    }

    [Fact]
    public void Search_FusedGroup_AppliesRrf_AcrossSourceGroups()
    {
        SeedUserInput("s1", 1, "deploy the application to production");
        SeedUserInput("s1", 2, "fix the css styling on the login page");
        _inputService.Capture("s1", 1, "deploy the application to production");
        _inputService.Capture("s1", 2, "fix the css styling on the login page");

        var response = _unifiedSearch.Search("deploy production release", topK: 5);

        var fused = response.Groups[3];
        Assert.NotEmpty(fused.Hits);
        // Fused score is RRF sum (Σ 1/(60 + rank)) → always positive, well below 1.0
        // even when a doc tops every group (3/61 ≈ 0.049). Sanity-check the bound.
        Assert.All(fused.Hits, h =>
        {
            Assert.NotNull(h.Score);
            Assert.InRange(h.Score!.Value, 0.0, 1.0);
        });
        // Top hit should be the deploy/production message — it scores in semantic AND
        // lexical groups, so RRF promotes it over single-group hits.
        Assert.Equal("s1:1", fused.Hits[0].DocumentId);
    }

    [Fact]
    public void Search_PerSessionGroup_FindsSessionAggregateChunks()
    {
        // Capture two sessions worth of inputs, then aggregate-embed each as one
        // chunk per session.
        SeedSession("s1", "Codex", endedSecondsAgo: 100);
        SeedSession("s2", "Claude", endedSecondsAgo: 60);
        SeedUserInput("s1", 1, "review my pull request and tell me about the database migration");
        SeedUserInput("s2", 1, "what is the weather like in Paris");
        _sessionService.CaptureSession("s1", new[] { "review my pull request and tell me about the database migration" });
        _sessionService.CaptureSession("s2", new[] { "what is the weather like in Paris" });

        var response = _unifiedSearch.Search("schema change in the migration", topK: 5);

        var perSession = response.Groups[1];
        Assert.NotEmpty(perSession.Hits);
        Assert.Equal("session-chunk", perSession.Hits[0].Kind);
        // The migration session should rank above the weather session.
        var topSessionId = perSession.Hits[0].SessionId;
        Assert.Equal("s1", topSessionId);
    }

    [Fact]
    public void BuildFusedGroup_DeduplicatesAcrossSources_AndSumsRrfScores()
    {
        // Pure RRF math — no DB or embedder needed. The same DocumentId in three groups
        // at rank 1 each should produce score = 3/61.
        var hit = MakeHit("doc-A");
        var solo = MakeHit("doc-B");

        var perMessage = MakeGroup("per-message-semantic", new[] { hit, solo });
        var perSession = MakeGroup("per-session-semantic", new[] { hit });
        var lexical = MakeGroup("lexical", new[] { hit });

        var fused = UnifiedSearchService.BuildFusedGroup(perMessage, perSession, lexical, topK: 10);

        Assert.Equal(2, fused.Hits.Count);
        Assert.Equal("doc-A", fused.Hits[0].DocumentId);
        Assert.Equal("doc-B", fused.Hits[1].DocumentId);
        Assert.Equal(3.0 / 61.0, fused.Hits[0].Score!.Value, 6);

        // doc-B appears once at rank 2 of per-message → 1/62.
        Assert.Equal(1.0 / 62.0, fused.Hits[1].Score!.Value, 6);
    }

    [Fact]
    public void BuildFusedGroup_TruncatesToTopK()
    {
        var hits = Enumerable.Range(0, 25)
            .Select(i => MakeHit($"doc-{i}"))
            .ToArray();

        var perMessage = MakeGroup("per-message-semantic", hits);
        var empty = MakeGroup("per-session-semantic", Array.Empty<BertSearchHitResponse>());
        var lexical = MakeGroup("lexical", Array.Empty<BertSearchHitResponse>());

        var fused = UnifiedSearchService.BuildFusedGroup(perMessage, empty, lexical, topK: 7);

        Assert.Equal(7, fused.Hits.Count);
        Assert.Equal("doc-0", fused.Hits[0].DocumentId);
        Assert.Equal("doc-6", fused.Hits[6].DocumentId);
    }

    [Fact]
    public void SessionEmbedding_ComputeChunks_ReturnsSingleChunk_WhenUnderWindow()
    {
        var chunks = BertV2SessionEmbeddingService.ComputeChunks("hello world");
        Assert.Single(chunks);
        Assert.Equal("hello world", chunks[0]);
    }

    [Fact]
    public void SessionEmbedding_ComputeChunks_SlidesWith50PercentOverlap()
    {
        // 4000-char block → expected windows at offsets 0, 800, 1600, 2400 (length 1600
        // each, last one truncated to 1600 bytes ending at 4000).
        var text = new string('x', 4000);

        var chunks = BertV2SessionEmbeddingService.ComputeChunks(text);

        // Windows: [0..1600], [800..2400], [1600..3200], [2400..4000] → 4 chunks.
        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(1600, c.Length));
    }

    [Fact]
    public void SessionDocumentId_RoundTrips()
    {
        var id = BertSessionDocumentId.Create("session-abc", 7);
        var parsed = BertSessionDocumentId.Parse(id);

        Assert.NotNull(parsed);
        Assert.Equal("session-abc", parsed!.SessionId);
        Assert.Equal(7, parsed.ChunkIndex);
    }

    [Fact]
    public void SessionDocumentId_RejectsPerMessageIds()
    {
        // Per-message ids look like "<sessionId>:<userInputId>" without the "sess:" prefix.
        Assert.Null(BertSessionDocumentId.Parse("session-abc:42"));
    }

    public void Dispose()
    {
        _embedder.Dispose();
        _store.Dispose();
        _sessionStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void InitializeStateDb(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWriteCreate");
        connection.Open();

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = SqlStrings.PragmaForeignKeys;
            pragmaCmd.ExecuteNonQuery();
        }

        foreach (var sql in SqlStrings.InitStatements)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        foreach (var migration in SqlStrings.MigrationStatements)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = migration;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Migrations are safe to re-run; ignore already-applied errors.
            }
        }
    }

    private void SeedSession(string sessionId, string cli, int endedSecondsAgo)
    {
        using var connection = new SqliteConnection($"Data Source={_stateDbPath};Mode=ReadWrite");
        connection.Open();

        var startedUtc = DateTime.UtcNow.AddSeconds(-endedSecondsAgo - 60).ToString("O");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = SqlStrings.InsertSession;
            insert.Parameters.AddWithValue("$id", sessionId);
            insert.Parameters.AddWithValue("$cli", cli);
            insert.Parameters.AddWithValue("$envName", DBNull.Value);
            insert.Parameters.AddWithValue("$workDir", "C:\\test");
            insert.Parameters.AddWithValue("$projectDisplayName", "Test");
            insert.Parameters.AddWithValue("$startedUTC", startedUtc);
            insert.Parameters.AddWithValue("$ownerPid", 1234);
            insert.ExecuteNonQuery();
        }
        using (var end = connection.CreateCommand())
        {
            end.CommandText = SqlStrings.UpdateSessionEnd;
            end.Parameters.AddWithValue("$id", sessionId);
            end.Parameters.AddWithValue("$endedUTC", DateTime.UtcNow.AddSeconds(-endedSecondsAgo).ToString("O"));
            end.Parameters.AddWithValue("$exitCode", 0);
            end.ExecuteNonQuery();
        }
    }

    private void SeedUserInput(string sessionId, int sequence, string text)
    {
        EnsureSessionRow(sessionId);

        using var connection = new SqliteConnection($"Data Source={_stateDbPath};Mode=ReadWrite");
        connection.Open();
        long userInputId;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SqlStrings.InsertUserInput;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            cmd.Parameters.AddWithValue("$sequence", sequence);
            cmd.Parameters.AddWithValue("$inputText", text);
            cmd.Parameters.AddWithValue("$gitCommitHash", DBNull.Value);
            cmd.Parameters.AddWithValue("$timestampUTC", DateTime.UtcNow.ToString("O"));
            userInputId = (long)cmd.ExecuteScalar()!;
        }

        // Mirror Repository.InsertUserInputAsync: write the FTS row through the same
        // InputEtlFilter the app uses, so lexical search has something to match.
        var safe = VibeRails.Services.UserInOut.InputEtlFilter.Process(text);
        if (!string.IsNullOrWhiteSpace(safe))
        {
            using var ftsCmd = connection.CreateCommand();
            ftsCmd.CommandText = SqlStrings.InsertUserInputsFtsRow;
            ftsCmd.Parameters.AddWithValue("$rowid", userInputId);
            ftsCmd.Parameters.AddWithValue("$inputText", safe);
            ftsCmd.ExecuteNonQuery();
        }
    }

    private void EnsureSessionRow(string sessionId)
    {
        using var connection = new SqliteConnection($"Data Source={_stateDbPath};Mode=ReadWrite");
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM Sessions WHERE Id = $id LIMIT 1;";
        check.Parameters.AddWithValue("$id", sessionId);
        var exists = check.ExecuteScalar() is not null;
        if (exists) return;

        using var insert = connection.CreateCommand();
        insert.CommandText = SqlStrings.InsertSession;
        insert.Parameters.AddWithValue("$id", sessionId);
        insert.Parameters.AddWithValue("$cli", "Codex");
        insert.Parameters.AddWithValue("$envName", DBNull.Value);
        insert.Parameters.AddWithValue("$workDir", "C:\\test");
        insert.Parameters.AddWithValue("$projectDisplayName", "Test");
        insert.Parameters.AddWithValue("$startedUTC", DateTime.UtcNow.AddSeconds(-120).ToString("O"));
        insert.Parameters.AddWithValue("$ownerPid", 1234);
        insert.ExecuteNonQuery();
    }

    private static BertSearchHitResponse MakeHit(string documentId) => new BertSearchHitResponse(
        DocumentId: documentId,
        SessionId: "session",
        UserInputId: null,
        Sequence: null,
        TimestampUTC: null,
        Cli: null,
        EnvironmentName: null,
        WorkingDirectory: null,
        GitCommitHash: null,
        UserTextPreview: documentId,
        FileChangeCount: 0,
        Score: null,
        Kind: "input",
        ChunkIndex: null);

    private static UnifiedSearchHitGroup MakeGroup(string key, IReadOnlyList<BertSearchHitResponse> hits) => new UnifiedSearchHitGroup(
        Key: key,
        Label: key,
        Description: string.Empty,
        SearchTimeMs: 0,
        CorpusSize: hits.Count,
        Hits: hits.ToList());

    private sealed class TestBertSettings : IBertSettings
    {
        public TestBertSettings(string modelDirectory, string dataDirectory)
        {
            ModelPath = Path.Combine(modelDirectory, "model.onnx");
            VocabPath = Path.Combine(modelDirectory, "vocab.txt");
            DataDirectory = dataDirectory;
        }

        public string ModelPath { get; }
        public string VocabPath { get; }
        public string DataDirectory { get; }
        public string ModelName => "test";
        public int EmbeddingDimension => 384;
        public int MaxSequenceLength => 512;
    }
}
