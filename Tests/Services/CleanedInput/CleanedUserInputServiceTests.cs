using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.CleanedInput;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.CleanedInput;

public class CleanedUserInputServiceTests
{
    private static CleanedUserInputService CreateService(Mock<IRepository> repo)
    {
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());

        return new(repo.Object, new TuiTextExtractor(), NullLogger<CleanedUserInputService>.Instance);
    }

    private static UserInputRecord MakeRawInput(long id, string text, string sessionId = "session-1", int seq = 1) =>
        new(
            Id: id,
            SessionId: sessionId,
            Sequence: seq,
            InputText: text,
            GitCommitHash: null,
            TimestampUTC: DateTime.UtcNow);

    [Fact]
    public async Task CleanAndPersistAsync_SkipsAlreadyCleaned()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.IsInputCleanedAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var svc = CreateService(repo);
        await svc.CleanAndPersistAsync(7);

        repo.Verify(
            r => r.CreateCleanedAndLinkAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CleanAndPersistAsync_PersistsEmpty_WhenSecretFiltered()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.IsInputCleanedAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repo.Setup(r => r.GetUserInputByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRawInput(7, "export ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstu"));

        var svc = CreateService(repo);
        await svc.CleanAndPersistAsync(7);

        // Secret → filtered out → persisted with empty text
        repo.Verify(
            r => r.CreateCleanedAndLinkAsync(
                "session-1", 7, "",
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanAndPersistAsync_PersistsEmpty_WhenNoiseFiltered()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.IsInputCleanedAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repo.Setup(r => r.GetUserInputByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRawInput(7, "ls"));

        var svc = CreateService(repo);
        await svc.CleanAndPersistAsync(7);

        repo.Verify(
            r => r.CreateCleanedAndLinkAsync(
                "session-1", 7, "",
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanAndPersistAsync_PersistsCleanedText_WhenValid()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.IsInputCleanedAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repo.Setup(r => r.GetUserInputByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeRawInput(7, "> add search functionality"));

        var svc = CreateService(repo);
        await svc.CleanAndPersistAsync(7);

        repo.Verify(
            r => r.CreateCleanedAndLinkAsync(
                "session-1", 7, "add search functionality",
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CleanAndPersistAsync_SkipsMissingInput()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.IsInputCleanedAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repo.Setup(r => r.GetUserInputByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserInputRecord?)null);

        var svc = CreateService(repo);
        await svc.CleanAndPersistAsync(7);

        repo.Verify(
            r => r.CreateCleanedAndLinkAsync(
                It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void CleanText_StripsPromptPrefix()
    {
        var svc = CreateService(new Mock<IRepository>());
        var result = svc.CleanText("> $ fix the bug");
        Assert.Equal("fix the bug", result);
    }

    [Fact]
    public void CleanText_UsesTuiExtraction_WhenRicherSessionOutputIsAvailable()
    {
        var svc = CreateService(new Mock<IRepository>());

        var result = svc.CleanText(
            "add search functionality",
            "> @VibeRails/wwwroot/js/modules/chat-history-sidebar.js add search functionality with GUID support");

        Assert.Equal("@VibeRails/wwwroot/js/modules/chat-history-sidebar.js add search functionality with GUID support", result);
    }

    [Fact]
    public void CleanText_ReturnsNullForSecret()
    {
        var svc = CreateService(new Mock<IRepository>());
        var result = svc.CleanText(
            "[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','sk-ant-api03-abcdefghijklmnopqrstu','User')");
        Assert.Null(result);
    }
}
