using System.Text.Json.Nodes;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class BoardMcpOpenCodeAuthorizationTests
{
    [Theory]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Glm52)]
    [InlineData(LLM.Glm53)]
    [InlineData(LLM.DeepSeekV4Pro)]
    [InlineData(LLM.KimiK3)]
    public void AppliesExactlyTheVibeRailsToolListWithoutChangingProxyConfig(LLM llm)
    {
        var environment = new Dictionary<string, string>
        {
            [BoardMcpOpenCodeAuthorization.PermissionVariable] = "{}",
            ["OPENCODE_CONFIG_CONTENT"] = "{\"provider\":{\"existing\":{}}}"
        };

        BoardMcpOpenCodeAuthorization.Apply(llm, environment);

        var permissions = JsonNode.Parse(environment[BoardMcpOpenCodeAuthorization.PermissionVariable])!.AsObject();
        Assert.Equal(BoardMcpAuthorization.ToolNames.Count, permissions.Count);
        foreach (var tool in BoardMcpAuthorization.ToolNames)
            Assert.Equal("allow", permissions["viberails-mcp_" + tool]!.GetValue<string>());
        Assert.Equal("{\"provider\":{\"existing\":{}}}", environment["OPENCODE_CONFIG_CONTENT"]);
    }

    [Theory]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Grok46)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.Antigravity)]
    [InlineData(LLM.Shell)]
    public void OtherProvidersRemainUntouched(LLM llm)
    {
        var environment = new Dictionary<string, string>();
        BoardMcpOpenCodeAuthorization.Apply(llm, environment);
        Assert.Empty(environment);
    }

    [Fact]
    public void ExplicitBoardAuthorizationOverridesAskWithoutChangingUnrelatedRules()
    {
        const string current = "{\"viberails-mcp_get_board_card\":\"ask\",\"*\":\"ask\",\"bash\":{\"git push*\":\"deny\",\"git status\":\"allow\"},\"other_server_*\":\"deny\"}";
        var result = JsonNode.Parse(BoardMcpOpenCodeAuthorization.MergePermissionRules(current))!.AsObject();

        Assert.Equal("ask", result["*"]!.GetValue<string>());
        Assert.Equal("deny", result["other_server_*"]!.GetValue<string>());
        Assert.Equal("deny", result["bash"]!["git push*"]!.GetValue<string>());
        Assert.Equal("allow", result["bash"]!["git status"]!.GetValue<string>());
        Assert.Equal("allow", result["viberails-mcp_get_board_card"]!.GetValue<string>());
        Assert.Equal(["*", "bash", "other_server_*"], result.Take(3).Select(rule => rule.Key));
    }

    [Fact]
    public void ExactAndWildcardBoardDeniesArePreserved()
    {
        const string current = "{\"viberails-mcp_update_*\":\"deny\",\"viberails-mcp_read_board_attachment\":{\"*\":\"deny\"},\"viberails-mcp_get_board_card\":\"deny\",\"*\":\"ask\"}";
        var result = JsonNode.Parse(BoardMcpOpenCodeAuthorization.MergePermissionRules(current))!.AsObject();

        Assert.False(result.ContainsKey("viberails-mcp_update_board_card"));
        Assert.Equal("deny", result["viberails-mcp_get_board_card"]!.GetValue<string>());
        Assert.Equal("deny", result["viberails-mcp_read_board_attachment"]!["*"]!.GetValue<string>());
        Assert.Equal("allow", result["viberails-mcp_list_board_cards"]!.GetValue<string>());
    }

    [Fact]
    public void BroadDenyPreventsEveryNewGrant()
    {
        const string current = "{\"*\":\"deny\",\"bash\":\"ask\"}";
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(current),
            JsonNode.Parse(BoardMcpOpenCodeAuthorization.MergePermissionRules(current))));
    }

    [Theory]
    [InlineData("VIBERAILS-MCP_GET_BOARD_CARD")]
    [InlineData("viberails-mcp_get_board_car?")]
    [InlineData("viberails-mcp_get_board_card *")]
    public void DenyMatchingFollowsOpenCodeCaseWildcardAndOptionalArgumentSemantics(string pattern)
    {
        var current = new JsonObject { [pattern] = "deny" }.ToJsonString();
        var result = JsonNode.Parse(BoardMcpOpenCodeAuthorization.MergePermissionRules(current))!.AsObject();
        Assert.False(result.ContainsKey("viberails-mcp_get_board_card"));
        Assert.Equal("deny", result[pattern]!.GetValue<string>());
        Assert.Equal("allow", result["viberails-mcp_list_board_cards"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("private-secret-invalid")]
    [InlineData("[]")]
    [InlineData("\"deny\"")]
    [InlineData("{\"bash\":true}")]
    [InlineData("{\"bash\":\"unknown\"}")]
    [InlineData("{\"bash\":{\"*\":{\"*\":\"allow\"}}}")]
    [InlineData("{\"bash\":\"deny\",\"bash\":\"allow\"}")]
    public void InvalidPermissionConfigFailsWithoutLeakingOrReplacingItsContent(string current)
    {
        var environment = new Dictionary<string, string> { [BoardMcpOpenCodeAuthorization.PermissionVariable] = current };
        var error = Assert.Throws<ArgumentException>(() => BoardMcpOpenCodeAuthorization.Apply(LLM.OpenCode, environment));
        Assert.DoesNotContain(current, error.Message);
        Assert.Equal(current, environment[BoardMcpOpenCodeAuthorization.PermissionVariable]);
    }
}
