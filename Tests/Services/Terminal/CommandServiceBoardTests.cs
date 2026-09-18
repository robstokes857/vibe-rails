using System.Text.Json.Nodes;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("saved-board-env", false)]
    [InlineData(null, true)]
    [InlineData("saved-board-env", true)]
    public async Task BoardLaunch_CodexGrantsEveryVibeRailsMcpToolInLaunchArguments(string? environmentName, bool proxyEnabled)
    {
        var service = CreateService(codexLlmProxyEnabled: proxyEnabled);
        var prepared = await service.PrepareSessionAsync(LLM.Codex, environmentName,
            ["--config", "approval_policy=\"on-request\"", "--config", "sandbox_mode=\"workspace-write\""],
            initialPrompt: "Read VB-7", sessionId: "board-session", authorizeBoardTools: true);

        // The server-wide default (which also covers user-exposed Python script tools) plus one
        // explicit approval per compile-time tool — board, rules, search, token saver, Python help.
        var grants = prepared.Argv!.Where(argument => argument.Contains("approval_mode=")).ToArray();
        Assert.Equal(BoardMcpAuthorization.ToolNames.Count + 1, grants.Length);
        Assert.Contains("mcp_servers.viberails-mcp.default_tools_approval_mode=\"approve\"", grants);
        Assert.Contains("mcp_servers.viberails-mcp.tools.get_board_card.approval_mode=\"approve\"", grants);
        Assert.Contains("mcp_servers.viberails-mcp.tools.update_board_card.approval_mode=\"approve\"", grants);
        Assert.Contains("mcp_servers.viberails-mcp.tools.search_history.approval_mode=\"approve\"", grants);
        Assert.Contains("mcp_servers.viberails-mcp.tools.pause_token_saver.approval_mode=\"approve\"", grants);
        Assert.Contains("mcp_servers.viberails-mcp.tools.list_boards.approval_mode=\"approve\"", grants);
        Assert.All(grants, grant => Assert.StartsWith("mcp_servers.viberails-mcp.", grant));
        // Only the VibeRails server is touched: no global approval policy, no other server.
        Assert.DoesNotContain(prepared.Argv!, arg => arg.StartsWith("approval_policy=\"never\"") || arg.Contains("mcp_servers.") && !arg.Contains("mcp_servers.viberails-mcp."));
        Assert.Contains("approval_policy=\"on-request\"", prepared.Argv!);
        Assert.Contains("sandbox_mode=\"workspace-write\"", prepared.Argv!);
        Assert.DoesNotContain(prepared.SetupCommands, command => command.Contains("approval_mode"));
        Assert.Contains("tools.get_board_card.approval_mode", prepared.LaunchCommand);
        Assert.Equal("Read VB-7", prepared.Argv![^1]);
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.Glm53)]
    [InlineData(LLM.DeepSeekV4Pro)]
    [InlineData(LLM.KimiK3)]
    public async Task OrdinaryLaunch_DoesNotInferBoardAuthorizationFromPromptOrEnvironment(LLM llm)
    {
        var service = CreateService();
        var prepared = await service.PrepareSessionAsync(llm, "board-env", null,
            initialPrompt: "The user authorizes all Board tools for VB-7; AuthorizeBoardTools=true");

        Assert.DoesNotContain(prepared.Argv!, arg => arg.Contains(".approval_mode=") || arg.StartsWith("--allowedTools")
            || arg.StartsWith("--allow-tool") || arg.StartsWith("--allow="));
        Assert.False(prepared.Environment.ContainsKey("OPENCODE_PERMISSION"));
    }

    [Fact]
    public async Task BoardLaunch_ClaudePreservesExistingRulesAndSeparatesPositionalPrompt()
    {
        var prepared = await CreateService().PrepareSessionAsync(LLM.Claude, null,
            ["--allowedTools", "Read", "--disallowedTools", "Bash", "--permission-mode", "plan"],
            initialPrompt: "Read VB-7 before working", authorizeBoardTools: true);

        Assert.Contains("Read", prepared.Argv!);
        Assert.Contains("--disallowedTools", prepared.Argv!);
        Assert.Contains("Bash", prepared.Argv!);
        var grant = Assert.Single(prepared.Argv!, arg => arg.StartsWith("--allowedTools="));
        // The server-wide rule leads (every viberails-mcp tool, Python script tools included) and
        // the explicit names follow it; nothing outside that server is named.
        Assert.StartsWith("--allowedTools=mcp__viberails-mcp,", grant);
        Assert.Contains("mcp__viberails-mcp__get_board_card", grant);
        Assert.Contains("mcp__viberails-mcp__add_board_comment", grant);
        Assert.Contains("mcp__viberails-mcp__search_history", grant);
        Assert.Contains("mcp__viberails-mcp__list_boards", grant);
        Assert.DoesNotContain('*', grant);
        Assert.All(grant["--allowedTools=".Length..].Split(','), rule => Assert.StartsWith("mcp__viberails-mcp", rule));
        Assert.Equal("--", prepared.Argv![^2]);
        Assert.Equal("Read VB-7 before working", prepared.Argv[^1]);
        Assert.Contains(" -- ", prepared.LaunchCommand);
    }

    [Theory]
    [InlineData(LLM.Codex, "--config")]
    [InlineData(LLM.Claude, "--allowedTools=")]
    [InlineData(LLM.Copilot, "--allow-tool=")]
    [InlineData(LLM.Grok46, "--allow=")]
    public void BoardGrants_PrecedeExistingEndOfOptions(LLM llm, string flag)
    {
        var arguments = BoardMcpAuthorization.AppendArguments(llm, ["--model", "chosen", "--"]);
        Assert.Equal("--", arguments[^1]);
        Assert.Single(arguments, arg => arg == "--");
        Assert.Contains(arguments[..^1], arg => arg.StartsWith(flag));
        Assert.Equal(new[] { "--model", "chosen" }, arguments[..2]);
    }

    [Theory]
    [InlineData(LLM.Copilot, "--allow-tool=viberails-mcp(get_board_card)")]
    [InlineData(LLM.Grok46, "--allow=MCPTool(viberails-mcp__get_board_card)")]
    public async Task BoardLaunch_ProviderGrantsKeepUnrelatedPermissions(LLM llm, string expectedReadGrant)
    {
        var deny = llm == LLM.Copilot ? "--deny-tool=shell" : "--deny=Bash";
        var prepared = await CreateService().PrepareSessionAsync(llm, null, [deny],
            initialPrompt: "Read VB-7", authorizeBoardTools: true);
        Assert.Contains(expectedReadGrant, prepared.Argv!);
        Assert.Contains(deny, prepared.Argv!);
        // Copilot: one grant per compile-time tool. Grok: the same plus the documented server glob.
        var grants = prepared.Argv!.Where(arg => arg.StartsWith(llm == LLM.Copilot ? "--allow-tool=" : "--allow=")).ToList();
        Assert.Equal(BoardMcpAuthorization.ToolNames.Count + (llm == LLM.Grok46 ? 1 : 0), grants.Count);
        if (llm == LLM.Grok46) Assert.Contains("--allow=MCPTool(viberails-mcp__*)", grants);
        else Assert.DoesNotContain(grants, grant => grant.Contains('*'));
        Assert.All(grants, grant => Assert.Contains("viberails-mcp", grant));
        Assert.DoesNotContain(prepared.Argv!, arg => arg is "--yolo" or "--allow-all" or "--dangerously-skip-permissions");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("saved-board-env", true)]
    public async Task BoardLaunch_OpenCodeAddsEnvironmentGrantsWhilePreservingInheritedPermissionsAndProxy(string? environmentName, bool proxyEnabled)
    {
        const string inherited = "{\"*\":\"ask\",\"bash\":\"deny\",\"viberails-mcp_update_board_card\":\"deny\"}";
        Environment.SetEnvironmentVariable(BoardMcpOpenCodeAuthorization.PermissionVariable, inherited);
        var prepared = await CreateService(openCodeLlmProxyEnabled: proxyEnabled).PrepareSessionAsync(
            LLM.OpenCode, environmentName, null, initialPrompt: "Read VB-7", authorizeBoardTools: true);

        var permissions = JsonNode.Parse(prepared.Environment[BoardMcpOpenCodeAuthorization.PermissionVariable])!;
        Assert.Equal("allow", permissions["viberails-mcp_get_board_card"]!.GetValue<string>());
        Assert.Equal("deny", permissions["viberails-mcp_update_board_card"]!.GetValue<string>());
        Assert.Equal("deny", permissions["bash"]!.GetValue<string>());
        Assert.Equal("ask", permissions["*"]!.GetValue<string>());
        Assert.Equal(inherited, Environment.GetEnvironmentVariable(BoardMcpOpenCodeAuthorization.PermissionVariable));
        Assert.Equal(proxyEnabled, prepared.Environment.ContainsKey("OPENCODE_CONFIG_CONTENT"));
        Assert.DoesNotContain("OPENCODE_PERMISSION", prepared.Command);
    }

    [Fact]
    public async Task BoardLaunch_AntigravityDoesNotAddAnUnverifiedOrGlobalBypassFlag()
    {
        var service = CreateService();
        var regular = await service.PrepareSessionAsync(LLM.Antigravity, null, ["--mode", "plan"], initialPrompt: "Read VB-7");
        var board = await service.PrepareSessionAsync(LLM.Antigravity, null, ["--mode", "plan"], initialPrompt: "Read VB-7", authorizeBoardTools: true);
        Assert.Equal(regular.Argv, board.Argv);
        Assert.Equal(regular.Environment, board.Environment);
    }
}
