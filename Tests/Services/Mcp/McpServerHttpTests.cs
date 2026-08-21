using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.HostShell;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Mcp.WebResearch;
using VibeRails.Services.PythonScripts;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// End-to-end protocol tests: hosts the in-process MCP server on a real loopback Kestrel
/// — the same AddMcpServer().WithHttpTransport() + MapMcp("/mcp") wiring Program.cs uses —
/// and drives it through VibeRails' own <see cref="McpClientService"/> over the Streamable
/// HTTP transport. This proves the registration is AOT-shaped correctly, the endpoint is
/// reachable, every tool lists, and the DI-injected <see cref="SessionSearchTool"/> resolves
/// and runs. The real BGE/sqlite-vec search is substituted with a deterministic fake so the
/// test needs no model files or database; the live search is exercised by the runtime smoke.
/// </summary>
public class McpServerHttpTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _endpoint = null!;
    private readonly string _pythonMcpDirectory = Path.Combine(
        Path.GetTempPath(), $"viberails-mcp-http-python-{Guid.NewGuid():N}");
    // One shared client for the whole Kestrel test class instead of the SDK creating and disposing
    // its own internal HttpClient per ConnectAsync (which clogs TCP ports across a run).
    private static readonly HttpClient SharedClient = new();

    private static readonly string[] ExpectedTools =
    {
        "validate_vca",
        "search_history",
        "pause_token_saver",
        "resume_token_saver",
        "get_token_saver_status",
        "python_http_smoke",
    };

    public async ValueTask InitializeAsync()
    {
        var port = PortFinder.FindOpenPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        // Stand in for the real BGE/sqlite-vec search so the test is hermetic.
        builder.Services.AddSingleton<IUnifiedSearchService>(new FakeUnifiedSearchService());
        builder.Services.AddScoped<SessionSearchTool>();
        builder.Services.AddHttpClient(TokenSaverTool.HttpClientName);
        builder.Services.AddScoped<TokenSaverTool>();

        var pythonScripts = new Mock<IPythonScriptService>(MockBehavior.Loose);
        pythonScripts.Setup(service => service.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonScriptListResponse(
                true,
                _pythonMcpDirectory,
                [new PythonScriptInfo(
                    "http-smoke.py", PythonScriptService.StatusApproved, null, null, 1,
                    Path.Combine(_pythonMcpDirectory, "http-smoke.py"))]));
        pythonScripts.Setup(service => service.RunAsync(
                "http-smoke.py",
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonScriptRunResponse(
                "http-smoke.py", 0, false, "dynamic python tool ran", "", 1,
                DateTime.UtcNow.ToString("O")));
        var pythonStore = new PythonScriptMcpConfigurationStore(_pythonMcpDirectory);
        await pythonStore.SaveAsync(new PythonScriptMcpConfiguration(
            "http-smoke.py",
            "python_http_smoke",
            "Run the dynamic Python MCP HTTP smoke test.",
            []));
        builder.Services.AddSingleton(pythonScripts.Object);
        builder.Services.AddSingleton<IPythonScriptMcpConfigurationStore>(pythonStore);
        builder.Services.AddSingleton<IPythonScriptMcpService, PythonScriptMcpService>();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "viberails-mcp-test", Version = "1.0.0" };
            })
            .WithHttpTransport()
            .WithTools<RulesTool>()
            .WithTools<SessionSearchTool>()
            .WithTools<TokenSaverTool>()
            .WithPythonScriptTools();

        _app = builder.Build();
        _app.MapMcp("/mcp");
        await _app.StartAsync();
        _endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_pythonMcpDirectory))
        {
            Directory.Delete(_pythonMcpDirectory, recursive: true);
        }
    }

    private async Task<McpClientService> ConnectAsync(CancellationToken ct = default)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = _endpoint,
                Name = "viberails-mcp-test",
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            SharedClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);

        return await McpClientService.ConnectAsync(transport, cancellationToken: ct);
    }

    [Fact]
    public async Task ListTools_ExposesExpectedToolsAsSnakeCase()
    {
        await using var client = await ConnectAsync(TestContext.Current.CancellationToken);
        var tools = await client.GetAvailableToolsAsync(TestContext.Current.CancellationToken);
        var names = tools.Select(t => t.Name).ToHashSet();

        foreach (var expected in ExpectedTools)
        {
            Assert.Contains(expected, names);
        }
        Assert.Equal(ExpectedTools.Length, names.Count);
    }

    [Fact]
    public async Task CallTool_MissingRequiredArgument_ReportsToolError()
    {
        // Regression: the Explorer used to send {} and report "Call succeeded" because IsError was
        // ignored. A missing required parameter must now surface as a tool error (IsError=true).
        // search_history's `query` is required, so an empty argument map trips schema validation.
        await using var client = await ConnectAsync(TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("search_history", new Dictionary<string, object?>(), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.NotEmpty(result.Text);
    }

    [Fact]
    public async Task CallDynamicPythonTool_RunsThroughTheCustomHandler()
    {
        await using var client = await ConnectAsync(TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync(
            "python_http_smoke",
            new Dictionary<string, object?>(),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("dynamic python tool ran", result.Text);
    }

    [Fact]
    public async Task CallSearchHistory_ReturnsFusedHits()
    {
        await using var client = await ConnectAsync(TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("search_history", new Dictionary<string, object?> { ["query"] = "websocket timeout" }, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        // The fake returns one fused hit; the tool should surface its session id, agent, and preview.
        Assert.Contains("sess-abc", result.Text);
        Assert.Contains("claude", result.Text);
        Assert.Contains("websocket reconnect", result.Text);
    }

    [Fact]
    public async Task CallSearchHistory_EmptyQueryFails()
    {
        await using var client = await ConnectAsync(TestContext.Current.CancellationToken);
        var result = await client.CallToolAsync("search_history", new Dictionary<string, object?> { ["query"] = "   " }, TestContext.Current.CancellationToken);
        // Empty query is a graceful "FAIL" verdict, not a tool error.
        Assert.False(result.IsError);
        Assert.StartsWith("FAIL", result.Text);
    }

    /// <summary>Deterministic stand-in for the BGE/sqlite-vec unified search.</summary>
    private sealed class FakeUnifiedSearchService : IUnifiedSearchService
    {
        public UnifiedSearchResponse Search(string query, int topK)
        {
            var hit = new BertSearchHitResponse(
                DocumentId: "doc-1",
                SessionId: "sess-abc",
                UserInputId: 1,
                Sequence: 3,
                TimestampUTC: new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                Cli: "claude",
                EnvironmentName: null,
                WorkingDirectory: "/repo",
                GitCommitHash: null,
                UserTextPreview: "how did we fix the websocket reconnect timeout",
                FileChangeCount: 0,
                Score: 0.42,
                Kind: "input",
                ChunkIndex: null);

            var perMessage = new UnifiedSearchHitGroup("per-message-semantic", "Per-Message Semantic", "", 1, 1, new() { hit });
            var fused = new UnifiedSearchHitGroup("fused", "Fused (RRF)", "", 0, 1, new() { hit });
            return new UnifiedSearchResponse(query, topK, 5, new() { perMessage, fused });
        }
    }

}
