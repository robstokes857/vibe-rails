namespace VibeRails.DTOs;

public sealed record SkipBoardCardAutomationRequest(long JobId, string EventKey);

public sealed record BoardCardActivityRequest(string? BoardId, List<string>? CardIds);
public sealed record BoardCardActivityResponse(string Id, string? ActiveSessionId, string? ActiveTabId, bool HasActiveAutomation,
    bool HasWaitingAutomation = false)
{
    /// <summary>A desktop MCP client has recently marked this card as active.</summary>
    public bool HasActiveDesktopAgent { get; init; }

    /// <summary>The desktop app recorded on the card, including after its activity ends.</summary>
    public string? DesktopAppName { get; init; }

    /// <summary>
    /// Moves when anything the card editor's refresh shows for this card may have changed: the Board
    /// change stamp, the card's live sessions and tabs, and its running, waiting and desktop state.
    /// Null when the store cannot tell (VIBE-322).
    /// </summary>
    public string? Stamp { get; init; }
}
public sealed record BoardCardActivityListResponse(List<BoardCardActivityResponse> Cards)
{
    public IReadOnlyList<string> ActiveAutomationColumnIds { get; init; } = [];

    /// <summary>
    /// The Board change stamp, read before anything else in this response (VIBE-322). While it is
    /// unchanged, the boards, lanes and card pages a client read after the earlier value are current.
    /// Null means unknown: reread them.
    /// </summary>
    public string? ChangeToken { get; init; }
}
