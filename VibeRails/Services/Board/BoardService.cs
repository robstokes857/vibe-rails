using System.Text.RegularExpressions;
using VibeRails.DTOs;
using VibeRails.Services.LlmClis;

namespace VibeRails.Services.Board;

/// <summary>
/// Board rules and wire mapping shared by the HTTP routes (dashboard) and the MCP tools. Has no
/// dependency on the dashboard's service graph so the stdio MCP host can construct it.
/// </summary>
public partial interface IBoardService
{
    // Boards. boardId null = the project's default (first) board everywhere it is optional.
    Task<BoardListResponse> GetBoardsAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<BoardSummaryResponse> CreateBoardAsync(string projectPath, CreateBoardRequest request, CancellationToken cancellationToken = default);
    Task<BoardSummaryResponse?> UpdateBoardAsync(string projectPath, string boardId, UpdateBoardRequest request, CancellationToken cancellationToken = default);
    Task<DeleteBoardResponse?> DeleteBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default);
    /// <summary>Resolves a board by id or (case-insensitive) name within the project.</summary>
    Task<BoardRecord?> FindBoardAsync(string projectPath, string idOrName, CancellationToken cancellationToken = default);

    Task<BoardColumnListResponse> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardColumnResponse> CreateColumnAsync(string projectPath, CreateBoardColumnRequest request, CancellationToken cancellationToken = default);
    Task<BoardColumnResponse?> UpdateColumnAsync(string projectPath, string columnId, UpdateBoardColumnRequest request, CancellationToken cancellationToken = default);
    Task<DeleteBoardColumnResponse?> DeleteColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default);
    Task<BoardColumnListResponse> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default, string? boardId = null);

    Task<BoardCardListResponse> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardCardResponse?> GetCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardResponse> CreateCardAsync(string projectPath, CreateBoardCardRequest request, CancellationToken cancellationToken = default, BoardAuthor? author = null);
    Task<BoardCardResponse?> UpdateCardAsync(string projectPath, string idOrKey, UpdateBoardCardRequest request, CancellationToken cancellationToken = default, BoardAuthor? author = null);
    Task<bool> DeleteCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCardResponse?> MoveCardAsync(string projectPath, string idOrKey, string columnIdOrName, int? position, CancellationToken cancellationToken = default);

    Task<BoardDescriptionHistoryResponse?> GetDescriptionHistoryAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);

    Task<BoardCommentDto?> AddCommentAsync(string projectPath, string idOrKey, BoardAuthor author, string body, CancellationToken cancellationToken = default);
    /// <summary>Agent scratchpad entry: same validation as a comment, never shown in the comment stream.</summary>
    Task<BoardCommentDto?> AddNoteAsync(string projectPath, string idOrKey, BoardAuthor author, string body, CancellationToken cancellationToken = default);
    Task<List<BoardCommentDto>?> GetNotesAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardAttachmentDto?> AddAttachmentAsync(string projectPath, string idOrKey, AddBoardAttachmentRequest request, CancellationToken cancellationToken = default);
    /// <summary>Agent-written Markdown/TXT attachment. Only these two types; the text is stored as UTF-8 bytes.</summary>
    Task<BoardAttachmentDto?> AddTextAttachmentAsync(string projectPath, string idOrKey, string name, string text, BoardAuthor author, CancellationToken cancellationToken = default);
    Task<BoardSessionOutcomeRecord?> FindSessionOutcomeAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAttachmentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default);

    Task<List<BoardCommitDto>?> GetCommitsAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardCommitDto?> LinkCommitAsync(string projectPath, string idOrKey, string sha, CancellationToken cancellationToken = default, string? gitWorkingDirectory = null);
    Task<bool> UnlinkCommitAsync(string projectPath, string idOrKey, string sha, CancellationToken cancellationToken = default);
    Task<SandboxDiffResponse?> GetCommitDiffAsync(string projectPath, string idOrKey, string sha, CancellationToken cancellationToken = default);

    Task<List<BoardSessionDto>?> GetSessionsAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
    Task<BoardSessionDto?> LinkSessionAsync(string projectPath, string idOrKey, string sessionId, string? tabId, string selection, string cli, string displayName, string origin, CancellationToken cancellationToken = default);
    Task<BoardSessionDto?> RenameSessionAsync(string projectPath, string idOrKey, string sessionId, string displayName, CancellationToken cancellationToken = default);
    Task<bool> UnlinkSessionAsync(string projectPath, string idOrKey, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a lane: by id anywhere in the project (lane ids are project-unique), else by
    /// case-insensitive name on the given board (null = the default board).
    /// </summary>
    Task<BoardColumnRecord?> FindColumnAsync(string projectPath, string idOrName, CancellationToken cancellationToken = default, string? boardId = null);
    Task<BoardCardRecord?> FindCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService(
    IBoardStore store,
    IBoardCommitService commits,
    IBoardLiveSessionProbe liveSessions) : IBoardService
{
    // Raised 2026-09-17 (description 20k→100k, comment 10k→50k, attachments 12→40 per card): the
    // first agents to work cards split multi-part reports across comments and hit the old caps.
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 100_000;
    public const int MaxCommentLength = 50_000;
    public const int MaxTags = 20;
    public const int MaxTagLength = 40;
    public const int MaxAttachmentsPerCard = BoardAttachmentData.MaxAttachmentsPerCard;
    public const int MaxColumnNameLength = 60;
    public const int MaxBoardNameLength = 60;
    public const int MaxSessionIdLength = 36;
    public static readonly IReadOnlyList<int> AllowedPoints = [1, 2, 3, 5, 8, 13];

    // ------------------------------------------------------------------ boards

    public async Task<BoardListResponse> GetBoardsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        await store.EnsureDefaultColumnsAsync(projectPath, cancellationToken);
        var boards = await store.GetBoardsAsync(projectPath, cancellationToken);
        var columns = await store.GetAllColumnsAsync(projectPath, cancellationToken);
        var counts = await store.CountCardsByBoardAsync(projectPath, cancellationToken);
        return new BoardListResponse(boards.Select(board => ToDto(board, columns, counts)).ToList());
    }

    public async Task<BoardSummaryResponse> CreateBoardAsync(string projectPath, CreateBoardRequest request, CancellationToken cancellationToken = default)
    {
        var name = NormalizeBoardName(request.Name) ?? throw new BoardValidationException("A board needs a name.");
        var board = await store.CreateBoardAsync(projectPath, name, cancellationToken);
        var columns = await store.GetColumnsAsync(projectPath, cancellationToken, board.Id);
        return ToDto(board, columns, new Dictionary<string, int>());
    }

    public async Task<BoardSummaryResponse?> UpdateBoardAsync(string projectPath, string boardId, UpdateBoardRequest request, CancellationToken cancellationToken = default)
    {
        var name = NormalizeBoardName(request.Name) ?? throw new BoardValidationException("A board needs a name.");
        var board = await store.RenameBoardAsync(projectPath, boardId, name, cancellationToken);
        if (board is null)
            return null;
        var columns = await store.GetColumnsAsync(projectPath, cancellationToken, board.Id);
        var counts = await store.CountCardsByBoardAsync(projectPath, cancellationToken);
        return ToDto(board, columns, counts);
    }

    public async Task<DeleteBoardResponse?> DeleteBoardAsync(string projectPath, string boardId, CancellationToken cancellationToken = default)
    {
        var result = await store.DeleteBoardAsync(projectPath, boardId, cancellationToken);
        return result is null ? null : new DeleteBoardResponse(true, result.DeletedColumns, result.DeletedCards);
    }

    public async Task<BoardRecord?> FindBoardAsync(string projectPath, string idOrName, CancellationToken cancellationToken = default)
    {
        var wanted = idOrName?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
            return null;
        await store.EnsureDefaultColumnsAsync(projectPath, cancellationToken);
        var boards = await store.GetBoardsAsync(projectPath, cancellationToken);
        return boards.FirstOrDefault(b => string.Equals(b.Id, wanted, StringComparison.Ordinal))
            ?? boards.FirstOrDefault(b => string.Equals(b.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ columns

    public async Task<BoardColumnListResponse> GetColumnsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null)
    {
        await store.EnsureDefaultColumnsAsync(projectPath, cancellationToken);
        var columns = await store.GetColumnsAsync(projectPath, cancellationToken, boardId);
        return new BoardColumnListResponse(columns.Select(ToDto).ToList());
    }

    public async Task<BoardColumnResponse> CreateColumnAsync(string projectPath, CreateBoardColumnRequest request, CancellationToken cancellationToken = default)
    {
        var name = NormalizeColumnName(request.Name) ?? "New lane";
        var wip = IsNoLimit(request.WipLimit) ? null : NormalizeWip(request.WipLimit);
        var color = NormalizeColor(request.Color) ?? "#64748b";
        await store.EnsureDefaultColumnsAsync(projectPath, cancellationToken);
        var column = await store.CreateColumnAsync(projectPath, name, wip, color, cancellationToken, NormalizeBoardId(request.BoardId));
        return ToDto(column);
    }

    public async Task<BoardColumnResponse?> UpdateColumnAsync(string projectPath, string columnId, UpdateBoardColumnRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name is null ? null : NormalizeColumnName(request.Name);
        if (request.Name is not null && name is null)
            throw new BoardValidationException("Lane name cannot be empty.");
        var color = request.Color is null ? null : NormalizeColor(request.Color);
        if (request.Color is not null && color is null)
            throw new BoardValidationException("Lane color must be a hex color like #3b82f6.");
        var clearWip = IsNoLimit(request.WipLimit);
        var column = await store.UpdateColumnAsync(projectPath, columnId, name, clearWip ? null : NormalizeWip(request.WipLimit), clearWip, color, cancellationToken);
        return column is null ? null : ToDto(column);
    }

    public async Task<DeleteBoardColumnResponse?> DeleteColumnAsync(string projectPath, string columnId, CancellationToken cancellationToken = default)
    {
        var result = await store.DeleteColumnAsync(projectPath, columnId, cancellationToken);
        return result is null ? null : new DeleteBoardColumnResponse(true, result.MovedToColumnId, result.MovedCards);
    }

    public async Task<BoardColumnListResponse> ReorderColumnsAsync(string projectPath, IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var columns = await store.ReorderColumnsAsync(projectPath, orderedIds, cancellationToken, NormalizeBoardId(boardId));
        return new BoardColumnListResponse(columns.Select(ToDto).ToList());
    }

    public async Task<BoardColumnRecord?> FindColumnAsync(string projectPath, string idOrName, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var wanted = idOrName?.Trim() ?? string.Empty;
        if (wanted.Length == 0)
            return null;
        await store.EnsureDefaultColumnsAsync(projectPath, cancellationToken);
        var everywhere = await store.GetAllColumnsAsync(projectPath, cancellationToken);
        var byId = everywhere.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.Ordinal));
        if (byId is not null)
            return byId;
        var columns = await store.GetColumnsAsync(projectPath, cancellationToken, NormalizeBoardId(boardId));
        return columns.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ cards

    public async Task<BoardCardListResponse> GetCardsAsync(string projectPath, CancellationToken cancellationToken = default, string? boardId = null)
    {
        var cards = await store.GetCardsAsync(projectPath, cancellationToken, NormalizeBoardId(boardId));
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        var activeByCard = new Dictionary<string, (string SessionId, string TabId)>(StringComparer.Ordinal);
        if (live.Count > 0)
        {
            foreach (var session in await store.GetSessionsForProjectAsync(projectPath, cancellationToken))
            {
                if (live.TryGetValue(session.SessionId, out var tabId) && !activeByCard.ContainsKey(session.CardId))
                    activeByCard[session.CardId] = (session.SessionId, tabId);
            }
        }

        return new BoardCardListResponse(cards.Select(card =>
        {
            activeByCard.TryGetValue(card.Id, out var active);
            return ToSummary(card, active.SessionId, active.TabId);
        }).ToList());
    }

    public async Task<BoardCardResponse?> GetCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var detail = await store.GetCardDetailAsync(projectPath, idOrKey, cancellationToken);
        return detail is null ? null : await ToDetailAsync(detail, cancellationToken);
    }

    public Task<BoardCardRecord?> FindCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default) =>
        store.FindCardAsync(projectPath, idOrKey, cancellationToken);

    public async Task<BoardCardResponse> CreateCardAsync(string projectPath, CreateBoardCardRequest request, CancellationToken cancellationToken = default, BoardAuthor? author = null)
    {
        var title = NormalizeTitle(request.Title) ?? throw new BoardValidationException("Title is required.");
        // A brand-new project's first card may arrive over MCP before anything listed the lanes.
        await store.EnsureDefaultColumnsAsync(projectPath, cancellationToken);
        var card = await store.CreateCardAsync(projectPath, new NewBoardCard(
            request.ColumnId,
            title,
            NormalizeDescription(request.Description),
            NormalizeAssignee(request.Assignee),
            NormalizePriority(request.Priority) ?? BoardPriorities.Default,
            NormalizePoints(request.Points),
            NormalizeTags(request.Tags) ?? [],
            request.Blocked ?? false,
            NormalizeBaseOptions(NormalizeAssignee(request.Assignee), request.BaseLlmOptions),
            Author: author,
            Type: NormalizeCardType(request.Type) ?? BoardCardTypes.Default,
            BoardId: NormalizeBoardId(request.BoardId)), cancellationToken);
        return (await GetCardAsync(projectPath, card.Id, cancellationToken))!;
    }

    public async Task<BoardCardResponse?> UpdateCardAsync(string projectPath, string idOrKey, UpdateBoardCardRequest request, CancellationToken cancellationToken = default, BoardAuthor? author = null)
    {
        // Append mode is optimistic on the revision this call reads. Without a caller-supplied
        // expected revision, a concurrent edit is retried once against the fresh text; with one,
        // the caller asked to be told. Every field, including the append, is validated before the
        // store write and lands in that single write -- an invalid priority must not leave the
        // appended text behind for a retry to duplicate.
        var retryOnConflict = request.DescriptionAppend is not null && request.ExpectedDescriptionRevision is null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await UpdateCardOnceAsync(projectPath, idOrKey, request, cancellationToken, author);
            }
            catch (BoardConflictException) when (retryOnConflict && attempt == 1)
            {
                // Re-read and append to the revision that won.
            }
        }
    }

    private async Task<BoardCardResponse?> UpdateCardOnceAsync(string projectPath, string idOrKey, UpdateBoardCardRequest request, CancellationToken cancellationToken, BoardAuthor? author)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;

        var description = request.Description;
        var expectedRevision = request.ExpectedDescriptionRevision;
        if (request.DescriptionAppend is not null)
        {
            if (description is not null)
                throw new BoardValidationException("Pass either description (replace) or descriptionAppend (append), not both.");
            var addition = request.DescriptionAppend.Replace("\r\n", "\n").Trim();
            if (addition.Length == 0)
                throw new BoardValidationException("Nothing to append.");
            description = existing.Description.Length == 0 ? addition : existing.Description + "\n\n" + addition;
            expectedRevision ??= existing.DescriptionRevision;
        }

        string? title = null;
        if (request.Title is not null)
            title = NormalizeTitle(request.Title) ?? throw new BoardValidationException("Title cannot be empty.");
        string? priority = null;
        if (request.Priority is not null)
            priority = NormalizePriority(request.Priority) ?? throw new BoardValidationException($"Priority must be one of: {string.Join(", ", BoardPriorities.All)}.");
        string? type = null;
        if (request.Type is not null)
            type = NormalizeCardType(request.Type) ?? throw new BoardValidationException($"Type must be one of: {string.Join(", ", BoardCardTypes.All)}.");

        // Points: "" or null clears; omitted stays Undefined and leaves the value alone.
        // Assignee: "" clears; null or omitted leaves it alone. The patch carries clear flags.
        var clearPoints = IsClearValue(request.Points);
        var points = clearPoints ? null : NormalizePoints(request.Points);
        var clearAssignee = request.Assignee is not null && string.IsNullOrWhiteSpace(request.Assignee);
        var assignee = clearAssignee ? null : NormalizeAssignee(request.Assignee);
        var finalAssignee = clearAssignee ? null : assignee ?? existing.Assignee;
        var assigneeChanged = !string.Equals(finalAssignee, existing.Assignee, StringComparison.Ordinal);
        var options = request.ClearBaseLlmOptions || request.BaseLlmOptions is null ? null : NormalizeBaseOptions(finalAssignee, request.BaseLlmOptions);
        var clearOptions = request.ClearBaseLlmOptions || assigneeChanged
            || (request.BaseLlmOptions is not null && options is null);
        if (options is not null) clearOptions = false;
        if (expectedRevision is < 1)
            throw new BoardValidationException("Description revision must be a positive number.");
        var normalizedDescription = description is null ? null : NormalizeDescription(description);
        var activeSessionIds = description is null ? null
            : (await liveSessions.GetLiveSessionsAsync(cancellationToken)).Keys.ToList();

        var patch = new BoardCardPatch(
            Title: title,
            Description: normalizedDescription,
            Assignee: assignee,
            ClearAssignee: clearAssignee,
            Priority: priority,
            Points: points,
            ClearPoints: clearPoints,
            Tags: NormalizeTags(request.Tags),
            Blocked: request.Blocked,
            ColumnId: request.ColumnId,
            ExpectedDescriptionRevision: expectedRevision,
            BaseLlmOptions: options,
            ClearBaseLlmOptions: clearOptions,
            Author: author,
            ActiveSessionIds: activeSessionIds,
            Type: type);
        var updated = await store.UpdateCardAsync(projectPath, existing.Id, patch, cancellationToken);
        if (updated is null) return null;
        var detail = await store.GetCardDetailAsync(projectPath, updated.Id, cancellationToken);
        return detail is null ? null : await ToDetailAsync(detail with { Card = updated }, cancellationToken);
    }

    public async Task<bool> DeleteCardAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        return existing is not null && await store.DeleteCardAsync(projectPath, existing.Id, cancellationToken);
    }

    public async Task<BoardCardResponse?> MoveCardAsync(string projectPath, string idOrKey, string columnIdOrName, int? position, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        // A lane name means a lane on the card's own board; an id can move it to another board.
        var column = await FindColumnAsync(projectPath, columnIdOrName, cancellationToken, NormalizeBoardId(existing.BoardId))
            ?? throw new BoardValidationException($"Lane not found: {columnIdOrName}");
        if (position is < 0)
            throw new BoardValidationException("Position cannot be negative.");
        var moved = await store.MoveCardAsync(projectPath, existing.Id, column.Id, position, cancellationToken);
        return moved is null ? null : await GetCardAsync(projectPath, moved.Id, cancellationToken);
    }

    // ------------------------------------------------------------------ rails

    public Task<BoardDescriptionHistoryResponse?> GetDescriptionHistoryAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default) =>
        store.GetDescriptionHistoryAsync(projectPath, idOrKey, cancellationToken);

    public async Task<BoardCommentDto?> AddCommentAsync(string projectPath, string idOrKey, BoardAuthor author, string body, CancellationToken cancellationToken = default)
    {
        var text = NormalizeCommentBody(body, "Comment");
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var comment = await store.AddCommentAsync(projectPath, existing.Id, author, text, cancellationToken);
        return comment is null ? null : ToDto(comment);
    }

    public async Task<BoardCommentDto?> AddNoteAsync(string projectPath, string idOrKey, BoardAuthor author, string body, CancellationToken cancellationToken = default)
    {
        var text = NormalizeCommentBody(body, "Note");
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var note = await store.AddNoteAsync(projectPath, existing.Id, author, text, cancellationToken);
        return note is null ? null : ToDto(note);
    }

    public async Task<List<BoardCommentDto>?> GetNotesAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var notes = await store.GetNotesAsync(projectPath, existing.Id, cancellationToken);
        return notes.Select(ToDto).ToList();
    }

    public async Task<BoardAttachmentDto?> AddAttachmentAsync(string projectPath, string idOrKey, AddBoardAttachmentRequest request, CancellationToken cancellationToken = default)
    {
        var content = DecodeAttachmentDataUrl(request.DataUrl);
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null)
            return null;
        var name = NormalizeAttachmentName(request.Name);
        var mimeType = DetectAttachmentMimeType(name, content);
        // The browser's declared byte count and MIME type are never authoritative.
        var attachment = await store.AddAttachmentContentAsync(projectPath, card.Id, name, mimeType, content, cancellationToken);
        return attachment is null ? null : ToDto(attachment);
    }

    public async Task<BoardAttachmentDto?> AddTextAttachmentAsync(string projectPath, string idOrKey, string name, string text, BoardAuthor author, CancellationToken cancellationToken = default)
    {
        var label = NormalizeAttachmentName(name);
        var extension = Path.GetExtension(label).ToLowerInvariant();
        if (extension is not (".md" or ".markdown" or ".txt"))
            throw new BoardValidationException("Agent attachments must be Markdown or TXT: name the file *.md or *.txt.");
        var body = (text ?? string.Empty).Replace("\r\n", "\n");
        if (body.Trim().Length == 0)
            throw new BoardValidationException("The attachment text cannot be empty.");
        if (body.Length > MaxAgentAttachmentTextCharacters)
            throw new BoardValidationException($"The attachment text is too long (max {MaxAgentAttachmentTextCharacters} characters).");
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null)
            return null;
        var content = new System.Text.UTF8Encoding(false).GetBytes(body);
        var mimeType = DetectAttachmentMimeType(label, content);
        var attachment = await store.AddAttachmentContentAsync(projectPath, card.Id, label, mimeType, content, cancellationToken, author);
        return attachment is null ? null : ToDto(attachment);
    }

    public Task<BoardSessionOutcomeRecord?> FindSessionOutcomeAsync(string sessionId, CancellationToken cancellationToken = default) =>
        store.FindSessionOutcomeAsync(sessionId, cancellationToken);

    private static string NormalizeCommentBody(string? body, string what)
    {
        var text = body?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new BoardValidationException($"{what} cannot be empty.");
        if (text.Length > MaxCommentLength)
            throw new BoardValidationException($"{what} is too long (max {MaxCommentLength} characters).");
        return text;
    }

    public async Task<bool> DeleteAttachmentAsync(string projectPath, string idOrKey, string attachmentId, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        return existing is not null && await store.DeleteAttachmentAsync(projectPath, existing.Id, attachmentId, cancellationToken);
    }

    public async Task<List<BoardCommitDto>?> GetCommitsAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var list = await store.GetCommitsAsync(projectPath, existing.Id, cancellationToken);
        return list.Select(ToDto).ToList();
    }

    public async Task<BoardCommitDto?> LinkCommitAsync(string projectPath, string idOrKey, string sha, CancellationToken cancellationToken = default, string? gitWorkingDirectory = null)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        // The MCP host supplies its own checkout, never a path supplied by the tool caller.
        // Capture everything before inserting the link, pinning reads to the resolved full sha.
        var checkout = gitWorkingDirectory ?? projectPath;
        var info = await commits.DescribeAsync(checkout, sha, cancellationToken);
        var diff = await commits.GetDiffAsync(checkout, info.Sha, cancellationToken);
        var snapshot = new SandboxDiffResponse(
            diff.Files.Select(f => new SandboxDiffFileResponse(f.FileName, f.Language, f.OriginalContent, f.ModifiedContent)).ToList(),
            diff.TotalChanges);
        var record = await store.AddCommitAsync(projectPath, existing.Id, info.Sha, info.Author, info.Message, info.CommittedUtc, snapshot, cancellationToken);
        return record is null ? null : ToDto(record);
    }

    public async Task<bool> UnlinkCommitAsync(string projectPath, string idOrKey, string sha, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return false;
        var fullSha = await ResolveUniqueLinkedShaAsync(projectPath, existing.Id, sha, cancellationToken);
        return fullSha is not null && await store.RemoveCommitAsync(projectPath, existing.Id, fullSha, cancellationToken);
    }

    public async Task<SandboxDiffResponse?> GetCommitDiffAsync(string projectPath, string idOrKey, string sha, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var fullSha = await ResolveUniqueLinkedShaAsync(projectPath, existing.Id, sha, cancellationToken)
            ?? throw new BoardValidationException("That commit is not linked to this card.");
        return await store.GetCommitSnapshotAsync(projectPath, existing.Id, fullSha, cancellationToken)
            ?? throw new BoardValidationException("This commit has no saved code snapshot. Unlink it and link it again while its checkout is available.");
    }

    public async Task<List<BoardSessionDto>?> GetSessionsAsync(string projectPath, string idOrKey, CancellationToken cancellationToken = default)
    {
        var detail = await store.GetCardDetailAsync(projectPath, idOrKey, cancellationToken);
        if (detail is null)
            return null;
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        return detail.Sessions.Select(s => ToDto(s, live)).ToList();
    }

    public async Task<BoardSessionDto?> LinkSessionAsync(string projectPath, string idOrKey, string sessionId, string? tabId, string selection, string cli, string displayName, string origin, CancellationToken cancellationToken = default)
    {
        var id = NormalizeSessionId(sessionId);
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var name = string.IsNullOrWhiteSpace(displayName) ? "Working session" : displayName.Trim();
        if (name.Length > 120) name = name[..120];
        var record = await store.LinkSessionAsync(projectPath, existing.Id, id, tabId, selection, cli, name, origin, cancellationToken);
        if (record is null)
            return null;
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        return ToDto(record, live);
    }

    public async Task<BoardSessionDto?> RenameSessionAsync(string projectPath, string idOrKey, string sessionId, string displayName, CancellationToken cancellationToken = default)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new BoardValidationException("Session name cannot be empty.");
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (existing is null)
            return null;
        var record = await store.RenameSessionAsync(projectPath, existing.Id, sessionId, name, cancellationToken);
        if (record is null)
            return null;
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        return ToDto(record, live);
    }

    public async Task<bool> UnlinkSessionAsync(string projectPath, string idOrKey, string sessionId, CancellationToken cancellationToken = default)
    {
        var existing = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        return existing is not null && await store.UnlinkSessionAsync(projectPath, existing.Id, sessionId, cancellationToken);
    }

    // ------------------------------------------------------------------ mapping

    private async Task<BoardCardResponse> ToDetailAsync(BoardCardDetailRecord detail, CancellationToken cancellationToken)
    {
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        var active = detail.Sessions.FirstOrDefault(s => live.ContainsKey(s.SessionId));
        var summary = ToSummary(detail.Card, active?.SessionId, active is null ? null : live[active.SessionId]);
        // Older comments may have been written before their session was linked.
        // Resolve those labels on read without rewriting historical comment rows.
        var authors = new Dictionary<string, BoardAuthor?>();
        var comments = await ResolveAuthorsAsync(detail.Comments, authors, cancellationToken);
        var notes = await ResolveAuthorsAsync(detail.Notes, authors, cancellationToken);
        return new BoardCardResponse(
            summary.Id, summary.Key, summary.ColumnId, summary.Position, summary.Title, summary.Description,
            summary.Assignee, summary.Priority, summary.Points, summary.Tags, summary.Blocked, summary.CommentCount,
            summary.ActiveSessionId, summary.ActiveTabId, summary.CreatedAt, summary.UpdatedAt,
            comments,
            detail.Commits.Select(ToDto).ToList(),
            detail.Sessions.Select(s => ToDto(s, live)).ToList(),
            detail.Attachments.Select(ToDto).ToList(),
            detail.Card.DescriptionRevision, detail.Card.BaseLlmOptions, detail.Card.DescriptionChanged,
            notes, summary.Type, summary.BoardId);
    }

    private async Task<List<BoardCommentDto>> ResolveAuthorsAsync(IReadOnlyList<BoardCommentRecord> rows, Dictionary<string, BoardAuthor?> authors, CancellationToken cancellationToken)
    {
        var result = new List<BoardCommentDto>(rows.Count);
        foreach (var row in rows)
        {
            var author = row.Author;
            if (author.Kind == BoardAuthor.AgentKind && BoardAuthor.IsGenericAgentLabel(author.Label)
                && !string.IsNullOrWhiteSpace(author.SessionId))
            {
                if (!authors.TryGetValue(author.SessionId, out var resolved))
                {
                    resolved = await store.FindSessionAuthorAsync(author.SessionId, cancellationToken);
                    authors[author.SessionId] = resolved;
                }
                author = resolved ?? author;
            }
            result.Add(ToDto(row with { Author = author }));
        }
        return result;
    }

    internal static BoardCardSummaryResponse ToSummary(BoardCardRecord card, string? activeSessionId, string? activeTabId) => new(
        card.Id, card.Key, card.ColumnId, card.Position, card.Title, card.Description, card.Assignee, card.Priority,
        card.Points, card.Tags.ToList(), card.Blocked, card.CommentCount, activeSessionId, activeTabId, card.CreatedUtc, card.UpdatedUtc,
        card.DescriptionRevision, card.BaseLlmOptions, card.Type, card.BoardId);

    internal static BoardColumnResponse ToDto(BoardColumnRecord column) =>
        new(column.Id, column.Name, column.WipLimit, column.Position, column.Color, column.BoardId);

    internal static BoardSummaryResponse ToDto(BoardRecord board, IReadOnlyList<BoardColumnRecord> columns, IReadOnlyDictionary<string, int> counts) =>
        new(board.Id, board.Name, board.Position, board.CreatedUtc,
            counts.TryGetValue(board.Id, out var count) ? count : 0,
            columns.Where(c => c.BoardId == board.Id).OrderBy(c => c.Position).Select(ToDto).ToList());

    internal static BoardCommentDto ToDto(BoardCommentRecord comment) =>
        new(comment.Id, new BoardAuthorDto(comment.Author.Kind, comment.Author.Label, comment.Author.Cli, comment.Author.SessionId), comment.Body, comment.CreatedUtc);

    internal static BoardAttachmentDto ToDto(BoardAttachmentRecord attachment) =>
        new(attachment.Id, attachment.Name, attachment.DataUrl, attachment.MimeType, attachment.Bytes, attachment.CreatedUtc);

    internal static BoardCommitDto ToDto(BoardCommitRecord commit) =>
        new(commit.Sha, commit.ShortSha, commit.Author, commit.Message, commit.CommittedUtc, commit.LinkedUtc);

    internal static BoardSessionDto ToDto(BoardSessionRecord session, IReadOnlyDictionary<string, string> live)
    {
        var active = live.TryGetValue(session.SessionId, out var liveTab);
        return new BoardSessionDto(session.SessionId, active ? liveTab : session.TabId, session.DisplayName,
            session.Cli, session.Selection, session.Origin, session.CreatedUtc, active);
    }

    // ------------------------------------------------------------------ normalisation

    /// <summary>
    /// Terminal sessions are <c>Guid.NewGuid().ToString()</c> (D format). Accept that or the
    /// 32-hex N form the Sessions form also recognises; reject everything else so a payload
    /// cannot be stored and later interpolated into the replay UI.
    /// </summary>
    internal static string NormalizeSessionId(string? sessionId)
    {
        var id = sessionId?.Trim() ?? string.Empty;
        if (id.Length == 0)
            throw new BoardValidationException("A session id is required.");
        if (id.Length > MaxSessionIdLength
            || (!Guid.TryParseExact(id, "D", out var guid) && !Guid.TryParseExact(id, "N", out guid))
            || guid == Guid.Empty)
            throw new BoardValidationException("That does not look like a session id.");
        return guid.ToString("D");
    }

    /// <summary>
    /// Short shas from the UI must resolve to exactly one linked commit — the same rule the
    /// snapshot reader uses — so an ambiguous prefix cannot delete every match.
    /// </summary>
    private async Task<string?> ResolveUniqueLinkedShaAsync(string projectPath, string cardId, string sha, CancellationToken cancellationToken)
    {
        var normalized = BoardCommitService.NormalizeSha(sha);
        var linked = await store.GetCommitsAsync(projectPath, cardId, cancellationToken);
        var matches = linked.Where(c => c.Sha.StartsWith(normalized, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
            throw new BoardValidationException("That short sha matches more than one linked commit. Use the full sha.");
        return matches[0].Sha;
    }

    internal static string? NormalizeTitle(string? value)
    {
        var title = value?.Trim();
        if (string.IsNullOrEmpty(title))
            return null;
        if (title.Length > MaxTitleLength)
            throw new BoardValidationException($"Title is too long (max {MaxTitleLength} characters).");
        return title;
    }

    internal static string NormalizeDescription(string? value)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length > MaxDescriptionLength)
            throw new BoardValidationException($"Description is too long (max {MaxDescriptionLength} characters).");
        return text;
    }

    internal static string? NormalizePriority(string? value)
    {
        var priority = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(priority) ? null
            : BoardPriorities.IsValid(priority) ? priority
            : throw new BoardValidationException($"Priority must be one of: {string.Join(", ", BoardPriorities.All)}.");
    }

    internal static string? NormalizeCardType(string? value)
    {
        var type = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type))
            return null;
        type = type switch
        {
            "research" or "research spike" or "spike" => BoardCardTypes.ResearchSpike,
            "chore/tech debt" or "chore / tech debt" or "tech debt" => BoardCardTypes.Chore,
            _ => type
        };
        return BoardCardTypes.IsValid(type)
            ? type
            : throw new BoardValidationException($"Type must be one of: {string.Join(", ", BoardCardTypes.All)}.");
    }

    internal static bool IsClearValue(System.Text.Json.JsonElement element) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Null
        || (element.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrWhiteSpace(element.GetString()));

    internal static int? NormalizePoints(System.Text.Json.JsonElement element)
    {
        int parsed;
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
                return null;
            case System.Text.Json.JsonValueKind.Number:
                if (!element.TryGetInt32(out parsed))
                    throw new BoardValidationException("Points must be a whole number.");
                break;
            case System.Text.Json.JsonValueKind.String:
                var text = element.GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                    return null;
                if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                    throw new BoardValidationException("Points must be a whole number.");
                break;
            default:
                throw new BoardValidationException("Points must be a whole number.");
        }
        if (!AllowedPoints.Contains(parsed))
            throw new BoardValidationException($"Points must be one of: {string.Join(", ", AllowedPoints)}.");
        return parsed;
    }

    internal static string? NormalizeAssignee(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (!BoardSelection.TryParse(text, out var selection))
            throw new BoardValidationException("Assignee must be an LLM picker key like base:claude or env:7:codex.");
        return selection!.Key;
    }

    private static BaseLlmOptions? NormalizeBaseOptions(string? selection, BaseLlmOptions? options)
    {
        if (options is null || !BoardSelection.TryParse(selection, out var parsed) || parsed is null || parsed.IsEnvironment)
            return null;
        try { return BaseLlmOptionsBuilder.Normalize(parsed.Llm, options); }
        catch (ArgumentException ex) { throw new BoardValidationException(ex.Message); }
    }

    internal static IReadOnlyList<string>? NormalizeTags(List<string>? tags)
    {
        if (tags is null)
            return null;
        var cleaned = tags
            .Select(t => (t ?? string.Empty).Trim())
            .Where(t => t.Length > 0)
            .Select(t => t.Length > MaxTagLength ? t[..MaxTagLength] : t)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTags)
            .ToList();
        return cleaned;
    }

    /// <summary>Empty and whitespace mean "the default board"; anything else is passed through trimmed.</summary>
    internal static string? NormalizeBoardId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeBoardName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        if (name.Length > MaxBoardNameLength)
            throw new BoardValidationException($"Board name is too long (max {MaxBoardNameLength} characters).");
        return name;
    }

    private static string? NormalizeColumnName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        return name.Length > MaxColumnNameLength ? name[..MaxColumnNameLength] : name;
    }

    /// <summary>null, "" or 0 all mean "no WIP limit" — that is what the lane editor sends when the field is blank.</summary>
    private static bool IsNoLimit(System.Text.Json.JsonElement element) =>
        IsClearValue(element)
        || (element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt32(out var n) && n <= 0);

    /// <summary>Undefined → leave alone; otherwise a positive whole number.</summary>
    private static int? NormalizeWip(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Number:
                return element.TryGetInt32(out var number) && number > 0 ? number : null;
            case System.Text.Json.JsonValueKind.String:
                var text = element.GetString()?.Trim();
                if (string.IsNullOrEmpty(text))
                    return null;
                return int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                    ? parsed
                    : throw new BoardValidationException("WIP limit must be a whole number.");
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
                return null;
            default:
                throw new BoardValidationException("WIP limit must be a whole number.");
        }
    }

    private static string? NormalizeColor(string? value)
    {
        var color = value?.Trim();
        if (string.IsNullOrEmpty(color))
            return null;
        return HexColorPattern().IsMatch(color) ? color.ToLowerInvariant() : null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorPattern();

    [GeneratedRegex(@"^data:image/(?<type>png|jpeg|jpg|gif|webp);base64,[A-Za-z0-9+/=\s]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageDataUrlPattern();
}
