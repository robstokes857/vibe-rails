using Moq;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class SwitchReviewerTests
{
    [Theory]
    [InlineData("agent", "work", "task", null, "agent_manager", false)]
    [InlineData("agent", "manager", "research-spike", null, "agent_manager", false)]
    [InlineData("agent", "worker", "research-spike", null, "agent_worker_planning", false)]
    [InlineData("agent", "worker", "task", "--permission-mode plan", "agent_worker_planning", false)]
    [InlineData("agent", "worker", "task", "--permission-mode=plan", "agent_worker_planning", false)]
    [InlineData("agent", "worker", "task", "--mode plan", "agent_worker_planning", false)]
    [InlineData("agent", "worker", "task", "--agent=plan", "agent_worker_planning", false)]
    [InlineData("agent", "worker", "task", null, "agent_worker", true)]
    [InlineData("agent", "code_review", "research-spike", null, "agent_worker", false)]
    [InlineData("manual", "work", "research-spike", null, "planning", false)]
    [InlineData("manual", "work", "task", "--permission-mode plan", "planning", false)]
    [InlineData("manual", "work", "task", null, "launch", true)]
    [InlineData("manual", "chat", "research-spike", null, "chat", false)]
    [InlineData("manual", "code_review", "research-spike", null, "code_review", false)]
    public async Task LaunchedRoleAndPurposeControlCodingEligibility_WithoutContextSamples(
        string workflow, string intent, string type, string? args, string origin, bool eligible)
    {
        await VerifyLaunch(workflow, intent, type, args, origin, eligible, earlyAttachment: false);
        await VerifyLaunch(workflow, intent, type, args, origin, eligible, earlyAttachment: true);
    }

    private async Task VerifyLaunch(string workflow, string intent, string type, string? args, string origin,
        bool eligible, bool earlyAttachment)
    {
        var selection = "base:claude";
        if (args is not null)
        {
            var environment = new LLM_Environment { CustomName = earlyAttachment ? "Early planning" : "Planning", LLM = LLM.Claude, CustomArgs = args, ProjectPath = project };
            await repository.SaveEnvironmentAsync(environment, Ct);
            selection = $"env:{environment.Id}:claude";
        }
        card = await boards.CreateCardAsync(project, new(null, "Role and purpose", "", selection, "low", null, [], false,
            Type: type, WorkflowMode: workflow, ManagerSelection: workflow == "agent" ? "base:claude" : null), Ct);
        var sessionId = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(sessionId, "claude", null, project, 1);
        BoardSessionRecord? earlyLink = null;
        var tabs = new Mock<ITerminalTabHostService>();
        tabs.SetupGet(t => t.MaxTabs).Returns(8);
        tabs.Setup(t => t.ListTabsAsync(Ct)).ReturnsAsync([]);
        tabs.Setup(t => t.CreateTabAsync(Ct)).ReturnsAsync(new TerminalTabStatusResponse("tab", DateTime.UtcNow, false));
        tabs.Setup(t => t.StartSessionAsync("tab", It.IsAny<StartTerminalRequest>(), Ct))
            .Returns(async () =>
            {
                if (earlyAttachment)
                    earlyLink = await boards.LinkSessionAsync(project, card.Id, sessionId, null, selection, "claude", "Early attachment", "mcp", Ct);
                return new TerminalStatusResponse(true, sessionId, "claude", project);
            });
        // Context measurement is best effort. Eligibility must survive its failure.
        var estimator = new Mock<IBoardContextEstimator>(MockBehavior.Strict);
        var launcher = new BoardLaunchService(boards, repository, tabs.Object, estimator.Object);
        await launcher.LaunchAsync(project, card.Id, null, Ct, intent);

        var linked = Assert.Single((await boards.GetCardDetailAsync(project, card.Id, Ct))!.Sessions);
        Assert.Equal(origin, linked.Origin);
        if (earlyLink is not null)
        {
            Assert.Equal(earlyLink.CreatedUtc, linked.CreatedUtc);
            Assert.Equal(earlyLink.DisplayName, linked.DisplayName);
        }
        if (intent != "code_review")
            Assert.Equal(!eligible, await boards.IsNonCodingSessionAsync(project, sessionId, Ct));
        if (origin == "agent_worker_planning")
        {
            tabs.Setup(t => t.ListTabsAsync(Ct)).ReturnsAsync([new("tab", DateTime.UtcNow, true, sessionId)]);
            await Assert.ThrowsAsync<BoardConflictException>(() => launcher.LaunchAsync(project, card.Id, null, Ct, "worker"));
        }

        var other = await boards.CreateCardAsync(project, new(null, "Additional card", "", null, "low", null, [], false), Ct);
        var service = new BoardService(boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        Assert.NotNull(await service.AttachSessionAsync(project, other.Id, sessionId, null, Ct));
        foreach (var target in new[] { card.Id, other.Id })
        {
            var dto = Assert.Single((await service.GetSessionsAsync(project, target, Ct))!);
            Assert.Equal(!eligible, dto.IsNonCoding);
            var settings = new BoardReviewSettings("session", sessionId, "Claimed coding contribution");
            if (eligible)
            {
                Assert.NotNull(await routing.SaveSettingsAsync(project, target, settings, Ct));
                var resolved = await routing.ResolveAsync(project, target, ReviewerRouting.SwitchDefault(), null, Ct);
                Assert.Null(resolved.Resolution.Problem);
                Assert.Equal("claude", resolved.Resolution.Source.Provider);
                Assert.Equal("codex", resolved.Resolution.Provider);
            }
            else
            {
                await Assert.ThrowsAsync<BoardValidationException>(() => routing.SaveSettingsAsync(project, target, settings, Ct));
                // An older version could have saved an invalid source. Resolution must reject it too.
                await boards.SaveReviewSettingsAsync(project, target, settings, Ct);
                var resolved = await routing.ResolveAsync(project, target, ReviewerRouting.SwitchDefault(), null, Ct);
                Assert.Contains("cannot be selected as coding evidence", resolved.Resolution.Problem);
                Assert.Null(resolved.Resolution.Source.Provider);
                await Assert.ThrowsAsync<BoardValidationException>(() => routing.RequireLaunchAsync(resolved, Ct));
            }
        }
        Assert.Equal(origin, Assert.Single((await boards.GetCardDetailAsync(project, card.Id, Ct))!.Sessions).Origin);
    }

    [Fact]
    public async Task NonCodingBatchLookupIsScopedAndHandlesMultipleBatches()
    {
        var session = await Session("claude", "agent_manager");
        var ids = Enumerable.Range(0, 101).Select(i => "missing-" + i).Concat([session, session]).ToList();
        Assert.Equal(session, Assert.Single(await boards.GetNonCodingSessionIdsAsync(project, ids, Ct)));
        Assert.Empty(await boards.GetNonCodingSessionIdsAsync(root, ids, Ct));
        Assert.Empty(await boards.GetNonCodingSessionIdsAsync(project, [], Ct));
    }

    [Fact]
    public async Task LaunchOriginConfirmationOnlyRefinesTheExactAttachment_AndDoesNotChangeDuplicateLinkBehavior()
    {
        var session = await Session("claude", "mcp");
        var other = await boards.CreateCardAsync(project, new(null, "Additional", "", null, "low", null, [], false), Ct);
        await boards.LinkSessionAsync(project, other.Id, session, null, "base:claude", "claude", "Other", "mcp", Ct);
        Assert.False(await boards.ConfirmSessionLaunchOriginAsync(root, card.Id, session, "agent_manager", Ct));
        Assert.False(await boards.ConfirmSessionLaunchOriginAsync(project, card.Id, "missing", "agent_manager", Ct));
        Assert.True(await boards.ConfirmSessionLaunchOriginAsync(project, other.Id, session, "agent_worker_planning", Ct));
        Assert.Equal("mcp", Assert.Single((await boards.GetCardDetailAsync(project, card.Id, Ct))!.Sessions).Origin);
        Assert.False(await boards.ConfirmSessionLaunchOriginAsync(project, other.Id, session, "agent_worker", Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() =>
            boards.LinkSessionAsync(project, other.Id, session, null, "base:claude", "claude", "Duplicate", "mcp", Ct));
        Assert.Equal("agent_worker_planning", Assert.Single((await boards.GetCardDetailAsync(project, other.Id, Ct))!.Sessions).Origin);
    }
}
