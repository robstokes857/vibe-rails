using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    [Theory]
    [InlineData(LLM.Claude)]
    [InlineData(LLM.Codex)]
    [InlineData(LLM.Grok46)]
    public async Task UntrustedSummaryIsAlwaysAPositionalArgument(LLM llm)
    {
        const string summary = "--config=untrusted --settings=hooks";
        var prepared = await CreateService().PrepareSessionAsync(llm, null, null, summary: summary);
        Assert.Equal("--", prepared.Argv![^2]);
        Assert.Equal(summary, prepared.Argv[^1]);
        Assert.Contains(" -- ", prepared.LaunchCommand);
    }

    [Theory]
    [InlineData(LLM.OpenCode)]
    [InlineData(LLM.Copilot)]
    [InlineData(LLM.Antigravity)]
    public async Task NamedPromptClisRejectOptionLookingMessages(LLM llm)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().PrepareSessionAsync(llm, null, null, initialPrompt: "  --config=untrusted"));
    }

    [Fact]
    public void PublicJsonCannotGrantBoardTools()
    {
        var request = JsonSerializer.Deserialize("{\"cli\":\"claude\",\"authorizeBoardTools\":true}", AppJsonSerializerContext.Default.StartTerminalRequest);
        Assert.False(request!.AuthorizeBoardTools);
        var json = JsonSerializer.Serialize(new StartTerminalRequest(AuthorizeBoardTools: true), AppJsonSerializerContext.Default.StartTerminalRequest);
        Assert.DoesNotContain("authorizeBoardTools", json, StringComparison.OrdinalIgnoreCase);
    }
}
