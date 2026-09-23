using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardAutomationSessionLinkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"board-automation-session-{Guid.NewGuid():N}");
    private readonly BoardStore _boards;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardAutomationSessionLinkerTests()
    {
        Directory.CreateDirectory(_root);
        var connection = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        _boards = new BoardStore(connection, connection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("workflow-tab")]
    public async Task LaneAutomation_LinksReplayToItsOriginatingCard_Idempotently(string? tabId)
    {
        var project = Path.Combine(_root, "project");
        await _boards.EnsureDefaultColumnsAsync(project, Ct);
        var card = await _boards.CreateCardAsync(project, new(null, "Card", "", null, "medium", null, [], false), Ct);
        var run = Run(project, card.Key);

        await BoardAutomationSessionLinker.LinkAsync(_boards, run, "recorded-session", tabId, Ct);
        await BoardAutomationSessionLinker.LinkAsync(_boards, run, "recorded-session", tabId, Ct);

        var session = Assert.Single((await _boards.GetCardDetailAsync(project, card.Id, Ct))!.Sessions);
        Assert.Equal("recorded-session", session.SessionId);
        Assert.Equal(tabId, session.TabId);
        Assert.Equal("Automation: Review code", session.DisplayName);
        Assert.Equal("env:7:codex", session.Selection);
    }

    [Fact]
    public async Task ManualRetriesAndOtherProjects_DoNotInheritACardLink()
    {
        var project = Path.Combine(_root, "project");
        await _boards.EnsureDefaultColumnsAsync(project, Ct);
        var card = await _boards.CreateCardAsync(project, new(null, "Card", "", null, "medium", null, [], false), Ct);
        var run = Run(project, card.Key);
        await BoardAutomationSessionLinker.LinkAsync(_boards, run with { TriggerKind = JobTriggerKind.Manual }, "manual", cancellationToken: Ct);
        await BoardAutomationSessionLinker.LinkAsync(_boards, run with { ProjectPath = Path.Combine(_root, "elsewhere") }, "other", cancellationToken: Ct);
        Assert.Empty((await _boards.GetCardDetailAsync(project, card.Id, Ct))!.Sessions);
    }

    private static JobRunRecord Run(string project, string cardKey) => new(
        "run", 1, JobTriggerKind.BoardLane, $"board-lane:{cardKey}:lane:event", JobRunStatus.Running,
        "Review code", project, LLM.Codex, 7, "Review", null, null, DateTime.UtcNow,
        null, null, null, null, false, null);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
