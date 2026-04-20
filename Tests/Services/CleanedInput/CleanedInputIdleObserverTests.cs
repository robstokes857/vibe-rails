using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.CleanedInput;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.CleanedInput;

public class CleanedInputIdleObserverTests
{
    [Fact]
    public async Task OnTerminalIdleAsync_WhenWindowContainsRawInput_StillPersistsCleanedRow()
    {
        var input = new UserInputRecord(
            Id: 7,
            SessionId: "session-1",
            Sequence: 1,
            InputText: ">> What was worked on",
            GitCommitHash: null,
            TimestampUTC: DateTime.UtcNow);

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.GetUncleanedInputsForSessionAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<UserInputRecord> { input });
        repository.Setup(r => r.GetSessionLogChunksAsync("session-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>
            {
                new(
                    Id: 1,
                    TimestampUtc: DateTime.UtcNow,
                    Content: Encoding.UTF8.GetBytes(">> What was worked on"))
            });

        var cleanedService = new Mock<ICleanedUserInputService>();
        var observer = new CleanedInputIdleObserver(
            cleanedService.Object,
            repository.Object,
            NullLogger<CleanedInputIdleObserver>.Instance);

        await observer.OnTerminalIdleAsync(new TerminalIdleEvent(
            SessionId: "session-1",
            Cli: "codex",
            IdleFor: TimeSpan.FromSeconds(5),
            IdleThreshold: TimeSpan.FromSeconds(5),
            LastInputUtc: DateTimeOffset.UtcNow.AddSeconds(-6),
            LastOutputUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
            TimestampUtc: DateTimeOffset.UtcNow));

        cleanedService.Verify(
            s => s.CleanAndPersistAsync(
                7,
                It.Is<string>(text => text.Contains(">> What was worked on", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
