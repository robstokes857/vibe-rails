using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.BertBaseClasses;
using Xunit;

namespace Tests.Services;

public sealed class UserInputRecordingServiceTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 8)]
    public async Task RecordAsync_PersistsGitStateBeforeOpeningCaptureWindow(bool hasPrevious, int expectedSequence)
    {
        var ct = TestContext.Current.CancellationToken;
        var input = new Mock<IUserInputStore>(MockBehavior.Strict);
        var git = new Mock<IGitService>(MockBehavior.Strict);
        var capture = new Mock<IGitDiffCaptureService>(MockBehavior.Strict);
        var order = new MockSequence();
        git.InSequence(order).Setup(x => x.GetCurrentCommitHashAsync(ct)).ReturnsAsync("commit-123");
        input.InSequence(order).Setup(x => x.GetLastUserInputAsync("session"))
            .ReturnsAsync(hasPrevious ? new UserInputRecord(90, "session", 7, "previous", null, DateTime.UtcNow) : null);
        input.InSequence(order).Setup(x => x.InsertUserInputAsync("session", expectedSequence, "fix the bug", "commit-123"))
            .ReturnsAsync(91);
        input.InSequence(order).Setup(x => x.GetSessionWorkingDirectoryAsync("session", ct)).ReturnsAsync("project");
        capture.InSequence(order).Setup(x => x.BeginCaptureWindowAsync("session", 91, "commit-123", "project", ct))
            .Returns(Task.CompletedTask);

        await new UserInputRecordingService(input.Object, capture.Object)
            .RecordAsync("session", "fix the bug", git.Object, ct);

        input.VerifyAll();
        git.VerifyAll();
        capture.VerifyAll();
    }

    [Fact]
    public async Task RecordAsync_CaptureFailurePreservesStoredInputAndDoesNotInterruptTerminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = new Mock<IUserInputStore>(MockBehavior.Strict);
        var git = new Mock<IGitService>(MockBehavior.Strict);
        var capture = new Mock<IGitDiffCaptureService>(MockBehavior.Strict);
        git.Setup(x => x.GetCurrentCommitHashAsync(ct)).ReturnsAsync((string?)null);
        input.Setup(x => x.GetLastUserInputAsync("session")).ReturnsAsync((UserInputRecord?)null);
        input.Setup(x => x.InsertUserInputAsync("session", 1, "hello", null)).ReturnsAsync(13);
        input.Setup(x => x.GetSessionWorkingDirectoryAsync("session", ct)).ReturnsAsync("project");
        capture.Setup(x => x.BeginCaptureWindowAsync("session", 13, null, "project", ct))
            .ThrowsAsync(new IOException("Git capture unavailable"));

        await new UserInputRecordingService(input.Object, capture.Object)
            .RecordAsync("session", "hello", git.Object, ct);

        input.Verify(x => x.InsertUserInputAsync("session", 1, "hello", null), Times.Once);
        capture.VerifyAll();
    }
}
