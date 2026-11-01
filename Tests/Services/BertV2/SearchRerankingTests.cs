using Moq;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class SearchRerankingTests
{
    private sealed class Scores(Func<IReadOnlyList<string>, float[]> score) : ISearchReranker
    {
        public int Calls;
        public bool IsAvailable { get; init; } = true;
        public float[] Score(string query, IReadOnlyList<string> passages) { Calls++; return score(passages); }
        public void Dispose() { }
    }

    [Fact]
    public void ReordersOnlyTheCandidatePoolAndKeepsTheTailInHybridOrder()
    {
        var ranked = Enumerable.Range(0, SearchReranking.Candidates + 5).Select(i => "doc" + i).ToArray();
        var reranker = new Scores(passages => passages.Select(p => p == "doc7" ? 5f : p == "doc3" ? 4f : -3f).ToArray());

        var result = SearchReranking.Apply(reranker, "query", ranked, p => p, null);

        Assert.Equal(["doc7", "doc3", "doc0", "doc1", "doc2", "doc4"], result.Take(6));
        Assert.Equal(ranked.Skip(SearchReranking.Candidates), result.Skip(SearchReranking.Candidates));
        Assert.Equal(ranked.Order(), result.Order());
    }

    [Fact]
    public void BoostStaysBoundedSoAMuchStrongerMatchStillWins()
    {
        string[] ranked = ["local-weak", "foreign-strong", "local-close"];
        var reranker = new Scores(_ => [0f, 3f, 2.9f]);

        var result = SearchReranking.Apply(reranker, "query", ranked, p => p, null, p => p.StartsWith("local", StringComparison.Ordinal) ? 1.1 : 1);

        // sigmoid(3)=0.953 beats 1.1*sigmoid(0)=0.55, but 1.1*sigmoid(2.9)=1.04 beats it.
        Assert.Equal(["local-close", "foreign-strong", "local-weak"], result);
    }

    [Fact]
    public void FailuresMissingModelsAndTrivialInputsKeepTheHybridOrder()
    {
        string[] ranked = ["a", "b", "c"];
        Assert.Same(ranked, SearchReranking.Apply(null, "query", ranked, p => p, null));
        Assert.Same(ranked, SearchReranking.Apply(new Scores(_ => throw new FileNotFoundException("model.onnx")), "query", ranked, p => p, null));
        Assert.Same(ranked, SearchReranking.Apply(new Scores(_ => throw new InvalidOperationException("onnx")), "query", ranked, p => p, null));
        Assert.Same(ranked, SearchReranking.Apply(new Scores(_ => [1f]), "query", ranked, p => p, null));
        var unused = new Scores(_ => [1f, 2f, 3f]);
        Assert.Same(ranked, SearchReranking.Apply(unused, " ", ranked, p => p, null));
        Assert.Equal(0, unused.Calls);
        var absent = new Scores(_ => [3f, 2f, 1f]) { IsAvailable = false };
        Assert.Same(ranked, SearchReranking.Apply(absent, "query", ranked, p => p, null));
        Assert.Equal(0, absent.Calls);
    }

    [Fact]
    public void IdleOwnerLoadsOnFirstUseAndReleasesTheModel()
    {
        var model = new Mock<ISearchReranker>();
        model.Setup(m => m.Score(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>())).Returns([1f]);
        var loads = 0;
        var installed = false;
        var owner = new IdleSearchReranker(() => { loads++; return model.Object; }, () => installed, idleTimeout: TimeSpan.FromHours(1));
        Assert.False(owner.IsAvailable);
        installed = true;
        Assert.True(owner.IsAvailable);
        Assert.Equal(0, loads);
        Assert.Equal([1f], owner.Score("q", ["p"]));
        owner.Score("q", ["p"]);
        Assert.Equal(1, loads);
        owner.Dispose();
        model.Verify(m => m.Dispose(), Times.Once);
        Assert.Throws<ObjectDisposedException>(() => owner.Score("q", ["p"]));
    }
}
