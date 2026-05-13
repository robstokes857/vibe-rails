using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public class BertSearchServiceV2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runtimeDir;
    private readonly BertV2BgeEmbedder _embedder;
    private readonly BertV2VectorStore _store;
    private readonly BertV2InputService _inputService;
    private readonly BertSearchDbService _searchDb;
    private readonly BertSearchServiceV2 _searchService;

    public BertSearchServiceV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bert_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _runtimeDir = Path.Combine(_tempDir, "runtime");
        BertV2TestAssets.MaterializeRuntimeFilesAsync(_runtimeDir).GetAwaiter().GetResult();

        _embedder = new BertV2BgeEmbedder(
            Path.Combine(_runtimeDir, "model.onnx"),
            Path.Combine(_runtimeDir, "vocab.txt"));
        _store = new BertV2VectorStore(Path.Combine(_tempDir, "bert_user_text_vectors.db"));
        _inputService = new BertV2InputService(_embedder, _store);
        // Pin state.db to a fresh, empty file so FTS5 lookups in SearchByText
        // don't accidentally resolve against the developer's real state.db.
        _searchDb = new BertSearchDbService(
            new TestBertSettings(_runtimeDir, _tempDir),
            stateDatabasePathOverride: Path.Combine(_tempDir, "state.db"));
        _searchService = new BertSearchServiceV2(
            new IBertSearchStrategy[]
            {
                new SemanticBertSearchStrategy(_embedder, _searchDb),
                new TextBertSearchStrategy(_searchDb),
            },
            _searchDb,
            new BertDocumentResponseMapper());
    }

    [Fact]
    public void Capture_StoresDocument_SearchFindsIt()
    {
        _inputService.Capture("session-1", 1, "How do I reset my password?");

        var response = _searchService.Search("password reset", "semantic", topK: 5);
        var results = response.Results;

        Assert.Single(results);
        Assert.Equal("session-1:1", results[0].DocumentId);
    }

    [Fact]
    public void Search_RanksSemanticallySimialarDocumentsHigher()
    {
        _inputService.Capture("s1", 1, "How do I reset my password?");
        _inputService.Capture("s1", 2, "The server crashed with an out of memory error");
        _inputService.Capture("s1", 3, "Deploy the application to production");
        _inputService.Capture("s1", 4, "Configure nginx reverse proxy settings");
        _inputService.Capture("s1", 5, "I forgot my login credentials");

        var response = _searchService.Search("I can't log in to my account", "semantic", topK: 5);
        var results = response.Results;

        // The password/credentials entries should rank above server crashes and deployments
        var top2Ids = results.Take(2).Select(r => r.DocumentId).ToList();
        Assert.Contains("s1:1", top2Ids);
        Assert.Contains("s1:5", top2Ids);
    }

    [Fact]
    public void Search_TopKLimitsResults()
    {
        for (int i = 1; i <= 10; i++)
            _inputService.Capture("s1", i, $"Document number {i} about various topics");

        var response = _searchService.Search("document topics", "semantic", topK: 3);
        var results = response.Results;

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_TextModeUsesKeywordMatching()
    {
        _inputService.Capture("s1", 1, "customer account settings");
        _inputService.Capture("s1", 2, "billing invoice summary");

        var response = _searchService.Search("invoice", "text", topK: 5);

        Assert.Single(response.Results);
        Assert.Equal("s1:2", response.Results[0].DocumentId);
        Assert.Null(response.Results[0].Score);
    }

    [Fact]
    public void Search_ScoresAreBetweenZeroAndOne()
    {
        _inputService.Capture("s1", 1, "Machine learning model training");
        _inputService.Capture("s1", 2, "Database migration scripts");

        var response = _searchService.Search("train a neural network", "semantic", topK: 2);
        var results = response.Results;

        Assert.All(results, r =>
        {
            Assert.NotNull(r.Score);
            Assert.InRange(r.Score!.Value, 0.0, 1.0);
        });
    }

    [Fact]
    public void Search_UnrelatedQueryScoresLow()
    {
        _inputService.Capture("s1", 1, "Fix the CSS styling on the login page");

        var response = _searchService.Search("quantum physics thermodynamics", "semantic", topK: 1);
        var results = response.Results;

        Assert.Single(results);
        Assert.NotNull(results[0].Score);
        Assert.True(results[0].Score < 0.5, $"Expected low score for unrelated query, got {results[0].Score}");
    }

    [Fact]
    public void Capture_UpdatesExistingDocument()
    {
        _inputService.Capture("s1", 1, "Original text about cooking recipes");
        _inputService.Capture("s1", 1, "Updated text about server deployment");

        var response = _searchService.Search("deploy to production", "semantic", topK: 1);
        var results = response.Results;

        Assert.Single(results);
        Assert.Equal("s1:1", results[0].DocumentId);
        Assert.Equal("Updated text about server deployment", _searchDb.GetCapture("s1:1")?.RawText);
    }

    public void Dispose()
    {
        _embedder.Dispose();
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class TestBertSettings : VibeRails.Services.BertBaseClasses.IBertSettings
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
