using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.PostProcessing;
using Xunit;

namespace Tests.Services.Board;

/// <summary>Run-once card post-processing (VIBE-185): store candidates, the conditional write and the pass.</summary>
public sealed class BoardPostProcessingTests : IDisposable
{
    private const string Sha = "abcdef0123456789abcdef0123456789abcdef01";
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-post-processing-" + Guid.NewGuid().ToString("N"));
    private readonly Lazy<BoardStore> lazyStore;
    private BoardStore store => lazyStore.Value;
    private readonly string stateConnection;
    private string BoardConnection => $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    // Every candidate read in these tests looks past the settle window.
    private static DateTime Later => DateTime.UtcNow.AddMinutes(1);

    public BoardPostProcessingTests()
    {
        Directory.CreateDirectory(root);
        stateConnection = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        lazyStore = new(() =>
        {
            _ = new Repository(stateConnection);
            return new BoardStore(BoardConnection, stateConnection);
        });
    }

    [Fact]
    public async Task ANewCardIsACandidateOnlyInTheLastLane()
    {
        var (card, lanes) = await CreateCardAsync();
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));

        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var candidate = Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
        Assert.Equal(card.Id, candidate.CardId);
        Assert.Equal(lanes[^1].Id, candidate.ColumnId);
        Assert.Equal(lanes[^1].Name, candidate.LaneName);
        Assert.Equal(card.Key, candidate.Key);
        Assert.Equal(0, candidate.SessionCount);
        Assert.Equal(0, candidate.CodeLinkCount);
        Assert.Empty(candidate.CommitShas);
    }

    [Fact]
    public async Task TheCandidateCarriesSessionsGitHubLinksAndCommits()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.LinkSessionAsync(root, card.Id, "session-1", null, "base:claude", "claude", "Claude", BoardSessionRecord.LaunchOrigin, Ct);
        await store.AddCodeLinkAsync(root, card.Id, "https://github.com/org/repo/pull/7", Ct);
        await store.AddCommitAsync(root, card.Id, Sha, "Author", "Work", DateTime.UtcNow, new SandboxDiffResponse([], 0), Ct);
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);

        var candidate = Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
        Assert.Equal(1, candidate.SessionCount);
        Assert.Equal(1, candidate.CodeLinkCount);
        Assert.Equal(new[] { Sha }, candidate.CommitShas);
    }

    [Fact]
    public async Task ACardTouchedInsideTheSettleWindowWaits()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(DateTime.UtcNow.AddMinutes(-10), 10, Ct));
    }

    [Fact]
    public async Task DeletedCardsAndOneLaneBoardsAreNeverCandidates()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        await store.DeleteCardAsync(root, card.Id, Ct);

        var single = await store.CreateBoardAsync(root, "Inbox", Ct);
        var singleLanes = await store.GetColumnsAsync(root, Ct, single.Id);
        foreach (var lane in singleLanes.Skip(1))
            await store.DeleteColumnAsync(root, lane.Id, Ct);
        Assert.Single(await store.GetColumnsAsync(root, Ct, single.Id));
        await store.CreateCardAsync(root, new NewBoardCard(singleLanes[0].Id, "Only lane", "", null, "medium", null, [], false), Ct);

        Assert.Empty(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
    }

    [Fact]
    public async Task CardsFromBeforeTheUpgradeAndOlderBinariesAreAlreadyProcessed()
    {
        var (existing, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, existing.Id, lanes[^1].Id, null, Ct);
        // Disposable fixture: the schema immediately before board-post-processing/1.
        await ExecuteAsync("""
            DROP INDEX IX_BoardCards_PostProcessPending;
            ALTER TABLE BoardCards DROP COLUMN PostProcessed;
            ALTER TABLE BoardCards DROP COLUMN GhostReason;
            DELETE FROM SchemaMigrations WHERE Component = 'board-post-processing';
            """);

        var upgraded = new BoardStore(BoardConnection, stateConnection);
        Assert.Empty(await upgraded.GetPostProcessingCandidatesAsync(Later, 10, Ct));

        // An older binary's insert names none of the new columns.
        await ExecuteAsync($"""
            INSERT INTO BoardCards (Id, ProjectPath, Number, ColumnId, Position, Title, CreatedUTC, UpdatedUTC)
            VALUES ('card_older', '{root.Replace("'", "''")}', 999, '{lanes[^1].Id}', 0, 'Older binary', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
            """);
        Assert.Empty(await upgraded.GetPostProcessingCandidatesAsync(Later, 10, Ct));
        Assert.Null((await upgraded.FindCardAsync(root, existing.Id, Ct))!.GhostReason);
    }

    [Fact]
    public async Task CompletingAppliesTheGhostOnceWithLocalHistoryAndNoActivity()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var candidate = Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));

        Assert.True(await store.CompletePostProcessingAsync(candidate, new(BoardGhostReasons.NoCode), Ct));
        Assert.False(await store.CompletePostProcessingAsync(candidate, new(BoardGhostReasons.NoCode), Ct));
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));

        var after = (await store.FindCardAsync(root, card.Id, Ct))!;
        Assert.Equal(BoardGhostReasons.NoCode, after.GhostReason);
        Assert.Equal(candidate.UpdatedUtc, after.UpdatedUtc);
        var (body, remoteSeq, author) = await ReadLastLogAsync(card.Id);
        Assert.Contains("no commit or GitHub link", body);
        Assert.Equal(0L, remoteSeq);
        Assert.Equal("VibeRails post-processing", author);
    }

    [Fact]
    public async Task CompletingWithoutAGhostOnlyMarksTheCardProcessed()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var candidate = Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
        var logs = await CountLogsAsync(card.Id);

        Assert.True(await store.CompletePostProcessingAsync(candidate, new(), Ct));
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        Assert.Equal(logs, await CountLogsAsync(card.Id));
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
    }

    [Fact]
    public async Task ACardThatMovedOrChangedAfterTheReadIsLeftForALaterPass()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var candidate = Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));

        await Task.Delay(20, Ct);
        await store.AddCodeLinkAsync(root, card.Id, "https://github.com/org/repo/pull/9", Ct);
        Assert.False(await store.CompletePostProcessingAsync(candidate, new(BoardGhostReasons.NoCode), Ct));

        var fresh = Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
        await store.MoveCardAsync(root, card.Id, lanes[0].Id, null, Ct);
        Assert.False(await store.CompletePostProcessingAsync(fresh, new(BoardGhostReasons.NoCode), Ct));
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
    }

    [Fact]
    public async Task ClearingTheGhostIsAnOrdinaryUpdateWithLocalHistory()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        await store.CompletePostProcessingAsync(Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct)),
            new(BoardGhostReasons.NotLanded), Ct);

        var kept = await store.UpdateCardAsync(root, card.Id, new BoardCardPatch(Title: "Renamed"), Ct);
        Assert.Equal(BoardGhostReasons.NotLanded, kept!.GhostReason);

        var cleared = await store.UpdateCardAsync(root, card.Id, new BoardCardPatch(ClearGhost: true), Ct);
        Assert.Null(cleared!.GhostReason);
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        var (body, remoteSeq, _) = await ReadLastLogAsync(card.Id);
        Assert.Equal("Ghost cleared", body);
        Assert.Equal(0L, remoteSeq);
        // Clearing never re-queues the pass.
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
    }

    [Fact]
    public async Task ThePassRunsEveryProcessorOnceAndAppliesTheirOutcome()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.LinkSessionAsync(root, card.Id, "session-1", null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var counting = new CountingProcessor();
        var service = new BoardCardPostProcessingService(store, [new GhostCardPostProcessor(new FixedCheck(BoardCommitLanding.Landed)), counting]);

        var pass = await service.RunOnceAsync(DateTime.UtcNow.AddMinutes(11), Ct);
        Assert.Equal(new BoardPostProcessingPass(1, 1, 1, 0, 0), pass);
        Assert.Equal(BoardGhostReasons.NoCode, (await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);

        Assert.Equal(new BoardPostProcessingPass(0, 0, 0, 0, 0), await service.RunOnceAsync(DateTime.UtcNow.AddMinutes(11), Ct));
        Assert.Equal(1, counting.Calls);
    }

    [Fact]
    public async Task ADeferringProcessorHoldsTheCardUntilTheGiveUpWindow()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.LinkSessionAsync(root, card.Id, "session-1", null, "base:codex", "codex", "Codex", BoardSessionRecord.LaunchOrigin, Ct);
        await store.AddCommitAsync(root, card.Id, Sha, "Author", "Work", DateTime.UtcNow, new SandboxDiffResponse([], 0), Ct);
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var service = new BoardCardPostProcessingService(store, [new GhostCardPostProcessor(new FixedCheck(BoardCommitLanding.Failed))]);

        Assert.Equal(new BoardPostProcessingPass(1, 0, 0, 1, 0), await service.RunOnceAsync(DateTime.UtcNow.AddMinutes(11), Ct));
        Assert.Single(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));

        // A day later the card is processed without the outcome git could not decide.
        Assert.Equal(new BoardPostProcessingPass(1, 1, 0, 0, 0), await service.RunOnceAsync(DateTime.UtcNow.AddHours(25), Ct));
        Assert.Null((await store.FindCardAsync(root, card.Id, Ct))!.GhostReason);
        Assert.Empty(await store.GetPostProcessingCandidatesAsync(Later, 10, Ct));
    }

    [Fact]
    public async Task AThrowingProcessorDefersInsteadOfFailingThePass()
    {
        var (card, lanes) = await CreateCardAsync();
        await store.MoveCardAsync(root, card.Id, lanes[^1].Id, null, Ct);
        var service = new BoardCardPostProcessingService(store, [new ThrowingProcessor()]);

        Assert.Equal(new BoardPostProcessingPass(1, 0, 0, 1, 0), await service.RunOnceAsync(DateTime.UtcNow.AddMinutes(11), Ct));
    }

    private async Task<(BoardCardRecord Card, IReadOnlyList<BoardColumnRecord> Lanes)> CreateCardAsync()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        var lanes = await store.GetColumnsAsync(root, Ct);
        var card = await store.CreateCardAsync(root, new NewBoardCard(lanes[0].Id, "Work", "", null, "medium", null, [], false), Ct);
        return (card, lanes);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = new SqliteConnection(BoardConnection);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<(string Body, long? RemoteSeq, string Author)> ReadLastLogAsync(string cardId)
    {
        await using var db = new SqliteConnection(BoardConnection);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Body, RemoteSeq, AuthorLabel FROM BoardComments WHERE CardId = $card AND Kind = 'change' ORDER BY rowid DESC LIMIT 1;";
        command.Parameters.AddWithValue("$card", cardId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2));
    }

    private async Task<long> CountLogsAsync(string cardId)
    {
        await using var db = new SqliteConnection(BoardConnection);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BoardComments WHERE CardId = $card;";
        command.Parameters.AddWithValue("$card", cardId);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class FixedCheck(BoardCommitLanding answer) : IBoardDefaultBranchCheck
    {
        public Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken cancellationToken) =>
            Task.FromResult(answer);
    }

    private sealed class CountingProcessor : IBoardCardPostProcessor
    {
        public int Calls { get; private set; }
        public string Name => "counting";
        public Task<BoardCardPostProcessorOutcome> ProcessAsync(BoardPostProcessingCandidate card, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(BoardCardPostProcessorOutcome.Nothing);
        }
    }

    private sealed class ThrowingProcessor : IBoardCardPostProcessor
    {
        public string Name => "throwing";
        public Task<BoardCardPostProcessorOutcome> ProcessAsync(BoardPostProcessingCandidate card, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
