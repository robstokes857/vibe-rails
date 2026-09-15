namespace VibeRails.Services.Terminal;

/// <summary>
/// Launch-local grants for the Board workflow. Keep this an explicit list: registering
/// another VibeRails MCP tool must never silently preauthorize it for board sessions.
/// </summary>
public static class BoardMcpAuthorization
{
    public const string ServerName = "viberails-mcp";

    public static IReadOnlyList<string> ToolNames { get; } = Array.AsReadOnly<string>(
    [
        "list_board_columns",
        "list_board_cards",
        "get_board_card",
        "read_board_attachment",
        "create_board_card",
        "update_board_card",
        "move_board_card",
        "add_board_comment",
        "link_board_commit"
    ]);

    public static string[] AppendArguments(LLM llm, string[] arguments)
    {
        var grants = new List<string>();
        if (llm == LLM.Codex)
        {
            // Explicit per-tool approval overrides the server's default prompt policy.
            // https://developers.openai.com/codex/mcp/#other-configuration-options
            foreach (var tool in ToolNames)
            {
                grants.Add("--config");
                grants.Add($"mcp_servers.{ServerName}.tools.{tool}.approval_mode=\"approve\"");
            }
        }
        else if (llm == LLM.Claude)
        {
            // --allowedTools is variadic; the delimiter below keeps the positional
            // task prompt from being swallowed as another permission rule.
            grants.Add("--allowedTools=" + string.Join(',', ToolNames.Select(tool => $"mcp__{ServerName}__{tool}")));
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

        var result = new List<string>(arguments);
        var delimiter = result.IndexOf("--");
        result.InsertRange(delimiter < 0 ? result.Count : delimiter, grants);
        if (llm == LLM.Claude && delimiter < 0)
            result.Add("--");
        return result.ToArray();
    }
}
