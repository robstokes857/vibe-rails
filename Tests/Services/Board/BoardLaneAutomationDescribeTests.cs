using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The wording agents read for a lane Automation (VB-34) comes from the definition, not from a
/// per-kind template: a review Worker, a PR-opening Worker and a script-only workflow all read
/// the same way, and every reason the scheduler would consume an entry without a run is named.
/// </summary>
public sealed class BoardLaneAutomationDescribeTests
{
    private static BoardLaneAutomationDefinition Definition(
        string? name = "Automated code review", bool enabled = true, bool deleted = false, bool inProject = true, bool hasActions = true,
        string? worker = "Reviewer", string cli = "Claude", string prompt = "Review the linked commits.\nPost findings as a comment.",
        IReadOnlyList<string>? scripts = null, string? activeRun = null, bool running = false) =>
        new(7, name, enabled, deleted, inProject, hasActions, worker, cli, prompt, scripts ?? ["scripts/check.py"], activeRun, running);

    [Fact]
    public void WorkerWithScripts_ReadsFromTheDefinition()
    {
        var info = BoardService.Describe(Definition());
        Assert.Equal("Automated code review", info.Name);
        Assert.Equal("Worker \"Reviewer\" (Claude): \"Review the linked commits.\" + 1 script (check.py)", info.Summary);
        Assert.Equal("a Worker terminal run linked to the card's Sessions rail", info.Output);
        Assert.Null(info.Unavailable);
        Assert.True(info.WouldQueue);
    }

    [Fact]
    public void ScriptOnlyWorkflow_ReadsTheSameWay_AndListsAtMostThreeScripts()
    {
        var info = BoardService.Describe(Definition(worker: null, prompt: "", scripts: ["a.py", "b.ps1", "c.sh", "d.py"]));
        Assert.Equal("4 scripts (a.py, b.ps1, c.sh, …)", info.Summary);
        Assert.Equal("script output recorded on the card's Sessions rail", info.Output);
        Assert.Equal("nothing", BoardService.Describe(Definition(worker: null, scripts: [], hasActions: false)).Output);
        Assert.Equal("no actions", BoardService.Describe(Definition(worker: null, scripts: [], hasActions: false)).Summary);
    }

    [Fact]
    public void EveryGateTheSchedulerApplies_IsNamed_InPrecedenceOrder()
    {
        Assert.Equal("no longer exists", BoardService.Describe(Definition(name: null, enabled: false)).Unavailable);
        Assert.Equal("Automation #7", BoardService.Describe(Definition(name: null)).Name);
        Assert.Equal("definition unavailable", BoardService.Describe(Definition(name: null, worker: null, scripts: [])).Summary);
        Assert.Equal("was deleted", BoardService.Describe(Definition(deleted: true, enabled: false)).Unavailable);
        Assert.Equal("is disabled", BoardService.Describe(Definition(enabled: false, inProject: false)).Unavailable);
        Assert.Equal("belongs to another repository", BoardService.Describe(Definition(inProject: false, hasActions: false)).Unavailable);
        Assert.Equal("has no actions", BoardService.Describe(Definition(hasActions: false)).Unavailable);

        var overlapping = BoardService.Describe(Definition(activeRun: "run-1", running: true));
        Assert.Null(overlapping.Unavailable);
        Assert.Equal("run-1", overlapping.ActiveRunId);
        Assert.True(overlapping.ActiveRunIsRunning);
        Assert.False(overlapping.WouldQueue);
    }

    [Fact]
    public void Names_AndPrompts_AreOneBoundedLine()
    {
        Assert.Equal("Open PR", BoardService.OneLine("  Open \t\u0007PR \r\nsecond line", 80));
        Assert.Equal("", BoardService.OneLine(null, 10));
        var truncated = BoardService.OneLine(new string('x', 200), 20);
        Assert.Equal(20, truncated.Length);
        Assert.EndsWith("…", truncated);
        Assert.Equal("\"A\", \"B\"", BoardService.QuotedNames([
            BoardService.Describe(Definition(name: "A")), BoardService.Describe(Definition(name: "B"))]));
        Assert.Equal("Moved to Review; lane Automations skipped at the caller's request: \"A\".",
            BoardService.SkipComment("Review", [BoardService.Describe(Definition(name: "A"))]));
    }

    [Fact]
    public async Task AStateDatabaseWithoutAutomations_DescribesSelectionsAsUnavailable_InsteadOfFailing()
    {
        // A stdio MCP host can open a state.db that has never held Automations.
        var root = Path.Combine(Path.GetTempPath(), $"board-lane-describe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new BoardStore(
                $"Data Source={Path.Combine(root, "board.db")};Pooling=False",
                $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
            var described = await store.DescribeLaneAutomationsAsync(root, [42, 43], TestContext.Current.CancellationToken);
            Assert.Equal([42L, 43L], described.Select(d => d.JobId));
            Assert.All(described, d => Assert.Null(d.Name));
            Assert.Equal("Automation #42 — definition unavailable; output: nothing",
                $"{BoardService.Describe(described[0]).Name} — {BoardService.Describe(described[0]).Summary}; output: {BoardService.Describe(described[0]).Output}");
            Assert.Empty(await store.DescribeLaneAutomationsAsync(root, [], TestContext.Current.CancellationToken));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
