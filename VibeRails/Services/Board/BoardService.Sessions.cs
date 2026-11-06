namespace VibeRails.Services.Board;

public sealed partial class BoardService
{
    public async Task<DTOs.BoardSessionDto?> AttachSessionAsync(string projectPath, string idOrKey,
        string sessionId, string? tabId, CancellationToken cancellationToken = default, bool work = false)
    {
        var id = NormalizeSessionId(sessionId);
        var card = await store.FindCardAsync(projectPath, idOrKey, cancellationToken);
        if (card is null)
            return null;

        var links = (await store.GetSessionsForProjectAsync(projectPath, cancellationToken))
            .Where(s => s.SessionId == id).ToList();
        var existing = links.FirstOrDefault(s => s.CardId == card.Id);
        if (existing is not null)
            return await DeclareWorkAsync(existing);

        var source = links.FirstOrDefault();
        var author = await store.FindSessionAuthorAsync(id, cancellationToken);
        // A manager attaching to another Agent card (the board manager taking on a card that joined its
        // scope, VIBE-204) stays that card's manager, so the card's Go to manager reaches its tab.
        var origin = source?.Origin == BoardSessionRecord.ChatWorkOrigin ? "chat"
            : source?.Origin is "chat" or "planning" or "code_review" ? source.Origin
            : source?.Origin == BoardSessionRecord.ManagerOrigin && card.WorkflowMode == BoardWorkflowModes.Agent ? BoardSessionRecord.ManagerOrigin
            : BoardSessionRecord.McpOrigin;
        try
        {
            var attached = await LinkSessionAsync(projectPath, card.Id, id, source?.TabId ?? tabId,
                source?.Selection ?? string.Empty, source?.Cli ?? author?.Cli ?? string.Empty,
                author?.Label ?? source?.DisplayName ?? "Agent session", origin, cancellationToken);
            return attached is null ? null : await DeclareWorkAsync(new(id, card.Id, attached.TabId,
                attached.Selection, attached.Cli, attached.DisplayName, attached.Origin, attached.CreatedAt));
        }
        catch (BoardConflictException)
        {
            // A concurrent retry can have attached the same pair. Only that conflict is success.
            var attached = (await store.GetSessionsForProjectAsync(projectPath, cancellationToken))
                .FirstOrDefault(s => s.CardId == card.Id && s.SessionId == id);
            if (attached is not null)
                return await DeclareWorkAsync(attached);
            throw;
        }

        async Task<DTOs.BoardSessionDto> DeclareWorkAsync(BoardSessionRecord record)
        {
            if (work && record.Origin == "chat"
                && await store.AuthorizeDiscussionWorkAsync(projectPath, card.Id, id, cancellationToken))
                record = record with { Origin = BoardSessionRecord.ChatWorkOrigin };
            // Re-read after a concurrent declaration so retries never return a stale chat purpose.
            if (record.Origin == "chat")
                record = (await store.GetSessionsForProjectAsync(projectPath, cancellationToken))
                    .FirstOrDefault(s => s.CardId == card.Id && s.SessionId == id) ?? record;
            return (await SessionDtosAsync(projectPath, [record], await liveSessions.GetLiveSessionsAsync(cancellationToken), cancellationToken))[0];
        }
    }
}
