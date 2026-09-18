namespace VibeRails.Services.Terminal;

/// <summary>
/// Launch-local grants for a session started from a board card: every tool the VibeRails MCP
/// server (<c>vb mcp</c>) offers is pre-approved, so the agent never stops to ask for a board,
/// search, token-saver or Python-script tool (VB-11). Nothing outside that one server is granted,
/// and ordinary launches still add none of this (CommandService only applies it on
/// <c>AuthorizeBoardTools</c>).
///
/// <see cref="ToolNames"/> is the compile-time tool list, used where a CLI wants tools named one
/// by one (Copilot, OpenCode) and repeated alongside the server-wide rule elsewhere so a CLI that
/// ignores the wildcard form still honours the explicit one. Python scripts exposed as MCP tools
/// have user-chosen names, so only the server-wide rules (Claude, Codex, Grok) cover them.
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
        "get_board_card_history",
        "get_board_notes",
        "read_board_attachment",
        "create_board_card",
        "update_board_card",
        "move_board_card",
        "add_board_comment",
        "append_board_note",
        "add_board_attachment",
        "link_board_commit",
        // RulesTool, SessionSearchTool, TokenSaverTool, PythonScriptTool
        "validate_vca",
        "search_history",
        "get_token_saver_status",
        "pause_token_saver",
        "resume_token_saver",
        "python_script_signing_help"
    ]);

    public static string[] AppendArguments(LLM llm, string[] arguments)
    {
        var grants = new List<string>();
        if (llm == LLM.Codex)
        {
            // Server-wide default first (covers Python script tools), then the explicit per-tool
            // approvals that were verified before the server-wide key existed.
            // https://developers.openai.com/codex/mcp/#other-configuration-options
            grants.Add("--config");
            grants.Add($"mcp_servers.{ServerName}.default_tools_approval_mode=\"approve\"");
            foreach (var tool in ToolNames)
            {
                grants.Add("--config");
                grants.Add($"mcp_servers.{ServerName}.tools.{tool}.approval_mode=\"approve\"");
            }
        }
        else if (llm == LLM.Claude)
        {
            // "mcp__<server>" matches every tool the server provides; the explicit names follow
            // for a build that only honours full tool names. --allowedTools is variadic; the
            // delimiter below keeps the positional task prompt from being swallowed as a rule.
            grants.Add("--allowedTools=" + string.Join(',',
                new[] { $"mcp__{ServerName}" }.Concat(ToolNames.Select(tool => $"mcp__{ServerName}__{tool}"))));
        }
        else if (llm == LLM.Copilot)
        {
            // Copilot's launch grants are additive; configured denies still take precedence.
            foreach (var tool in ToolNames)
                grants.Add($"--allow-tool={ServerName}({tool})");
        }
        else if (llm == LLM.Grok46)
        {
            // MCPTool(server__*) is the documented glob; the explicit names ride along for a
            // build that only matches full tool names.
            grants.Add($"--allow=MCPTool({ServerName}__*)");
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
