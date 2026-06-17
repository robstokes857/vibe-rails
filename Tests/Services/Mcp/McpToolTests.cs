using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// Pure-logic unit tests for the stateless MCP tool methods. These don't stand up a host —
/// they call the static tool methods directly. End-to-end protocol behaviour (including the
/// DI-injected <see cref="SessionSearchTool"/>) is covered by <see cref="McpServerHttpTests"/>.
/// </summary>
public class McpToolTests
{
    [Fact]
    public void Echo_PrefixesMessage()
    {
        Assert.Equal("Echo: hello", EchoTool.Echo("hello"));
    }

    [Fact]
    public void CheckRules_PassesCleanContent()
    {
        Assert.StartsWith("PASS", RulesTool.CheckRules("a perfectly ordinary sentence"));
    }

    [Fact]
    public void CheckRules_RejectsEmpty()
    {
        Assert.StartsWith("FAIL", RulesTool.CheckRules("   "));
    }

    [Theory]
    [InlineData("password = hunter2longvalue")]
    [InlineData("api_key: abc123def456")]
    [InlineData("SECRET=topsecretvalue")]
    [InlineData("token = ghp_aBcDeFgHiJkLmNoP")]
    public void CheckRules_DetectsSecrets(string content)
    {
        var result = RulesTool.CheckRules(content);
        Assert.StartsWith("FAIL", result);
        Assert.Contains("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckRules_RejectsUnresolvedTodo()
    {
        Assert.StartsWith("FAIL", RulesTool.CheckRules("ship it // TODO: write the tests"));
    }

    [Fact]
    public void CheckRules_RejectsOverlongContent()
    {
        var huge = new string('x', 2001);
        Assert.StartsWith("FAIL", RulesTool.CheckRules(huge));
    }
}
