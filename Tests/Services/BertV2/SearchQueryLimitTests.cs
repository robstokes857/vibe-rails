using System.Diagnostics;
using Moq;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class SearchQueryLimitTests
{
    [Theory]
    [InlineData('x')]
    [InlineData(' ')]
    public void OversizedQueryIsRejectedBeforeEmbeddingOrStorage(char value)
    {
        var db = new Mock<IBertSearchDbService>(MockBehavior.Strict);
        var mapper = new Mock<IBertDocumentResponseMapper>(MockBehavior.Strict);
        var embedder = new Mock<IBertV2BgeEmbedder>(MockBehavior.Strict);
        var strategy = new Mock<IBertSearchStrategy>(MockBehavior.Strict);
        strategy.SetupGet(s => s.Mode).Returns("text");
        strategy.SetupGet(s => s.Scope).Returns("inputs");
        var unified = new UnifiedSearchService(embedder.Object, db.Object, mapper.Object);
        var bert = new BertSearchServiceV2([strategy.Object], db.Object, mapper.Object);
        var query = new string(value, 8 * 1024 * 1024);

        var sw = Stopwatch.StartNew();
        Assert.Throws<ArgumentException>(() => unified.Search(query, 10));
        Assert.Throws<ArgumentException>(() => unified.Search(query, 10, [1f]));
        Assert.Throws<ArgumentException>(() => bert.Search(query, "semantic", "both", 10));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Query rejection took {sw.Elapsed}");
        embedder.VerifyNoOtherCalls();
        db.VerifyNoOtherCalls();
        mapper.VerifyNoOtherCalls();
    }

    [Fact]
    public void QueryLimitIsInclusive()
    {
        var strategy = new Mock<IBertSearchStrategy>();
        strategy.SetupGet(s => s.Mode).Returns("text");
        strategy.SetupGet(s => s.Scope).Returns("inputs");
        strategy.Setup(s => s.Search(It.IsAny<string>(), It.IsAny<int>())).Returns([]);
        var service = new BertSearchServiceV2([strategy.Object], Mock.Of<IBertSearchDbService>(), Mock.Of<IBertDocumentResponseMapper>());
        var query = new string('x', 1000);
        Assert.Equal(query, service.Search(query, "text", 1).Query);
        Assert.Throws<ArgumentException>(() => service.Search(query + "x", "text", 1));
        strategy.Verify(s => s.Search(query, 1), Times.Once);
    }
}
