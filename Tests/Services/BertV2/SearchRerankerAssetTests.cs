using System.IO.Compression;
using System.Security.Cryptography;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// The checked-in re-ranking model (VIBE-258) loads, ranks, and is found where each install puts it:
/// <c>~/.vibe_rails/models/reranker</c> first, then <c>Models/Reranker</c> beside the executable.
/// </summary>
public sealed class SearchRerankerAssetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vb-reranker-" + Guid.NewGuid().ToString("N"));
    private readonly string installed;
    private readonly string bundled;

    public SearchRerankerAssetTests()
    {
        installed = Path.Combine(root, "installed");
        bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(bundled);
    }

    [Fact]
    public void ShippedModelLoadsFromTheBundleAndRanksTheRelevantPassageFirst()
    {
        var hash = Materialize(bundled);
        // The attribution names the exact upstream file that ships.
        Assert.Contains(hash, File.ReadAllText(Path.Combine(BertV2TestAssets.GetBundledDirectory("Reranker"), "LICENSE.txt")));

        using var owner = SearchRerankerSettings.CreateOwner(installed, bundled);
        Assert.True(owner.IsAvailable);
        var scores = owner.Score("How many people live in Berlin?",
            ["Berlin had a population of 3,520,031 registered inhabitants in an area of 891.82 square kilometers.",
             "Berlin is well known for its museums."]);
        Assert.True(scores[0] > scores[1], $"Expected the population passage first: [{scores[0]}, {scores[1]}]");
    }

    [Fact]
    public void InstalledModelWinsAndAnEmptyLayoutLeavesRerankingOff()
    {
        using (var none = SearchRerankerSettings.CreateOwner(installed, bundled))
            Assert.False(none.IsAvailable);

        // Release archives carry only the compressed model beside the executable.
        File.WriteAllText(Path.Combine(bundled, "model.onnx.gz"), "compressed");
        Assert.Null(SearchRerankerSettings.FindModelDirectory([installed, bundled]));

        File.WriteAllText(Path.Combine(bundled, "model.onnx"), "bundled");
        Assert.Equal(bundled, SearchRerankerSettings.FindModelDirectory([installed, bundled]));

        File.WriteAllText(Path.Combine(installed, "model.onnx"), "installed");
        Assert.Equal(installed, SearchRerankerSettings.FindModelDirectory([installed, bundled]));
    }

    [Fact]
    public void DefaultLocationsAreTheInstallDirectoryAndTheExecutableFolder()
    {
        Assert.EndsWith(Path.Combine(".vibe_rails", "models", "reranker"), SearchRerankerSettings.InstalledDirectory);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Models", "Reranker"), SearchRerankerSettings.BundledDirectory);
    }

    /// <summary>Extracts the checked-in model and vocabulary, as an installer does; returns the model's SHA-256.</summary>
    private static string Materialize(string directory)
    {
        var source = BertV2TestAssets.GetBundledDirectory("Reranker");
        var model = Path.Combine(directory, "model.onnx");
        using (var archive = File.OpenRead(Path.Combine(source, "model.onnx.gz")))
        using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
        using (var destination = File.Create(model))
            gzip.CopyTo(destination);
        File.Copy(Path.Combine(source, "vocab.txt"), Path.Combine(directory, "vocab.txt"));
        using var stream = File.OpenRead(model);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
