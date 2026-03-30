using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services;

public interface ISessionResumeService
{
    /// <summary>
    /// Given a previous session ID, builds its transcript, summarises it,
    /// and returns the summary text ready for injection into a CLI prompt.
    /// Throws KeyNotFoundException if the session does not exist.
    /// Returns empty string if the session has no usable transcript.
    /// </summary>
    Task<string> GetResumeSummaryAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets ParentSessionId on the child session to link it to the session it was resumed from.
    /// </summary>
    Task LinkParentSessionAsync(string childSessionId, string parentSessionId, string childCli, CancellationToken cancellationToken);
}

public class SessionResumeService(
    IRepository repository,
    ISessionTranscriptService transcriptService,
    ISummaryService summaryService) : ISessionResumeService
{
    public async Task<string> GetResumeSummaryAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await repository.GetSessionWithLogsAsync(sessionId, cancellationToken);
        if (session is null)
            throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        // Check DB cache first
        var cached = (await repository.GetChatSummariesBySessionAsync(sessionId, cancellationToken))
            ?.OrderByDescending(s => s.Date)
            .FirstOrDefault();
        if (cached != null && !string.IsNullOrWhiteSpace(cached.SummaryText))
            return cached.SummaryText;

        var transcript = await transcriptService.GetOrBuildAsync(sessionId, cancellationToken);

        if (string.IsNullOrWhiteSpace(transcript))
            return "";

        var summary = await summaryService.GetSummaryAsync(transcript, cancellationToken);

        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("Summary service returned an empty summary.");

        if (summary.Length > 6000)
            throw new InvalidOperationException($"Summary is too long ({summary.Length} chars). Max allowed is 6000.");

        // Save to DB cache
        await repository.SaveChatSummaryAsync(new ChatSummary
        {
            SessionId = sessionId,
            SummaryText = summary,
            Date = DateTime.UtcNow
        }, cancellationToken);

        return summary;
    }

    public async Task LinkParentSessionAsync(string childSessionId, string parentSessionId, string childCli, CancellationToken cancellationToken)
    {
        var (parentCli, parentDisplayName) = await repository.GetSessionDisplayInfoAsync(parentSessionId);

        var parentLabel = !string.IsNullOrWhiteSpace(parentDisplayName)
            ? parentDisplayName
            : parentSessionId[..Math.Min(8, parentSessionId.Length)];
        var childDisplayName = $"Chat from {parentCli ?? "Unknown"} -> {childCli} {parentLabel}";

        await repository.SetParentSessionIdAsync(childSessionId, parentSessionId);
        await repository.SetSessionDisplayNameAsync(childSessionId, childDisplayName);
    }

}
