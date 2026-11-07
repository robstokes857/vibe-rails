using VibeRails;
using VibeRails.DB;
using Moq;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Environments;
using VibeRails.Services.Workspaces;
using Xunit;
namespace Tests.Services.Environments;

public class PromptPlaceholderServiceTests
{
    private static readonly string WorkDir = Path.GetTempPath();
    private static readonly PromptPlaceholderContext Context = new(WorkDir, EnvironmentName: "Agent", BoardCardKey: "VB-23");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("plain @file !command #card", "plain @file !command #card")]
    [InlineData("{{DATE}} {{time}} {{datetime}}", "2026-08-12 14:35 2026-08-12 14:35")]
    [InlineData("{{env_name}} {{board_card}}", "Agent VB-23")]
    [InlineData("{{question default=\"x\"}}", "{{question default=\"x\"}}")]
    public async Task OrdinaryFeaturesRemain(string? input, string? expected)
    {
        var service = new PromptPlaceholderService(new FixedClock());
        Assert.Equal(expected, await service.ResolveAsync(input, Context, Ct));
    }

    [Theory]
    [InlineData("{{step:123}}")]
    [InlineData("{{ STEP : missing}}")]
    [InlineData("{{worktree}} {{step:retired}}")]
    public async Task RetiredSyntaxFailsBeforeSideEffects(string input)
    {
        var worktrees = new Mock<IManagedWorktreeService>(MockBehavior.Strict);
        var service = new PromptPlaceholderService(worktrees: worktrees.Object);
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.ResolveAsync(input, Context, Ct));
        worktrees.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OversizePlainTextIsRejected()
    {
        await Assert.ThrowsAsync<PromptTooLongException>(() => new PromptPlaceholderService().ResolveAsync(new string('x', 30_001), Context, Ct));
    }

    [Fact]
    public async Task WorktreeResolvesOnceWithoutRescanningTheReturnedPath()
    {
        var worktrees = new Mock<IManagedWorktreeService>(MockBehavior.Strict);
        var path = Path.Combine(WorkDir, "literal-{{date}}");
        worktrees.Setup(w => w.CreateAsync(WorkDir, new WorktreeOptions(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ManagedWorktree("id", path, "common", "git", "branch", DateTime.UtcNow, null));
        var result = await new PromptPlaceholderService(worktrees: worktrees.Object)
            .ResolveLaunchAsync("{{worktree}} then {{worktree}}", Context, Ct);
        Assert.Equal(path + " then " + path, result.Prompt);
        Assert.Equal(path, result.WorkingDirectory);
        worktrees.Verify(w => w.CreateAsync(WorkDir, new WorktreeOptions(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConflictingWorktreesHaveNoSideEffects()
    {
        var worktrees = new Mock<IManagedWorktreeService>(MockBehavior.Strict);
        await Assert.ThrowsAsync<ArgumentException>(() => new PromptPlaceholderService(worktrees: worktrees.Object)
            .ResolveLaunchAsync("{{worktree}} {{worktree delete_after_days=never}}", Context, Ct));
        worktrees.VerifyNoOtherCalls();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 12, 14, 35, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
