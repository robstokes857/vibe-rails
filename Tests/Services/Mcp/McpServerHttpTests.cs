using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
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

    private static readonly string[] ExpectedTools =
    {
        "echo",
        "check_rules",
        "validate_vca",
        "search_history",
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

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "viberails-mcp-test", Version = "1.0.0" };
            })
            .WithHttpTransport()
            .WithTools<EchoTool>()
            .WithTools<RulesTool>()
            .WithTools<SessionSearchTool>();

        _app = builder.Build();
        _app.MapMcp("/mcp");
        await _app.StartAsync();
        _endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
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
            NullLoggerFactory.Instance);

        return await McpClientService.ConnectAsync(transport, cancellationToken: ct);
    }

    [Fact]
    public async Task ListTools_ExposesExpectedToolsAsSnakeCase()
    {
        await using var client = await ConnectAsync();
        var tools = await client.GetAvailableToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        foreach (var expected in ExpectedTools)
        {
            Assert.Contains(expected, names);
        }
        Assert.Equal(ExpectedTools.Length, names.Count);
    }

    [Fact]
    public async Task CallEcho_RoundTripsMessage()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "ping-42" });
        Assert.Equal("Echo: ping-42", result);
    }

    [Fact]
    public async Task CallCheckRules_DetectsSecret()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync("check_rules", new Dictionary<string, object?> { ["content"] = "password = hunter2longvalue" });
        Assert.StartsWith("FAIL", result);
        Assert.Contains("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallSearchHistory_ReturnsFusedHits()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync("search_history", new Dictionary<string, object?> { ["query"] = "websocket timeout" });

        // The fake returns one fused hit; the tool should surface its session id, agent, and preview.
        Assert.Contains("sess-abc", result);
        Assert.Contains("claude", result);
        Assert.Contains("websocket reconnect", result);
    }

    [Fact]
    public async Task CallSearchHistory_EmptyQueryFails()
    {
        await using var client = await ConnectAsync();
        var result = await client.CallToolAsync("search_history", new Dictionary<string, object?> { ["query"] = "   " });
        Assert.StartsWith("FAIL", result);
    }

    [Fact]
    public async Task Ping_Succeeds()
    {
        await using var client = await ConnectAsync();
        Assert.True(await client.PingAsync());
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
