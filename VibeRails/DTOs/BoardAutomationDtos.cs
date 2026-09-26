namespace VibeRails.DTOs;

public sealed record RunBoardCardAutomationRequest(long JobId);
public sealed record BoardCardAutomationRunResponse(string Id, string Name, JobRunStatus Status,
    DateTime QueuedAt, string? ErrorMessage);
public sealed record BoardCardAutomationsResponse(IReadOnlyList<BoardAutomationOption> Jobs,
    IReadOnlyList<BoardCardAutomationRunResponse> Runs);
