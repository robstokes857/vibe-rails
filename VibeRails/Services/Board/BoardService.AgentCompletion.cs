namespace VibeRails.Services.Board;

public partial interface IBoardService
{
    /// <summary>Bounded agent polling context with confirmed root-local liveness when available.</summary>
    Task<IReadOnlyList<BoardAgentSessionStatus>> GetAgentSessionsAsync(string projectPath, string cardId,
        string? sessionId = null, CancellationToken cancellationToken = default);
    /// <summary>Records a linked agent's final report without stopping its process or completing its workflow.</summary>
    Task<BoardAgentCompletion?> CompleteAgentAsync(string projectPath, string cardId, string sessionId,
        string outcome, string summary, CancellationToken cancellationToken = default);
}

public sealed partial class BoardService
{
    public async Task<IReadOnlyList<BoardAgentSessionStatus>> GetAgentSessionsAsync(string projectPath, string cardId,
        string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var sessions = await store.GetAgentSessionsAsync(projectPath, cardId,
            sessionId is null ? null : NormalizeSessionId(sessionId), cancellationToken);
        var live = await liveSessions.GetLiveSessionsAsync(cancellationToken);
        return sessions.Select(s => s with { Active = live.ContainsKey(s.Session.SessionId) }).ToList();
    }

    public async Task<BoardAgentCompletion?> CompleteAgentAsync(string projectPath, string cardId, string sessionId,
        string outcome, string summary, CancellationToken cancellationToken = default)
    {
        var id = NormalizeSessionId(sessionId);
        var result = outcome.Trim().ToLowerInvariant();
        if (result is not ("succeeded" or "failed" or "cancelled"))
            throw new BoardValidationException("Outcome must be succeeded, failed or cancelled.");
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 4000)
            throw new BoardValidationException("A completion summary of 1–4,000 characters is required.");
        return await store.CompleteAgentAsync(projectPath, cardId, id, result, summary.Trim(), cancellationToken);
    }
}
