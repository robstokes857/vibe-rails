using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Launches;

/// <summary>Regressions for the VIBE-292 review findings fixed in VIBE-296.</summary>
public sealed class LaunchReviewFollowUpTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launch-follow-up-" + Guid.NewGuid().ToString("N"));
    private readonly string repo;
    private readonly LaunchStore store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public LaunchReviewFollowUpTests()
    {
        // Mixed case on purpose: on Windows and macOS the comparison key upper-cases it.
        repo = Path.Combine(root, "Mixed-Case Repo");
        Directory.CreateDirectory(repo);
        store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
    }

    private LaunchService Service(IBoardStore? boards = null) => new(store, Mock.Of<IAutomationScriptService>(), boards);

    private async Task<LaunchPreset> PresetAsync(string name = "Agent", string purpose = "work") =>
        (await Service().SavePresetAsync(null, new(name, "", "codex", InitialPrompt: "Work", Purpose: purpose), Ct)).Preset;

    /// <summary>Item 3: a git hook must keep working when board.db is busy, newer or damaged, so it never opens it.</summary>
    [Fact]
    public async Task HookEnqueueNeverOpensBoardDb()
    {
        var state = Path.Combine(root, "state.db");
        var boardDb = Path.Combine(root, "board.db");
        var preset = await PresetAsync();
        await Service().SaveBindingAsync(repo, null, new(preset.Id, "preset", new(JobTriggerKind.Commit)), Ct);

        var events = LaunchEventService.Create(state);
        Assert.Equal(1, await events.EnqueueAsync(repo, JobTriggerKind.Commit, "aaaa", Ct));
        Assert.False(File.Exists(boardDb), "the hook path created board.db");

        // A damaged board.db (or one a newer build owns) cannot fail the hook either.
        var damaged = "not a sqlite database, and never opened"u8.ToArray();
        await File.WriteAllBytesAsync(boardDb, damaged, Ct);
        Assert.Equal(1, await LaunchEventService.Create(state).EnqueueAsync(repo, JobTriggerKind.Commit, "bbbb", Ct));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(boardDb, Ct));
        Assert.False(File.Exists(boardDb + "-wal"));

        // Without a Board store the snapshot leaves the nudge and App/Board context to the root's
        // session start, which TerminalSessionService honours through ContextIncluded.
        var run = (await store.ListRunsAsync(LaunchService.ProjectKey(repo), ct: Ct)).Runs[0];
        Assert.False(run.Snapshot.Context.ContextIncluded);
        Assert.Equal("Work", run.Snapshot.Context.ComposedPrompt);
    }

    /// <summary>Items 2 and 8: the key stays for comparisons and leases; everything else gets the cased root.</summary>
    [Fact]
    public async Task RunsBindingsAndLeasesKeepTheCasedProjectRootBesideTheKey()
    {
        var preset = await PresetAsync();
        var run = await Service().EnqueueAsync("preset", preset.Id, new(repo, repo, "board", "board:case"), Ct);
        Assert.Equal(LaunchService.ProjectKey(repo), run.ProjectPath);
        Assert.Equal(LaunchService.ProjectKey(repo), run.Snapshot.Context.ProjectPath);
        Assert.Equal(LaunchService.ProjectRoot(repo), run.Snapshot.Context.Root, StringComparer.Ordinal);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.NotEqual(run.ProjectPath, run.Snapshot.Context.Root, StringComparer.Ordinal);

        var binding = await Service().SaveBindingAsync(repo, null, new(preset.Id, "preset", new(JobTriggerKind.Commit)), Ct);
        Assert.Equal(LaunchService.ProjectKey(repo), binding.ProjectPath);
        Assert.Equal(LaunchService.ProjectRoot(repo), Assert.Single(await store.ListBindingsAsync(binding.ProjectPath, ct: Ct)).ProjectRoot,
            StringComparer.Ordinal);

        // A run queued before VIBE-296 has no root; it falls back to the key.
        Assert.Equal("KEY", new LaunchExecutionContext("KEY", "dir", "manual", "x").Root);

        var now = DateTime.UtcNow;
        Assert.True(await store.TryLeaseProjectAsync(LaunchService.ProjectKey(repo), LaunchService.ProjectRoot(repo), "me", now, TimeSpan.FromSeconds(30), Ct));
        Assert.True(await store.TryLeaseProjectAsync(LaunchService.ProjectKey(repo), LaunchService.ProjectRoot(repo), "me", now, TimeSpan.FromSeconds(30), Ct));
        Assert.False(await store.TryLeaseProjectAsync(LaunchService.ProjectKey(repo), LaunchService.ProjectRoot(repo), "another root", now, TimeSpan.FromSeconds(30), Ct));
        // An older build's lease has no recorded root and still answers with its key.
        Assert.True(await store.TryLeaseAsync("project:C:\\OLDER\\BUILD", "old", now, TimeSpan.FromSeconds(30), Ct));
        var open = await store.GetOpenProjectRootsAsync(now, Ct);
        Assert.Contains(LaunchService.ProjectRoot(repo), open);
        Assert.Contains("C:\\OLDER\\BUILD", open);
        Assert.Equal(2, open.Count);
        Assert.Empty(await store.GetOpenProjectRootsAsync(now.AddMinutes(1), Ct));
    }

    /// <summary>
    /// Review R1: an enabled script binding validates against the cased root, as saving the Action did.
    /// The validator here is ordinal, like a case-sensitive macOS volume; the upper-cased key fails it.
    /// </summary>
    [Fact]
    public async Task EnabledScriptBindingsValidateAgainstTheCasedRootNotTheKey()
    {
        var cased = LaunchService.ProjectRoot(repo);
        var seen = new List<string>();
        var scripts = new Mock<IAutomationScriptService>();
        scripts.Setup(x => x.NormalizeAsync(It.IsAny<string>(), It.IsAny<JobActionRequest>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((string project, JobActionRequest action, CancellationToken _, bool _) =>
            {
                seen.Add(project);
                if (!string.Equals(LaunchService.ProjectRoot(project), cased, StringComparison.Ordinal))
                    throw new AutomationScriptValidationException("No such directory on a case-sensitive volume: " + project);
                return action with { ApprovedHash = new string('a', 64) };
            });
        var service = new LaunchService(store, scripts.Object);
        var action = await service.SaveActionAsync(repo, null, new("Lint", "", "script", new("lint.py", JobScriptRuntime.Python, [])), Ct);

        var binding = await service.SaveBindingAsync(repo, null, new(action.Id, "action", new(JobTriggerKind.Commit)), Ct);

        Assert.True(binding.Enabled);
        Assert.Equal(cased, binding.ProjectRoot, StringComparer.Ordinal);
        Assert.All(seen, project => Assert.Equal(cased, LaunchService.ProjectRoot(project), StringComparer.Ordinal));
        Assert.True(seen.Count >= 2, "the Action save and the binding validation both reached the validator");
        // The hook then queues the same Action against the same root.
        Assert.Equal(1, await new LaunchEventService(store, service).EnqueueAsync(repo, JobTriggerKind.Commit, "cccc", Ct));
        Assert.Equal(cased, Assert.Single((await store.ListRunsAsync(LaunchService.ProjectKey(repo), ct: Ct)).Runs).Snapshot.Context.Root,
            StringComparer.Ordinal);
    }

    /// <summary>Item 4: the Board title reaches the tab and the card's single session link.</summary>
    [Theory]
    [InlineData(false, BoardSessionRecord.LaunchOrigin)]
    [InlineData(true, BoardSessionRecord.LaunchOrigin)]
    // Review R2: lane launches refine an early MCP attachment too, so they still count as Automations.
    [InlineData(true, BoardSessionRecord.AutomationOrigin)]
    [InlineData(true, "lane_review")]
    public async Task PresetBoardLaunchUsesTheCardTitleAndLinksTheSessionOnce(bool earlyAttachment, string origin)
    {
        var board = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False",
            $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await board.EnsureDefaultColumnsAsync(repo, Ct);
        var card = await board.CreateCardAsync(repo, new(null, "Card", "", null, "medium", null, [], false), Ct);
        var preset = await PresetAsync("Preset name");
        var title = $"{card.DisplayId} · Card";
        var run = await Service(board).EnqueueAsync("preset", preset.Id, new(repo, repo, "board", "board:title",
            BoardId: card.BoardId, CardId: card.Id, ContextIncluded: true, ComposedPrompt: "Work", SessionOrigin: origin,
            SessionTitle: title, NativeTerminal: true), Ct);
        Assert.False(run.Snapshot.Context.NativeTerminal);
        var session = Guid.NewGuid().ToString();
        StartTerminalRequest? started = null;
        var tabs = new Mock<ITerminalTabHostService>();
        tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TerminalTabStatusResponse("tab", DateTime.UtcNow, false));
        tabs.Setup(t => t.StartSessionAsync("tab", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, StartTerminalRequest request, CancellationToken token) =>
            {
                started = request;
                await store.StartAsync(request.LaunchClaim!, session, "Work", repo, Environment.ProcessId, DateTime.UtcNow, ct: token);
                // The CLI's MCP connection can attach its own session before the launch links it.
                if (earlyAttachment)
                    await board.LinkSessionAsync(repo, card.Id, session, null, "base:codex", "codex", "Early attachment", BoardSessionRecord.McpOrigin, token);
                return new TerminalStatusResponse(true, session, "codex", repo);
            });
        using var services = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new LaunchDispatcher(store, Service(board), tabs.Object, services.GetRequiredService<IServiceScopeFactory>(), board);

        await dispatcher.DispatchAsync(run.Id, Ct);

        Assert.Equal(title, started!.Title);
        var linked = Assert.Single((await board.GetCardDetailAsync(repo, card.Id, Ct))!.Sessions);
        Assert.Equal(origin, linked.Origin);
        Assert.Equal(earlyAttachment ? "Early attachment" : title, linked.DisplayName);
        Assert.Equal("running", (await store.GetRunAsync(run.Id, Ct))!.Status);
    }

    /// <summary>Item 5: an Agent's non-review purpose reaches its comments through the run snapshot.</summary>
    [Theory]
    [InlineData("testing", "testing")]
    [InlineData("documentation", "documentation")]
    [InlineData("work", "work")]
    public async Task AgentCommentsCarryThePurposeSnapshottedOnTheirLaunchRun(string purpose, string expected)
    {
        var board = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False",
            $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await board.EnsureDefaultColumnsAsync(repo, Ct);
        var card = await board.CreateCardAsync(repo, new(null, "Card", "", null, "medium", null, [], false), Ct);
        var preset = await PresetAsync("Tester", purpose);
        var run = await Service().EnqueueAsync("preset", preset.Id, new(repo, repo, "board", "board:purpose",
            CardId: card.Id, SessionOrigin: BoardSessionRecord.LaunchOrigin), Ct);
        var session = Guid.NewGuid().ToString();
        var claim = (await store.TryClaimAsync(run.Id, 3, Environment.ProcessId, DateTime.UtcNow, Ct))!;
        Assert.True(await store.SetTabAsync(claim, "tab", Ct));
        Assert.True(await store.StartAsync(claim, session, "Work", repo, Environment.ProcessId, DateTime.UtcNow, ct: Ct));
        await board.LinkSessionAsync(repo, card.Id, session, "tab", $"preset:{preset.Id}:codex", "codex", "Card", BoardSessionRecord.LaunchOrigin, Ct);
        var service = new BoardService(board, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), launchRuns: store);

        await service.AddCommentAsync(repo, card.Id, BoardAuthor.Agent("Claude", "claude", session), "Finished.", Ct);
        // Another session on the same card that no launch run started keeps no purpose.
        var unlaunched = Guid.NewGuid().ToString();
        await board.LinkSessionAsync(repo, card.Id, unlaunched, null, "base:codex", "codex", "Manual", BoardSessionRecord.LaunchOrigin, Ct);
        await service.AddCommentAsync(repo, card.Id, BoardAuthor.Agent("Codex", "codex", unlaunched), "Other.", Ct);

        var comments = (await board.GetCardDetailAsync(repo, card.Id, Ct))!.Comments;
        Assert.Equal(expected, BoardCommentPurpose.Read(comments.Single(c => c.Body == "Finished.").Changes));
        Assert.Null(BoardCommentPurpose.Read(comments.Single(c => c.Body == "Other.").Changes));
    }

    /// <summary>Item 6: the 2 s tick writes board.db only for a status change, and skips settled entries.</summary>
    [Fact]
    public async Task TickWritesOnlyChangedStatusesAndSkipsSettledEntries()
    {
        var runs = new Mock<ILaunchStore>();
        LaunchRun Run(string id, string status) => new(id, 1, "preset_a", "preset", "P", "o-" + id,
            new(new("P", "P", "board-lane", "o-" + id)), status, DateTime.UtcNow);
        runs.Setup(r => r.GetRunAsync("run_same", It.IsAny<CancellationToken>())).ReturnsAsync(Run("run_same", "running"));
        runs.Setup(r => r.GetRunAsync("run_moved", It.IsAny<CancellationToken>())).ReturnsAsync(Run("run_moved", "succeeded"));
        BoardLaunchEvent Entry(string id, string? run, string status, string type = "preset", string? verdict = null) =>
            new(id, "P", "card", "lane", "board", "preset_a", type, "process", 0, DateTime.UtcNow.AddMinutes(-5), true,
                run, status, verdict, false, null);
        var entries = new[]
        {
            Entry("same", "run_same", "running"),
            Entry("moved", "run_moved", "running"),
            Entry("settled-preset", "run_settled", "succeeded"),
            Entry("settled-action", "run_reported", "failed", "action", "failed"),
        };
        var board = new Mock<IBoardStore>();
        board.Setup(b => b.GetLaunchEventsAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(entries);
        board.Setup(b => b.UpdateLaunchEventAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var workflow = new BoardLaunchWorkflow(board.Object, runs.Object, new LaunchService(runs.Object, Mock.Of<IAutomationScriptService>()), null!);

        await workflow.TickAsync(Ct);
        await workflow.TickAsync(Ct);

        board.Verify(b => b.UpdateLaunchEventAsync("moved", "run_moved", "succeeded", It.IsAny<CancellationToken>()), Times.Exactly(2));
        board.Verify(b => b.UpdateLaunchEventAsync("same", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        runs.Verify(r => r.GetRunAsync("run_settled", It.IsAny<CancellationToken>()), Times.Never);
        runs.Verify(r => r.GetRunAsync("run_reported", It.IsAny<CancellationToken>()), Times.Never);
        // A terminal Action whose verdict is not yet reported keeps ticking until it is.
        Assert.False(BoardLaunchWorkflow.IsSettled(Entry("unreported", "run_x", "succeeded", "action")));
        Assert.False(BoardLaunchWorkflow.IsSettled(Entry("uncertain", "run_x", "uncertain")));
    }

    /// <summary>Item 6, store side: an unchanged status is not a write.</summary>
    [Fact]
    public async Task UnchangedLaunchEventStatusIsNotWritten()
    {
        var board = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False",
            $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await board.EnsureDefaultColumnsAsync(repo, Ct);
        var lane = (await board.GetColumnsAsync(repo, Ct))[0];
        await board.SaveLaunchLaneAsync(repo, lane.Id, new([new("preset_a", "preset")], 0), Ct);
        var card = await board.CreateCardAsync(repo, new(lane.Id, "Card", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await board.GetLaunchEventsAsync(repo, card.Id, Ct));
        Assert.True(await board.AcknowledgeLaunchEventAsync(entry.Id, "run_1", Ct));
        Assert.False(await board.UpdateLaunchEventAsync(entry.Id, "run_1", "queued", Ct));
        Assert.True(await board.UpdateLaunchEventAsync(entry.Id, "run_1", "running", Ct));
        Assert.False(await board.UpdateLaunchEventAsync(entry.Id, "run_1", "running", Ct));
        Assert.Equal("running", Assert.Single(await board.GetLaunchEventsAsync(repo, card.Id, Ct)).ProcessStatus);
    }

    /// <summary>Item 7: a retry is admitted against the definition it will execute, the source run's.</summary>
    [Fact]
    public async Task RetryValidatesTheSourceRunsDefinitionNotTheCallersSnapshot()
    {
        var retried = await PresetAsync("Retried");
        var other = await PresetAsync("Other");
        var run = await Service().EnqueueAsync("preset", retried.Id, new(repo, repo, "manual", "first"), Ct);
        var claim = (await store.TryClaimAsync(run.Id, 3, Environment.ProcessId, DateTime.UtcNow, Ct))!;
        Assert.True(await store.CompleteAsync(claim, "failed", ct: Ct));
        await Service().RetireAsync("preset", retried.Id, retried.Revision, Ct);
        var otherRun = await Service().EnqueueAsync("preset", other.Id, new(repo, repo, "manual", "other"), Ct);

        // The caller's snapshot names an enabled definition; the retry would execute the retired one.
        var disguised = otherRun.Snapshot with { Context = otherRun.Snapshot.Context with { OccurrenceId = "retry:disguised" } };
        await Assert.ThrowsAsync<LaunchValidationException>(() => store.EnqueueAsync(disguised, run.Id, Ct));
        Assert.Null(await store.FindOccurrenceAsync(run.ProjectPath, "retry:disguised", Ct));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
