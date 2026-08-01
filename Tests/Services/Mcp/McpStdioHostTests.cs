using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.BertV2;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.HostShell;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Mcp.WebResearch;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// Tests for the `vb mcp` stdio entry point. Verifies the trigger and that the minimal BERT
/// read-path + tools are registered. We assert on service descriptors rather than resolving
/// them, so no ONNX model is loaded.
/// </summary>
public class McpStdioHostTests
{
    [Theory]
    [InlineData(new[] { "mcp" }, true)]
    [InlineData(new[] { "MCP" }, true)]
    [InlineData(new[] { "mcp", "--anything" }, true)]
    [InlineData(new[] { "--vs-code-v1" }, false)]
    [InlineData(new[] { "--env", "mcp" }, false)] // "mcp" only triggers as the first arg
    [InlineData(new string[0], false)]
    public void IsRequested_MatchesOnlyMcpSubcommand(string[] args, bool expected)
    {
        Assert.Equal(expected, McpStdioHost.IsRequested(args));
    }

    [Fact]
    public void ConfigureServices_RegistersSearchToolAndReadPath()
    {
        var services = new ServiceCollection();
        McpStdioHost.ConfigureServices(services);

        // The search tool and its real BERT-backed dependency must be wired.
        Assert.Contains(services, d => d.ServiceType == typeof(SessionSearchTool));
        // HostShellTools/WebResearchTools + their backing services are intentionally not registered
        // now (see MapRegisterServices, security review 2026-07-02).
        Assert.Contains(services, d => d.ServiceType == typeof(IUnifiedSearchService));
        Assert.Contains(services, d => d.ServiceType == typeof(IBertV2BgeEmbedder));
        Assert.Contains(services, d => d.ServiceType == typeof(IBertSearchDbService));
    }

    [Fact]
    public void ConfigureServices_RegistersTokenSaverToolAndItsHttpClient()
    {
        // This is the transport that matters for the pause tools: the CLI spawns `vb mcp` and hands
        // it the environment naming the proxy to call. Registered here but not in MapRegisterServices
        // would mean agents silently have no way to pause, with nothing failing to say so.
        var services = new ServiceCollection();
        McpStdioHost.ConfigureServices(services);

        Assert.Contains(services, d => d.ServiceType == typeof(TokenSaverTool));
        Assert.Contains(services, d => d.ServiceType == typeof(IHttpClientFactory));
    }
}
