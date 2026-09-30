using Microsoft.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCardActionsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-actions-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly string connectionString;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCardActionsTests()
    {
        Directory.CreateDirectory(root);
        connectionString = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(connectionString, connectionString);
    }

    private async Task<BoardCardRecord> Card(string title, string description = "")
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        return await store.CreateCardAsync(root, new(null, title, description, null, "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task MergePreservesContentAndActivity_AndRetainsSourceRows()
    {
        var source = await Card("Source", "Original");
        var target = await Card("Destination", "Keep this");
        var related = await Card("Related");
        var attachment = (await store.AddAttachmentContentAsync(root, source.Id, "note.txt", "text/plain", [1, 2, 3], Ct))!;
        await store.UpdateCardAsync(root, source.Id, new(Description: $"File ![note](attachment:{attachment.Id})"), Ct);
        var body = new string('x', 5000) + $" attachment:{attachment.Id}";
        await store.AddNoteAsync(root, source.Id, BoardAuthor.Agent("Codex", "codex", "session"), body, Ct);
        await store.LinkSessionAsync(root, source.Id, "session", "tab", "base:codex", "codex", "Work", "mcp", Ct);
        await store.LinkCardAsync(root, source.Id, related.Id, Ct);
        const string sha = "0123456789abcdef";
        var snapshot = new SandboxDiffResponse([new("code.cs", "csharp", "before", "after")], 1);
        await store.AddCommitAsync(root, source.Id, sha, "Rob", "Saved code", DateTime.UtcNow, snapshot, Ct);
        var result = await store.MergeCardsAsync(root, source.Id, target.Id, Ct);
        Assert.Equal(target.Id, result!.Id);
        Assert.Equal(target.Key, result.Key);
        Assert.Equal("Destination", result.Title);
        Assert.StartsWith("Keep this", result.Description);
        Assert.Null(await store.FindCardAsync(root, source.Id, Ct));
        var detail = (await store.GetCardDetailAsync(root, target.Id, Ct))!;
        var copied = Assert.Single(detail.Attachments);
        Assert.NotEqual(attachment.Id, copied.Id);
        Assert.Contains("attachment:" + copied.Id, result.Description);
        Assert.Contains(detail.Comments, c => c.Body == body.Replace(attachment.Id, copied.Id) && c.Author.SessionId == "session");
        Assert.Equal("session", Assert.Single(detail.Sessions).SessionId);
        Assert.Equal(related.Id, Assert.Single(detail.LinkedCards).Id);
        Assert.Equal(sha, Assert.Single(detail.Commits).Sha);
        Assert.Equal(snapshot.Files, (await store.GetCommitSnapshotAsync(root, target.Id, sha, Ct))!.Files);
        Assert.Empty(detail.Notes);
        Assert.Equal(target.Id, (await store.FindSessionLinkAsync("session", Ct))!.CardId);
        await using var db = new SqliteConnection(connectionString);
        await db.OpenAsync(Ct);
        await using var read = db.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM BoardAttachments WHERE Id = $id;";
        read.Parameters.AddWithValue("$id", attachment.Id);
        Assert.Equal(1L, await read.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task InvalidMergeLeavesBothCardsUntouched()
    {
        var source = await Card("Source", "text");
        var target = await Card("Target", new string('x', 100_000));
        await Assert.ThrowsAsync<BoardValidationException>(() => store.MergeCardsAsync(root, source.Id, target.Id, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => store.MergeCardsAsync(root, source.Id, source.Key, Ct));
        Assert.Null(await store.MergeCardsAsync(root + "other", source.Id, target.Id, Ct));
        Assert.Equal("text", (await store.FindCardAsync(root, source.Id, Ct))!.Description);
        Assert.Equal(100_000, (await store.FindCardAsync(root, target.Id, Ct))!.Description.Length);
    }

    [Fact]
    public async Task MergeOverAttachmentLimitLeavesDescriptionsAndFilesUntouched()
    {
        var source = await Card("Source", "source text");
        var target = await Card("Target", "target text");
        await store.AddAttachmentContentAsync(root, source.Id, "source.txt", "text/plain", [1], Ct);
        for (var i = 0; i < BoardAttachmentData.MaxAttachmentsPerCard; i++)
            await store.AddAttachmentContentAsync(root, target.Id, $"{i}.txt", "text/plain", [2], Ct);

        await Assert.ThrowsAsync<BoardValidationException>(() => store.MergeCardsAsync(root, source.Id, target.Id, Ct));
        var sourceAfter = (await store.GetCardDetailAsync(root, source.Id, Ct))!;
        var targetAfter = (await store.GetCardDetailAsync(root, target.Id, Ct))!;
        Assert.Equal("source text", sourceAfter.Card.Description);
        Assert.Single(sourceAfter.Attachments);
        Assert.Equal("target text", targetAfter.Card.Description);
        Assert.Equal(BoardAttachmentData.MaxAttachmentsPerCard, targetAfter.Attachments.Count);
    }

    [Fact]
    public async Task OnlyHumanCanDeleteDiscussion_AndLegacyNotesJoinComments()
    {
        var card = await Card("Comments");
        var comment = (await store.AddNoteAsync(root, card.Id, BoardAuthor.Agent("Codex", "codex", "s"), "checkpoint", Ct))!;
        Assert.Equal("comment", comment.Kind);
        await Assert.ThrowsAsync<BoardValidationException>(() => store.DeleteCommentAsync(root, card.Id, comment.Id, BoardAuthor.Agent("Codex", "codex", "s"), Ct));
        Assert.False(await store.DeleteCommentAsync(root + "other", card.Id, comment.Id, BoardAuthor.User(), Ct));
        var other = await Card("Other");
        Assert.False(await store.DeleteCommentAsync(root, other.Id, comment.Id, BoardAuthor.User(), Ct));
        Assert.True(await store.DeleteCommentAsync(root, card.Id, comment.Id, BoardAuthor.User(), Ct));
        var detail = (await store.GetCardDetailAsync(root, card.Id, Ct))!;
        Assert.Empty(detail.Comments);
        Assert.Empty(await store.GetNotesAsync(root, card.Id, Ct));
        Assert.Equal(0, detail.Card.CommentCount);
        Assert.True(await store.HasLogEntryAsync(comment.Id, Ct));
        Assert.False(await store.DeleteCommentAsync(root, card.Id, comment.Id, BoardAuthor.User(), Ct));
    }

    [Fact]
    public async Task TransferKeepsIdentityAndScopesDeliveryMarksToEachBoard()
    {
        var card = await Card("Moving", "Keep everything");
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "discussion", Ct);
        var original = await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct);
        await store.MarkLogEntriesSentAsync(original.Select((entry, i) => KeyValuePair.Create(entry.Entry.Id, (long)i + 1)).ToList(), Ct);
        var board = await store.CreateBoardAsync(root, "Destination", Ct);
        var lane = (await store.GetColumnsAsync(root, Ct, board.Id))[0];
        var moved = (await store.MoveCardAsync(root, card.Id, lane.Id, null, Ct))!;
        Assert.Equal(card.Key, moved.Key);
        Assert.Equal(card.DisplayId, moved.DisplayId);
        Assert.Equal(board.Id, moved.BoardId);
        Assert.Equal(0, await store.GetMaxAcknowledgedSequenceAsync(board.Id, Ct));
        Assert.Contains(await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct), e => e.Entry.Kind == "deleted");
        var destination = await store.GetUnsentLogEntriesAsync(board.Id, 100, Ct);
        Assert.Contains(destination, e => e.Entry.Kind == "created");
        Assert.Contains(destination, e => e.Entry.Kind == "comment" && e.Entry.Body == "discussion");
        await store.MarkLogEntriesSentAsync(destination.Select((entry, i) => KeyValuePair.Create(entry.Entry.Id, (long)i + 1)).ToList(), Ct);
        Assert.Equal("discussion", Assert.Single((await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);
        await store.UpdateCardAsync(root, card.Id, new(ColumnId: card.ColumnId), Ct);
        Assert.Equal(card.BoardId, (await store.FindCardAsync(root, card.Id, Ct))!.BoardId);
        Assert.Equal("discussion", Assert.Single((await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);
    }

    [Fact]
    public async Task OlderSyncResetCannotSendDepartureOrOldFieldsToDestination()
    {
        var card = await Card("Moving", "original");
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "discussion", Ct);
        var board = await store.CreateBoardAsync(root, "Destination", Ct);
        var lane = (await store.GetColumnsAsync(root, Ct, board.Id))[0];
        await store.MoveCardAsync(root, card.Id, lane.Id, null, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Description: "current"), Ct);
        var sourceOutbox = await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct);
        var departure = Assert.Single(sourceOutbox, e => e.Entry.Kind == "deleted");

        // The preceding release resets all marks by the card's current board, including 0.
        await using var db = new SqliteConnection(connectionString);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.Parameters.AddWithValue("$board", board.Id);
        command.CommandText = """
            UPDATE BoardComments SET RemoteSeq = NULL WHERE RemoteSeq IS NOT NULL
              AND CardId IN (SELECT c.Id FROM BoardCards c JOIN BoardColumns k ON k.Id = c.ColumnId WHERE k.BoardId = $board);
            """;
        await command.ExecuteNonQueryAsync(Ct);
        command.CommandText = """
            SELECT COUNT(*) FROM BoardComments m JOIN BoardCards c ON c.Id = m.CardId
            JOIN BoardColumns k ON k.Id = c.ColumnId
            WHERE k.BoardId = $board AND m.RemoteSeq IS NULL AND m.SyncBoardId IS NOT NULL;
            """;
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
        Assert.Equal(sourceOutbox.Select(e => e.Entry.Id), (await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct)).Select(e => e.Entry.Id));
        Assert.DoesNotContain(await store.GetUnsentLogEntriesAsync(board.Id, 100, Ct), e => e.Entry.Id == departure.Entry.Id);

        await store.MarkLogEntriesSentAsync(sourceOutbox.Select((e, i) => KeyValuePair.Create(e.Entry.Id, (long)i + 1)).ToList(), Ct);
        Assert.Empty(await store.GetUnsentLogEntriesAsync(card.BoardId, 100, Ct));
        Assert.Equal(sourceOutbox.Count, await store.GetMaxAcknowledgedSequenceAsync(card.BoardId, Ct));
        await store.ResetSentMarksAsync(card.BoardId, Ct);
        Assert.Equal(sourceOutbox.Count, await store.CountUnsentLogEntriesAsync(card.BoardId, Ct));
        Assert.True(await store.RejectLogEntryAsync(card.BoardId, departure.Entry.Id, Ct));
        Assert.Equal(departure.Entry.Id, Assert.Single(await store.GetRejectedLogEntriesAsync(card.BoardId, 100, Ct)).EntryId);
        Assert.Equal("current", (await store.FindCardAsync(root, card.Id, Ct))!.Description);
    }

    public void Dispose() => Directory.Delete(root, true);
}
