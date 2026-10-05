namespace VibeRails.DTOs;

/// <summary>A finalized agent recording; the parent supplies the owning tab ID after removal.</summary>
public sealed record AgentSessionClosedPayload(string SessionId, string? TabId = null)
{
    public const string Reason = "Terminal closed because the agent called end_agent_session";
}
