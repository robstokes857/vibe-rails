namespace VibeRails.DTOs;

public sealed record SkipBoardCardAutomationRequest(long JobId, string EventKey);

public sealed record BoardCardActivityRequest(string? BoardId, List<string>? CardIds);
public sealed record BoardCardActivityResponse(string Id, string? ActiveSessionId, string? ActiveTabId, bool HasActiveAutomation,
    bool HasWaitingAutomation = false);
public sealed record BoardCardActivityListResponse(List<BoardCardActivityResponse> Cards)
{
    public IReadOnlyList<string> ActiveAutomationColumnIds { get; init; } = [];
}
