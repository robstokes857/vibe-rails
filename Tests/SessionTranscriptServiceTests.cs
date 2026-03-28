using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class SessionTranscriptServiceTests
{
    [Fact]
    public async Task GetOrBuildAsync_CallsParseTranscriptAsync_WithAllChunksAndUserInputs()
    {
        const string sessionId = "session-1";

        var chunks = new List<SessionLogChunkRecord>
        {
            new(1, DateTime.UtcNow, new byte[] { 1 }),
            new(2, DateTime.UtcNow.AddSeconds(1), new byte[] { 2 }),
        };

        var userInputs = new List<UserInputRecord>
        {
            new(1, sessionId, 1, "First question", null, DateTime.UtcNow),
        };

        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetSessionOutputAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionOutputDetailResponse?)null);
        repository
            .Setup(r => r.GetSessionLogChunksAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        repository
            .Setup(r => r.GetUserInputsForSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(userInputs);
        repository
            .Setup(r => r.SaveSessionOutputAndMarkProcessedAsync(sessionId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var parser = new Mock<ISessionOutputParser>(MockBehavior.Strict);
        parser
            .Setup(p => p.ParseTranscriptAsync(chunks, userInputs, It.IsAny<CancellationToken>()))
            .ReturnsAsync("User: First question\n\nAgent: The answer");

        var sut = new SessionTranscriptService(repository.Object, parser.Object);
        var result = await sut.GetOrBuildAsync(sessionId, CancellationToken.None);

        Assert.Equal("User: First question\n\nAgent: The answer", result);
        parser.Verify(p => p.ParseTranscriptAsync(chunks, userInputs, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyAll();
    }

    [Fact]
    public async Task GetOrBuildAsync_ReturnsCachedTranscript_WhenAvailable()
    {
        const string sessionId = "session-cached";

        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository
            .Setup(r => r.GetSessionOutputAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionOutputDetailResponse(sessionId, "Claude", null, "/tmp", DateTime.UtcNow, null, true, "Cached transcript"));

        var parser = new Mock<ISessionOutputParser>(MockBehavior.Strict);

        var sut = new SessionTranscriptService(repository.Object, parser.Object);
        var result = await sut.GetOrBuildAsync(sessionId, CancellationToken.None);

        Assert.Equal("Cached transcript", result);
        parser.Verify(p => p.ParseTranscriptAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<IReadOnlyList<UserInputRecord>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
