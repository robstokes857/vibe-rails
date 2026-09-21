namespace VibeRails.Services.Terminal;

/// <summary>
/// Launch-local grants for the explicit Board tool allowlist. Unrelated tools on the same
/// server retain their normal approval policy.
/// CommandService applies these grants only when <c>AuthorizeBoardTools</c> is set.
/// </summary>
public static class BoardMcpAuthorization
{
    public const string ServerName = "viberails-mcp";

    public static IReadOnlyList<string> ToolNames { get; } = Array.AsReadOnly<string>(
    [
        // BoardTool
        "list_boards",
        "list_board_columns",
        "list_board_cards",
        "get_board_card",
        "get_board_notes",
        "read_board_attachment",
        "create_board_card",
        "update_board_card",
        "move_board_card",
        "add_board_comment",
        "append_board_note",
        "add_board_attachment",
        "attach_board_session",
        "link_board_commit"
    ]);

    public static string[] AppendArguments(LLM llm, string[] arguments)
    {
        var grants = new List<string>();
        if (llm == LLM.Codex)
        {
            foreach (var tool in ToolNames)
            {
                grants.Add("--config");
                grants.Add($"mcp_servers.{ServerName}.tools.{tool}.approval_mode=\"approve\"");
            }
        }
        else if (llm == LLM.Claude)
        {
            // --allowedTools is variadic; the delimiter below keeps the positional task
            // prompt from being swallowed as a rule. Always name each tool explicitly.
            grants.Add("--allowedTools=" + string.Join(',',
                ToolNames.Select(tool => $"mcp__{ServerName}__{tool}")));
        }
        else if (llm == LLM.Copilot)
        {
            // Copilot's launch grants are additive; configured denies still take precedence.
            foreach (var tool in ToolNames)
                grants.Add($"--allow-tool={ServerName}({tool})");
        }
        else if (llm == LLM.Grok46)
        {
            foreach (var tool in ToolNames)
                grants.Add($"--allow=MCPTool({ServerName}__{tool})");
        }
        // Antigravity (agy) has no per-server or per-tool grant — only a global
        // --dangerously-skip-permissions — so a board launch adds nothing for it on purpose.

        var result = new List<string>(arguments);
        var delimiter = result.IndexOf("--");
        result.InsertRange(delimiter < 0 ? result.Count : delimiter, grants);
        if (llm == LLM.Claude && delimiter < 0)
            result.Add("--");
        return result.ToArray();
    }
}
