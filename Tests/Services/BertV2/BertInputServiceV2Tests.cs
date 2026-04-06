using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public class TestBertSettings : IBertSettings
{
    public string ModelPath { get; }
    public string VocabPath { get; }
    public string DataDirectory { get; }

    public TestBertSettings(string runtimeDir, string dataDirectory)
    {
        ModelPath = Path.Combine(runtimeDir, "model.onnx");
        VocabPath = Path.Combine(runtimeDir, "vocab.txt");
        DataDirectory = dataDirectory;
    }
}

public class BertInputServiceV2Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runtimeDir;
    private readonly BertV2InputService _service;

    public BertInputServiceV2Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"bert_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _runtimeDir = Path.Combine(_tempDir, "runtime");
        BertV2TestAssets.MaterializeRuntimeFilesAsync(_runtimeDir).GetAwaiter().GetResult();

        _service = new BertV2InputService(new TestBertSettings(_runtimeDir, _tempDir));
    }

    [Fact]
    public void Capture_StoresDocument_SearchFindsIt()
    {
        _service.Capture("session-1", 1, "How do I reset my password?");

        var results = _service.Search("password reset", topK: 5);

        Assert.Single(results);
        Assert.Equal("session-1:1", results[0].Id);
    }

    [Fact]
    public void Search_RanksSemanticallySimialarDocumentsHigher()
    {
        _service.Capture("s1", 1, "How do I reset my password?");
        _service.Capture("s1", 2, "The server crashed with an out of memory error");
        _service.Capture("s1", 3, "Deploy the application to production");
        _service.Capture("s1", 4, "Configure nginx reverse proxy settings");
        _service.Capture("s1", 5, "I forgot my login credentials");

        var results = _service.Search("I can't log in to my account", topK: 5);

        // The password/credentials entries should rank above server crashes and deployments
        var top2Ids = results.Take(2).Select(r => r.Id).ToList();
        Assert.Contains("s1:1", top2Ids);
        Assert.Contains("s1:5", top2Ids);
    }

    [Fact]
    public void Search_TopKLimitsResults()
    {
        for (int i = 1; i <= 10; i++)
            _service.Capture("s1", i, $"Document number {i} about various topics");

        var results = _service.Search("document topics", topK: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_ScoresAreBetweenZeroAndOne()
    {
        _service.Capture("s1", 1, "Machine learning model training");
        _service.Capture("s1", 2, "Database migration scripts");

        var results = _service.Search("train a neural network", topK: 2);

        Assert.All(results, r =>
        {
            Assert.InRange(r.Score, 0.0, 1.0);
        });
    }

    [Fact]
    public void Search_UnrelatedQueryScoresLow()
    {
        _service.Capture("s1", 1, "Fix the CSS styling on the login page");

        var results = _service.Search("quantum physics thermodynamics", topK: 1);

        Assert.Single(results);
        Assert.True(results[0].Score < 0.5, $"Expected low score for unrelated query, got {results[0].Score}");
    }

    [Fact]
    public void Capture_UpdatesExistingDocument()
    {
        _service.Capture("s1", 1, "Original text about cooking recipes");
        _service.Capture("s1", 1, "Updated text about server deployment");

        var results = _service.Search("deploy to production", topK: 1);

        Assert.Single(results);
        Assert.Equal("s1:1", results[0].Id);
        Assert.Equal("Updated text about server deployment", results[0].Text);
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
