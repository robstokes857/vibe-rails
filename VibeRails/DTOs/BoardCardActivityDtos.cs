namespace VibeRails.DTOs;

public sealed record BoardCardActivityRequest(string? BoardId, List<string>? CardIds);
public sealed record BoardCardActivityResponse(string Id, string? ActiveSessionId, string? ActiveTabId, bool HasActiveAutomation);
public sealed record BoardCardActivityListResponse(List<BoardCardActivityResponse> Cards);
