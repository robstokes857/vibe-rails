using System.Diagnostics;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// await_board_events (VIBE-52 follow-up, VIBE-204): the AI board manager switch, the snapshot and
/// its cursor, events after it in the manager's scope without the caller's own writes, a quiet wait
/// that keeps the cursor and does not repeat the snapshot, and a wake on a snapshot-only change.
/// </summary>
public sealed partial class BoardToolTests
{
    /// <summary>How the tool prints a card: its key, plus the display label when that differs.</summary>
    private static string Label(BoardCardRecord card) => card.DisplayId == card.Key ? card.Key : $"{card.Key} ({card.DisplayId})";

    private static string NextSince(string reply) =>
        reply.Split('\n').Single(line => line.StartsWith("nextSince=", StringComparison.Ordinal))["nextSince=".Length..].Trim();

    private async Task<string> ManagedBoardAsync()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        await _store.SetBoardManagerEnabledAsync(true, Ct);
        var boardId = (await _store.GetBoardsAsync(_project, Ct)).Single().Id;
        await _store.SaveBoardManagerAsync(_project, boardId, new("base:codex", null), 0, Ct);
        return boardId;
    }

    [Fact]
    public async Task AwaitBoardEvents_IsOff_UntilTheAiBoardManagerIsOn()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        Assert.Equal(BoardTool.BoardManagerOffReply, await _tool.AwaitBoardEvents(cancellationToken: Ct));
        await _store.SetBoardManagerEnabledAsync(true, Ct);
        Assert.Contains("No cursor supplied: snapshot only.", await _tool.AwaitBoardEvents(cancellationToken: Ct));
    }

    [Fact]
    public async Task AwaitBoardEvents_ReturnsSnapshotWithoutCursor_ThenOnlyChangesAfterIt_WithoutTheCallersOwnWrites()
    {
        var boardId = await ManagedBoardAsync();
        var managed = await _store.CreateCardAsync(_project, new(null, "Board managed", "", "base:claude", "high", null, [], false, WorkflowMode: "agent"), Ct);
        var own = await _store.CreateCardAsync(_project, new(null, "Own manager", "", null, "medium", null, [], false, WorkflowMode: "agent", ManagerSelection: "base:codex"), Ct);
        var manual = await _store.CreateCardAsync(_project, new(null, "Manual card", "", null, "medium", null, [], false), Ct);
        _resolver.CurrentSessionId = "44444444-4444-4444-8444-444444444444";

        var snapshot = await _tool.AwaitBoardEvents(cancellationToken: Ct);
        Assert.Contains("No cursor supplied: snapshot only.", snapshot);
        Assert.Contains("Agent cards (1 managed by the board manager, 1 with their own manager; 0 implementation workers open of 1 allowed):", snapshot);
        Assert.Contains($"- {Label(managed)} · lane Backlog · priority high · board manager · worker base:claude", snapshot);
        Assert.Contains($"- {Label(own)} · lane Backlog · priority medium · own manager base:codex · worker (none)", snapshot);
        Assert.DoesNotContain("Manual card", snapshot);
        var cursor = NextSince(snapshot);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT[\d:.]+Z~[0-9a-f]{12}$", cursor);
        Assert.Contains($"await_board_events(board=\"{boardId}\", since=\"{cursor}\")", snapshot);

        await Task.Delay(5, Ct);
        await _store.AddCommentAsync(_project, managed.Id, BoardAuthor.Agent("Board manager", "codex", _resolver.CurrentSessionId), "My own checkpoint", Ct);
        await _store.AddCommentAsync(_project, manual.Id, BoardAuthor.User(), "Noise on a manual card", Ct);
        await _store.AddCommentAsync(_project, managed.Id, BoardAuthor.Agent("Worker", "claude", null), "Tests pass; handoff saved.", Ct);
        var review = (await _store.GetColumnsAsync(_project, Ct, boardId)).First(column => column.Name == "Review");
        await _store.MoveCardAsync(_project, managed.Id, review.Id, null, Ct);

        var events = await _tool.AwaitBoardEvents(since: cursor, timeoutSeconds: 5, cancellationToken: Ct);
        Assert.Contains("2 event(s) since ", events);
        Assert.Contains($"· {Label(managed)} · lane Review · comment by agent:Worker · Tests pass; handoff saved.", events);
        Assert.Contains($"· {Label(managed)} · lane Review · change by", events);
        Assert.DoesNotContain("My own checkpoint", events);
        Assert.DoesNotContain("Noise on a manual card", events);
        var next = NextSince(events);
        Assert.True(DateTime.Parse(next.Split('~')[0], null, System.Globalization.DateTimeStyles.RoundtripKind)
            > DateTime.Parse(cursor.Split('~')[0], null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Contains($"- {Label(managed)} · lane Review", events);

        // scope=all also reports the manual card's comment.
        Assert.Contains("Noise on a manual card", await _tool.AwaitBoardEvents(since: cursor, timeoutSeconds: 1, scope: "all", cancellationToken: Ct));
    }

    [Fact]
    public async Task AwaitBoardEvents_AQuietWaitKeepsTheCursor_AndDoesNotRepeatTheSnapshot()
    {
        await ManagedBoardAsync();
        var cursor = NextSince(await _tool.AwaitBoardEvents(cancellationToken: Ct));
        var watch = Stopwatch.StartNew();
        var quiet = await _tool.AwaitBoardEvents(since: cursor, timeoutSeconds: 1, cancellationToken: Ct);
        watch.Stop();
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(10));
        Assert.Contains("No changes in 1 seconds.", quiet);
        Assert.Contains("Snapshot unchanged since your last call.", quiet);
        Assert.DoesNotContain("Agent cards (", quiet);
        Assert.Contains($"nextSince={cursor}", quiet);

        // A plain timestamp (the tag trimmed or from an older reply) still waits: with nothing to
        // compare it must not read as "changed" and return at once (VIBE-284 item 2). The caller's
        // snapshot is unknown, so it comes back with a tagged cursor.
        foreach (var plain in new[] { cursor.Split('~')[0], cursor.Split('~')[0] + "~" })
        {
            watch.Restart();
            var untagged = await _tool.AwaitBoardEvents(since: plain, timeoutSeconds: 1, cancellationToken: Ct);
            watch.Stop();
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(10));
            Assert.Contains("No changes in 1 seconds.", untagged);
            Assert.DoesNotContain("your cards changed", untagged);
            Assert.Contains("Agent cards (0 managed by the board manager", untagged);
            Assert.Contains($"nextSince={cursor}", untagged);
        }
    }

    [Fact]
    public async Task AwaitBoardEvents_WakesWhenOnlyTheSnapshotChanges_ForExampleANewWorker()
    {
        await ManagedBoardAsync();
        var managed = await _store.CreateCardAsync(_project, new(null, "Board managed", "", "base:claude", "high", null, [], false, WorkflowMode: "agent"), Ct);
        var cursor = NextSince(await _tool.AwaitBoardEvents(cancellationToken: Ct));
        // A session link writes no Card Log row; only the snapshot sees the open worker.
        await _store.LinkSessionAsync(_project, managed.Id, Guid.NewGuid().ToString(), "tab-w", "base:claude", "claude", "Worker", BoardSessionRecord.WorkerOrigin, Ct);
        var watch = Stopwatch.StartNew();
        var woke = await _tool.AwaitBoardEvents(since: cursor, timeoutSeconds: 10, cancellationToken: Ct);
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"waited {watch.Elapsed}");
        Assert.Contains("No new entries, but your cards changed", woke);
        Assert.Contains($"- {Label(managed)} · lane Backlog · priority high · board manager · worker base:claude · worker running", woke);
        Assert.Contains("1 implementation worker open of 1 allowed", woke);
        Assert.NotEqual(cursor, NextSince(woke));
    }

    [Fact]
    public async Task AwaitBoardEvents_TellsAManagerWhoseBoardLostItsManagerToStop_AndRejectsBadInput()
    {
        var boardId = await ManagedBoardAsync();
        await _store.SaveBoardManagerAsync(_project, boardId, BoardManagerSettings.None, 1, Ct);
        Assert.Contains("This board has no board manager", await _tool.AwaitBoardEvents(cancellationToken: Ct));

        Assert.StartsWith("FAIL: since must be the nextSince", await _tool.AwaitBoardEvents(since: "yesterday", cancellationToken: Ct));
        Assert.StartsWith("FAIL: scope must be managed or all", await _tool.AwaitBoardEvents(scope: "everything", cancellationToken: Ct));
        Assert.StartsWith("FAIL: board not found", await _tool.AwaitBoardEvents(board: "No such board", cancellationToken: Ct));
    }
}
