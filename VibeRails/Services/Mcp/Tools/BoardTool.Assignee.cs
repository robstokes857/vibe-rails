namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    /// <summary>
    /// The assignee an agent's new card gets when the call names none (VIBE-185): the LLM of the
    /// VibeRails terminal that made it, exactly as the picker would name it. Desktop clients and
    /// agents VibeRails did not launch have no session to read, so their cards stay unassigned
    /// unless they pass <c>assignee</c> themselves.
    /// </summary>
    private async Task<string?> ResolveCreatorAssigneeAsync(Services.Board.BoardAuthor author, CancellationToken cancellationToken)
    {
        if (author.Kind != Services.Board.BoardAuthor.AgentKind || desktopActivity?.IsDesktop == true
            || author.SessionId is not { } sessionId)
            return null;
        var session = await store.FindSessionLlmAsync(sessionId, cancellationToken);
        if (session is null) return null;
        var llm = LlmParser.ParseValue(session.Cli);
        return llm is LLM.NotSet or LLM.Shell ? null : Services.Board.BoardSelection.Format(llm, session.EnvironmentId);
    }

    /// <summary>
    /// A bare provider name (<c>claude</c>, <c>Codex</c>) is shorthand for its base picker key, so an
    /// agent that never read list_board_agent_options can still name its provider. Anything else,
    /// including the empty string that means "unassigned", goes to the shared validation unchanged.
    /// </summary>
    internal static string? ExpandAssignee(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains(':')) return value;
        return Services.Board.BoardSelection.TryParse("base:" + text, out var selection) ? selection!.Key : value;
    }
}
