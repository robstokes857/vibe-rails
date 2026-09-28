using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.Board;

namespace VibeRails.Services;

public class ChatHistoryService(
    IRepository repository,
    ISessionTranscriptService sessionTranscriptService,
    ISummaryService summaryService,
    ISessionDataExportService sessionDataExportService,
    IBoardStore boardStore) : IChatHistoryService
{
    public async Task<ChatHistoryResponse> GetHistoryAsync(int page, int pageSize, string? preferredWorkingDirectory, string? sortBy, string? sortDirection, CancellationToken cancellationToken)
    {
        var offset = (page - 1) * pageSize;
        var items = await repository.GetChatHistoryPageAsync(pageSize, offset, preferredWorkingDirectory, sortBy, sortDirection, cancellationToken);
        await AddBoardCardsAsync(items, cancellationToken);
        return new ChatHistoryResponse(items, page, pageSize);
    }

    public async Task<ChatHistoryItem?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var item = await repository.GetChatHistoryItemAsync(sessionId, cancellationToken);
        if (item is null) return null;
        var items = new List<ChatHistoryItem> { item };
        await AddBoardCardsAsync(items, cancellationToken);
        return items[0];
    }

    private async Task AddBoardCardsAsync(List<ChatHistoryItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        var cards = (await boardStore.GetSessionCardsAsync(items.Select(item => item.Id).ToArray(), cancellationToken))
            .ToLookup(card => card.SessionId, StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            // Do not rewrite stored names: legacy automatic previews and explicit renames
            // remain intact. The client uses card labels as the automatic display default.
            items[i] = items[i] with
            {
                BoardCards = cards[items[i].Id].Select(card => new ChatHistoryCard(card.CardId, card.Key, card.Title)).ToArray()
            };
        }
    }

    public Task<bool> RenameSessionAsync(string sessionId, string sessionDisplayName, CancellationToken cancellationToken)
        => repository.UpdateChatHistorySessionNameAsync(sessionId, sessionDisplayName, cancellationToken);

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteChatHistorySessionAsync(sessionId, cancellationToken);
        if (!deleted)
            return false;

        await repository.DeleteChatSummaryBySessionAsync(sessionId, cancellationToken);

        // The session rows are gone, so the drain job can never select this id again and the
        // exporter's own post-upload cleanup is now unreachable for it. Without this, a spool
        // retained from an earlier failed attempt would outlive the data the user just erased.
        sessionDataExportService.DeleteSpool(sessionId);
        return true;
    }

    public async Task<ChatSummaryResponse> GetSummaryAsync(string sessionId, bool regenerate, CancellationToken cancellationToken)
    {
        var session = await repository.GetSessionOutputAsync(sessionId, cancellationToken);
        if (session is null)
            throw new KeyNotFoundException($"Session not found: {sessionId}");

        var transcript = await sessionTranscriptService.GetOrBuildAsync(sessionId, cancellationToken, forceRebuild: regenerate);

        if (!regenerate)
        {
            var summaries = await repository.GetChatSummariesBySessionAsync(sessionId, cancellationToken);
            var cached = summaries?.OrderByDescending(s => s.Date).FirstOrDefault();
            if (cached != null)
                return new ChatSummaryResponse(cached.SummaryText, transcript);
        }

        if (string.IsNullOrWhiteSpace(transcript))
            return new ChatSummaryResponse("No conversation output found for this session.", transcript);

        string summary = await summaryService.GetSummaryAsync(transcript, cancellationToken);

        await repository.SaveChatSummaryAsync(new ChatSummary
        {
            SessionId = sessionId,
            SummaryText = summary,
            Date = DateTime.UtcNow
        }, cancellationToken);

        return new ChatSummaryResponse(summary, transcript);
    }
}
