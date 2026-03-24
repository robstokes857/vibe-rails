using Moq;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class SessionTranscriptServiceTests
{
    [Fact]
    public async Task GetOrBuildAsync_StartsWithFirstUserMessage_AndUsesSimpleLabels()
    {
        const string sessionId = "session-1";
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var chunks = new List<SessionLogChunkRecord>
        {
            new(1, baseTime.AddSeconds(-5), new byte[] { 1 }),
            new(2, baseTime.AddSeconds(1), new byte[] { 2 }),
            new(3, baseTime.AddSeconds(3), new byte[] { 3 })
        };

        var userInputs = new List<UserInputRecord>
        {
            new(1, sessionId, 1, "First question", null, baseTime),
            new(2, sessionId, 2, "Second question", null, baseTime.AddSeconds(2))
        };

        var dbService = new Mock<IDbService>(MockBehavior.Strict);
        dbService
            .Setup(service => service.GetSessionOutputAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionOutputDetailResponse?)null);
        dbService
            .Setup(service => service.GetSessionLogChunksAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        dbService
            .Setup(service => service.GetUserInputsForSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(userInputs);

        string? savedTranscript = null;
        dbService
            .Setup(service => service.SaveSessionOutputAndMarkProcessedAsync(sessionId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => savedTranscript = text)
            .Returns(Task.CompletedTask);

        var parser = new Mock<ISessionOutputParser>(MockBehavior.Strict);
        parser
            .Setup(service => service.ParseAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SessionLogChunkRecord> window, CancellationToken _) => WindowKey(window) switch
            {
                "2" => "First answer",
                "3" => "Second answer",
                _ => throw new Xunit.Sdk.XunitException($"Unexpected parse window: {WindowKey(window)}")
            });

        var sut = new SessionTranscriptService(dbService.Object, parser.Object);

        var transcript = await sut.GetOrBuildAsync(sessionId, CancellationToken.None);

        var expected = string.Join(Environment.NewLine,
            "User:",
            "First question",
            string.Empty,
            "Assistant:",
            "First answer",
            string.Empty,
            "User:",
            "Second question",
            string.Empty,
            "Assistant:",
            "Second answer");

        Assert.Equal(expected, transcript);
        Assert.Equal(expected, savedTranscript);
        Assert.StartsWith($"User:{Environment.NewLine}First question", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(">>>>>>>>", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("User said:", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("Assistant said:", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("1", transcript.Split(Environment.NewLine)[0], StringComparison.Ordinal);

        dbService.VerifyAll();
        parser.VerifyAll();
    }

    [Fact]
    public async Task GetOrBuildAsync_WithoutUserInputs_UsesParsedSessionOutput()
    {
        const string sessionId = "session-no-inputs";
        var chunks = new List<SessionLogChunkRecord>
        {
            new(10, DateTime.UtcNow, new byte[] { 10 }),
            new(11, DateTime.UtcNow.AddSeconds(1), new byte[] { 11 })
        };

        var dbService = new Mock<IDbService>(MockBehavior.Strict);
        dbService
            .Setup(service => service.GetSessionOutputAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SessionOutputDetailResponse?)null);
        dbService
            .Setup(service => service.GetSessionLogChunksAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(chunks);
        dbService
            .Setup(service => service.GetUserInputsForSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord>());

        string? savedTranscript = null;
        dbService
            .Setup(service => service.SaveSessionOutputAndMarkProcessedAsync(sessionId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, text, _) => savedTranscript = text)
            .Returns(Task.CompletedTask);

        var parser = new Mock<ISessionOutputParser>(MockBehavior.Strict);
        parser
            .Setup(service => service.ParseAsync(It.IsAny<IReadOnlyList<SessionLogChunkRecord>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Assistant-only transcript");

        var sut = new SessionTranscriptService(dbService.Object, parser.Object);

        var transcript = await sut.GetOrBuildAsync(sessionId, CancellationToken.None);

        Assert.Equal("Assistant-only transcript", transcript);
        Assert.Equal("Assistant-only transcript", savedTranscript);

        dbService.VerifyAll();
        parser.VerifyAll();
    }

    private static string WindowKey(IReadOnlyList<SessionLogChunkRecord> window)
    {
        return string.Join(",", window.Select(chunk => chunk.Id));
    }
}
