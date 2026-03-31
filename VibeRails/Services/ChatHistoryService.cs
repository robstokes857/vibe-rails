using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services;

public class ChatHistoryService(
    IRepository repository,
    ISessionTranscriptService sessionTranscriptService,
    ISummaryService summaryService) : IChatHistoryService
{

    public async Task<ChatHistoryResponse> GetHistoryAsync(int page, int pageSize, string? preferredWorkingDirectory, string? sortBy, string? sortDirection, CancellationToken cancellationToken)
    {
        var offset = (page - 1) * pageSize;
        var items = await repository.GetChatHistoryPageAsync(pageSize, offset, preferredWorkingDirectory, sortBy, sortDirection, cancellationToken);
        return new ChatHistoryResponse(items, page, pageSize);
    }

    public Task<ChatHistoryItem?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
        => repository.GetChatHistoryItemAsync(sessionId, cancellationToken);

    public Task<bool> RenameSessionAsync(string sessionId, string sessionDisplayName, CancellationToken cancellationToken)
        => repository.UpdateChatHistorySessionNameAsync(sessionId, sessionDisplayName, cancellationToken);

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteChatHistorySessionAsync(sessionId, cancellationToken);
        if (!deleted)
            return false;

        await repository.DeleteChatSummaryBySessionAsync(sessionId, cancellationToken);
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
