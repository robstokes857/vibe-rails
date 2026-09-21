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
[Collection("ProcessEnvIsolation")] // reads ParserConfigs.GetStatePath (process-global), which DataExportServiceTests rewrites
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
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(VibeRails.Services.PythonScripts.IPythonScriptService));
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

    [Fact]
    public void ConfigureServices_RegistersTheBoardToolAndItsOwnStore()
    {
        // The kanban tools must work from ANY terminal (the CLI spawns `vb mcp` with no VibeRails
        // tab involved), so the stdio host carries the board store + service itself — the store is
        // registered as a lazy factory so this test never opens state.db.
        var services = new ServiceCollection();
        McpStdioHost.ConfigureServices(services);

        Assert.Contains(services, d => d.ServiceType == typeof(BoardTool));
        Assert.Contains(services, d => d.ServiceType == typeof(VibeRails.Services.Board.IBoardStore));
        Assert.Contains(services, d => d.ServiceType == typeof(VibeRails.Services.Board.IBoardService));
        Assert.Contains(services, d => d.ServiceType == typeof(VibeRails.Services.Board.IBoardProjectResolver));
        Assert.Contains(services, d => d.ServiceType == typeof(VibeRails.Services.Board.IBoardCommitService));
        Assert.Contains(services, d => d.ServiceType == typeof(VibeRails.Services.Board.IBoardLiveSessionProbe));
        // No dashboard repository in this short-lived child: its constructor runs the full migration pass.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(VibeRails.DB.IRepository));
    }

    [Fact]
    public void ConfigureServices_ResolvesSearchDbService_NotJustRegistersIt()
    {
        // Descriptor assertions alone let a real outage through: BertSearchDbService moved to a
        // (vectorDatabasePath, stateDatabasePath) ctor while this host still registered it by type
        // activation, so the descriptor existed but every resolution threw "Unable to resolve
        // service for type 'System.String'" -- i.e. search_history was dead over stdio.
        // Resolving IBertSearchDbService touches no ONNX model and opens no database: the ctor
        // only validates its two path strings.
        var services = new ServiceCollection();
        McpStdioHost.ConfigureServices(services);
        using var provider = services.BuildServiceProvider(validateScopes: true);

        var search = provider.GetRequiredService<IBertSearchDbService>();

        Assert.EndsWith("bert_user_text_vectors.db", search.VectorDatabasePath);
        Assert.EndsWith("state.db", search.StateDatabasePath);
    }
}
