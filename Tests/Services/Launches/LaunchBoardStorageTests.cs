using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services.Launches;

public sealed class LaunchBoardStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "viberails-launch-board-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore board;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public LaunchBoardStorageTests()
    {
        Directory.CreateDirectory(root);
        board = new BoardStore($"Data Source={Path.Combine(root, "board.db")};Pooling=False", $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
    }

    [Fact]
    public async Task LaneEntriesRetainOrderAndMoveSkipIsAtomic()
    {
        await board.EnsureDefaultColumnsAsync(root, Ct);
        var columns = await board.GetColumnsAsync(root, Ct);
        var lane = columns[1];
        await board.SaveLaunchLaneAsync(root, lane.Id, new([new("preset_a", "preset", "review"), new("action_b", "action")], 0), Ct);
        var card = await board.CreateCardAsync(root, new(columns[0].Id, "Card", "", null, "medium", null, [], false), Ct);
        await board.MoveCardAsync(root, card.Id, lane.Id, null, false, Ct);
        var events = await board.GetLaunchEventsAsync(root, card.Id, Ct);
        Assert.Equal(new[] { "preset_a", "action_b" }, events.Select(e => e.DefinitionId));
        Assert.All(events, e => Assert.True(e.Current));
        Assert.All(events, e => Assert.False(BoardLaunchWorkflow.ReleasesNext(e)));
        await board.MoveCardAsync(root, card.Id, columns[0].Id, null, false, Ct);
        await board.MoveCardAsync(root, card.Id, lane.Id, null, true, Ct);
        events = await board.GetLaunchEventsAsync(root, card.Id, Ct);
        Assert.Equal(2, events.Count(e => e.Current));
        Assert.All(events.Where(e => e.Current), e => Assert.True(e.SkipRequested));
    }

    [Fact]
    public async Task AcknowledgmentAndRetryAreCompareAndSwapAndDoNotLoseReports()
    {
        await board.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await board.GetColumnsAsync(root, Ct))[0];
        await board.SaveLaunchLaneAsync(root, lane.Id, new([new("preset_a", "preset")], 0), Ct);
        var card = await board.CreateCardAsync(root, new(lane.Id, "Card", "", null, "medium", null, [], false), Ct);
        var entry = Assert.Single(await board.GetLaunchEventsAsync(root, card.Id, Ct));
        Assert.True(await board.AcknowledgeLaunchEventAsync(entry.Id, "run_original", Ct));
        Assert.True(await board.AcknowledgeLaunchEventAsync(entry.Id, "run_original", Ct));
        Assert.False(await board.AcknowledgeLaunchEventAsync(entry.Id, "run_wrong", Ct));
        await board.ReportLaunchEventAsync(root, card.Id, entry.Id, "failed", "Needs work", "session", null, ct: Ct);
        Assert.True(await board.RetryLaunchEventAsync(entry.Id, "run_original", "run_retry", Ct));
        Assert.False(await board.RetryLaunchEventAsync(entry.Id, "run_original", "run_duplicate", Ct));
        Assert.False(await board.ReportLaunchEventAsync(root, card.Id, entry.Id, "passed", "Late original report",
            "old-session", null, ct: Ct, expectedRunId: "run_original"));
        var retried = Assert.Single(await board.GetLaunchEventsAsync(root, card.Id, Ct));
        Assert.Equal("run_retry", retried.RunId);
        Assert.Null(retried.Verdict);
        await board.ReportLaunchEventAsync(root, card.Id, entry.Id, "skipped", "Skip", null, null, true, Ct);
        Assert.False(await board.RetryLaunchEventAsync(entry.Id, "run_retry", "run_too_late", Ct));
    }

    [Fact]
    public async Task LaneSavesConflictAndDoNotRewriteExistingEntries()
    {
        await board.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await board.GetColumnsAsync(root, Ct))[0];
        await board.SaveLaunchLaneAsync(root, lane.Id, new([new("preset_a", "preset")], 0), Ct);
        var card = await board.CreateCardAsync(root, new(lane.Id, "Card", "", null, "medium", null, [], false), Ct);
        await Assert.ThrowsAsync<BoardConflictException>(() => board.SaveLaunchLaneAsync(root, lane.Id, new([], 0), Ct));
        await board.SaveLaunchLaneAsync(root, lane.Id, new([new("preset_b", "preset")], 1), Ct);
        Assert.Equal("preset_a", Assert.Single(await board.GetLaunchEventsAsync(root, card.Id, Ct)).DefinitionId);
    }

    [Fact]
    public async Task FailedReviewOnOlderCard_DoesNotStarveNewerCardsSharedDefinition()
    {
        var runs = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        var launches = new LaunchService(runs, Mock.Of<IAutomationScriptService>(), board);
        var next = (await launches.SavePresetAsync(null, new("Next step", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        await board.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await board.GetColumnsAsync(root, Ct))[0];
        await board.SaveLaunchLaneAsync(root, lane.Id, new([new("review", "preset", "review"), new(next.Id, "preset")], 0), Ct);
        var older = await board.CreateCardAsync(root, new(lane.Id, "Older", "", null, "medium", null, [], false), Ct);
        var newer = await board.CreateCardAsync(root, new(lane.Id, "Newer", "", null, "medium", null, [], false), Ct);
        foreach (var card in new[] { older, newer })
        {
            var review = (await board.GetLaunchEventsAsync(root, card.Id, Ct))[0];
            var runId = "review-" + card.Id;
            await board.AcknowledgeLaunchEventAsync(review.Id, runId, Ct);
            await board.UpdateLaunchEventAsync(review.Id, runId, "succeeded", Ct);
            await board.ReportLaunchEventAsync(root, card.Id, review.Id,
                card.Id == older.Id ? "failed" : "passed", "Review result", null, null, ct: Ct);
        }
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "board.db")};Pooling=False"))
        {
            await db.OpenAsync(Ct);
            await using var due = db.CreateCommand();
            due.CommandText = "UPDATE BoardLaunchEvents SET DueUnixMs = CASE WHEN CardId = $older THEN 1 ELSE 2 END;";
            due.Parameters.AddWithValue("$older", older.Id);
            await due.ExecuteNonQueryAsync(Ct);
        }

        await new BoardLaunchWorkflow(board, runs, launches, null!).TickAsync(Ct);

        var blocked = (await board.GetLaunchEventsAsync(root, older.Id, Ct))[1];
        var queued = (await board.GetLaunchEventsAsync(root, newer.Id, Ct))[1];
        Assert.Null(blocked.RunId);
        Assert.NotNull(queued.RunId);
        var run = (await runs.GetRunAsync(queued.RunId, Ct))!;
        Assert.Equal(next.Id, run.DefinitionId);
        Assert.Equal(newer.Id, run.Snapshot.Context.CardId);
        Assert.Equal("queued", run.Status);
    }

    [Fact]
    public async Task ComponentInitializationNeverChangesLegacyDatabaseOrFolders()
    {
        var state = Path.Combine(root, "state.db");
        await using var old = new SqliteConnection($"Data Source={state};Pooling=False");
        await old.OpenAsync(Ct);
        await using var cmd = old.CreateCommand();
        cmd.CommandText = "CREATE TABLE Environments(Id INTEGER PRIMARY KEY, CustomPrompt TEXT); CREATE TABLE Jobs(Id INTEGER PRIMARY KEY, Name TEXT); INSERT INTO Environments VALUES(1,'{{step:old}}'); INSERT INTO Jobs VALUES(1,'old schedule');";
        await cmd.ExecuteNonQueryAsync(Ct);
        var folder = Directory.CreateDirectory(Path.Combine(root, "envs", "legacy"));
        var config = Path.Combine(folder.FullName, "settings.json");
        await File.WriteAllTextAsync(config, "retained config", Ct);
        var launches = SqliteStorage.CreateLaunchStore(state);
        Assert.Empty(await launches.ListPresetsAsync(Ct));
        cmd.CommandText = "SELECT CustomPrompt FROM Environments WHERE Id=1;";
        Assert.Equal("{{step:old}}", await cmd.ExecuteScalarAsync(Ct));
        cmd.CommandText = "INSERT INTO Jobs VALUES(2,'older version still writes'); SELECT count(*) FROM Jobs;";
        Assert.Equal(2L, await cmd.ExecuteScalarAsync(Ct));
        Assert.Equal("retained config", await File.ReadAllTextAsync(config, Ct));
        Assert.Equal(Path.Combine(root, "launches.db"), new SqliteStoragePaths(state).LaunchDatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}
