using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task Attention_DiscussionMarkerSurvivesSyncAndMergeWithoutCopyingLocalAlerts()
    {
        await _tool.CreateBoardCard("Source", cancellationToken: Ct);
        await _tool.CreateBoardCard("Destination", cancellationToken: Ct);
        var source = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        var destination = (await _store.FindCardAsync(_project, "PROJ-2", Ct))!;
        await _tool.UpdateBoardCard(source.Id, flagged: true, flagReason: "Need owner decision", cancellationToken: Ct);
        var original = Assert.Single(await _store.GetNotesAsync(_project, source.Id, Ct));
        var wire = BoardSyncService.ToWire(new(original, source.Key));
        Assert.True(wire.Changes!.Value.GetProperty("attention").GetProperty("to").GetBoolean());
        var imported = await _store.AddSyncedCommentAsync(_project, destination.Id, BoardAuthor.Agent("Remote agent", "codex", null),
            original.Body, "comment", new BoardSyncStamp("remote-attention", 1, DateTime.UtcNow, Changes: wire.Changes.Value.GetRawText(), BoardId: source.BoardId), Ct);
        Assert.True(BoardAttention.IsAttention(imported!.Changes));
        await _store.MergeCardsAsync(_project, source.Id, destination.Id, Ct);
        Assert.Equal(2, (await _store.GetNotesAsync(_project, destination.Id, Ct)).Count(entry => BoardAttention.IsAttention(entry.Changes)));
        Assert.Empty(await _store.GetAttentionSessionIdsAsync(["remote-session"], Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task Attention_RequiresReasonBeforeAnyMutation(string? reason)
    {
        await _tool.CreateBoardCard("Original", cancellationToken: Ct);
        var result = await _tool.UpdateBoardCard("PROJ-1", title: "Should not save", flagged: true, flagReason: reason, cancellationToken: Ct);
        Assert.StartsWith("FAIL: flagged=true requires flagReason", result);
        var card = (await _store.GetCardDetailAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal("Original", card.Card.Title);
        Assert.False(card.Card.Flagged);
        Assert.Empty(card.Comments);
    }

    [Fact]
    public async Task Attention_InvalidReasonOrFieldsSaveNothing()
    {
        await _tool.CreateBoardCard("Original", cancellationToken: Ct);
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard("PROJ-1", flagged: true, flagReason: new string('x', 50_001), cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard("PROJ-1", flagReason: "Reason without a flag", cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard("PROJ-1", flagged: false, flagReason: "Contradiction", cancellationToken: Ct));
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard("PROJ-1", priority: "invalid", flagged: true, flagReason: "Needs a decision", cancellationToken: Ct));
        var card = (await _store.GetCardDetailAsync(_project, "PROJ-1", Ct))!;
        await Assert.ThrowsAsync<BoardValidationException>(() => _store.UpdateCardAsync(_project, card.Card.Id,
            new(Flagged: true), Ct, BoardAuthor.Agent("Codex", "codex", "agent")));
        Assert.False(card.Card.Flagged);
        Assert.Empty(card.Comments);
    }

    [Fact]
    public async Task Attention_CommentFailureRollsBackFlagAndOtherFields()
    {
        await _tool.CreateBoardCard("Original", cancellationToken: Ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var trigger = connection.CreateCommand();
        trigger.CommandText = "CREATE TRIGGER FailAttention BEFORE INSERT ON BoardAttentionRequests BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
        await trigger.ExecuteNonQueryAsync(Ct);
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard("PROJ-1", title: "Partial", flagged: true, flagReason: "Major bug needs a decision", cancellationToken: Ct));
        var card = (await _store.GetCardDetailAsync(_project, "PROJ-1", Ct))!;
        Assert.False(card.Card.Flagged);
        Assert.Equal("Original", card.Card.Title);
        Assert.Empty(card.Comments);
    }

    [Fact]
    public async Task Attention_PersistsAttributionAndClearsOnlyResolvedCards()
    {
        _resolver.CurrentSessionId = "agent-a";
        await _tool.CreateBoardCard("First", cancellationToken: Ct);
        await _tool.CreateBoardCard("Second", cancellationToken: Ct);
        await _store.LinkSessionAsync(_project, "PROJ-1", "agent-b", "tab-b", "base:codex", "codex", "Other agent", "mcp", Ct);
        Assert.StartsWith("Updated", await _tool.UpdateBoardCard("PROJ-1", flagged: true, flagReason: "  Need the supported platform before implementing this.  ", cancellationToken: Ct));
        await _tool.UpdateBoardCard("PROJ-2", flagged: true, flagReason: "Security issue needs a decision", cancellationToken: Ct);
        var reopened = new BoardStore(_connectionString, _stateConnectionString);
        Assert.Equal(new[] { "agent-a" }, await reopened.GetAttentionSessionIdsAsync(["agent-a", "agent-b"], Ct));
        var detail = (await _service.GetCardAsync(_project, "PROJ-1", Ct))!;
        var comment = Assert.Single(detail.Comments);
        Assert.True(comment.IsAttention);
        Assert.Equal("agent-a", comment.Author.SessionId);
        Assert.Equal("Need the supported platform before implementing this.", comment.Body);
        Assert.Contains("ATTENTION:", await _tool.GetBoardCard("PROJ-1", cancellationToken: Ct));
        await _tool.UpdateBoardCard("PROJ-1", flagged: false, cancellationToken: Ct);
        Assert.Contains("agent-a", await reopened.GetAttentionSessionIdsAsync(["agent-a"], Ct));
        await _tool.UpdateBoardCard("PROJ-2", flagged: false, cancellationToken: Ct);
        Assert.Empty(await reopened.GetAttentionSessionIdsAsync(["agent-a"], Ct));
        Assert.True(Assert.Single((await _service.GetCardAsync(_project, "PROJ-1", Ct))!.Comments).IsAttention);
    }

    [Fact]
    public async Task Attention_OlderWriterCanClearWithoutReactivatingHistoricalRequests()
    {
        _resolver.CurrentSessionId = "agent-a";
        await _tool.CreateBoardCard("First", cancellationToken: Ct);
        await _tool.UpdateBoardCard("PROJ-1", flagged: true, flagReason: "Needs owner decision", cancellationToken: Ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        // An older binary knows only the existing Flagged column.
        command.CommandText = "UPDATE BoardCards SET Flagged=0; UPDATE BoardCards SET Flagged=1;";
        await command.ExecuteNonQueryAsync(Ct);
        Assert.Empty(await _store.GetAttentionSessionIdsAsync(["agent-a"], Ct));
        command.CommandText = "SELECT COUNT(*) FROM BoardAttentionRequests WHERE ResolvedUTC IS NOT NULL;";
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
        _resolver.CurrentSessionId = "agent-b";
        await _tool.UpdateBoardCard("PROJ-1", flagged: true, flagReason: "New unresolved issue", cancellationToken: Ct);
        Assert.Equal(new[] { "agent-b" }, await _store.GetAttentionSessionIdsAsync(["agent-a", "agent-b"], Ct));
    }

    [Fact]
    public async Task Attention_WorkerAlertsOnlyItsOuterAutomationRecording()
    {
        _resolver.CurrentSessionId = "worker-a";
        await _tool.CreateBoardCard("First", cancellationToken: Ct);
        await _tool.UpdateBoardCard("PROJ-1", flagged: true, flagReason: "Security decision", cancellationToken: Ct);
        var state = $"Data Source={Path.Combine(_root, "attention-state.db")};Pooling=False";
        await using var connection = new SqliteConnection(state);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE JobRuns (Id TEXT, TerminalSessionId TEXT);
            CREATE TABLE JobRunActions (RunId TEXT, SessionId TEXT);
            INSERT INTO JobRuns VALUES ('run-a', 'outer-a'), ('run-b', 'outer-b');
            INSERT INTO JobRunActions VALUES ('run-a', 'worker-a'), ('run-b', 'worker-b');
            """;
        await command.ExecuteNonQueryAsync(Ct);
        var store = new BoardStore(_connectionString, state);
        var sessions = await store.GetAttentionSessionIdsAsync(["outer-a", "outer-b", "worker-a"], Ct);
        Assert.Equal(new[] { "outer-a", "worker-a" }, sessions.Order());
    }
}
