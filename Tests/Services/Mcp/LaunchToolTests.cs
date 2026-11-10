using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// Behaviour of the launch tools through a real loopback MCP host, with the production transport and
/// serializer metadata (<see cref="McpToolRegistration.SerializerOptions"/>), a disposable
/// launches.db and a temporary project (VIBE-296 N2). McpServerHttpTests only pins the tool names.
/// </summary>
public sealed class LaunchToolTests : IAsyncLifetime
{
    private static readonly HttpClient SharedClient = new();
    private readonly string root = Path.Combine(Path.GetTempPath(), "launch-tool-mcp-" + Guid.NewGuid().ToString("N"));
    private string project = null!;
    private LaunchStore store = null!;
    private WebApplication app = null!;
    private Uri endpoint = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        project = Path.Combine(root, "Repo");
        Directory.CreateDirectory(project);
        store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // Port 0: Kestrel binds a free port itself, so this host never races another class's port pick.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ILaunchStore>(store);
        builder.Services.AddSingleton(Mock.Of<IAutomationScriptService>());
        var metadata = new Mock<VibeRails.DB.IRepository>();
        metadata.Setup(x => x.GetKnownProjectPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([project, root]);
        metadata.Setup(x => x.GetProjectDisplayNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("Project");
        builder.Services.AddSingleton(metadata.Object);
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(project);
        builder.Services.AddSingleton(projects.Object);
        // No Board store: this host proves the tools, not App/Board context composition.
        builder.Services.AddScoped(sp => new LaunchService(sp.GetRequiredService<ILaunchStore>(), sp.GetRequiredService<IAutomationScriptService>()));
        builder.Services.AddScoped<LaunchTool>();
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "viberails-launch-tool-test", Version = "1.0.0" })
            .WithVibeRailsHttpTransport()
            .WithTools<LaunchTool>(McpToolRegistration.SerializerOptions);
        app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        endpoint = new Uri(app.Urls.First() + "/mcp");
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Task<McpClient> ConnectAsync() => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp
    }, SharedClient, NullLoggerFactory.Instance, ownsHttpClient: false), cancellationToken: Ct);

    private static JsonElement Json(CallToolResult result)
    {
        Assert.False(result.IsError == true, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        return JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text).RootElement;
    }

    [Fact]
    public async Task ProjectScopeRoundTripsThroughMcpAndRejectsOutOfScopeLaunches()
    {
        await using var client = await ConnectAsync();
        var known = Json(await client.CallToolAsync("list_launch_projects", new Dictionary<string, object?>(), cancellationToken: Ct));
        Assert.Equal(2, known.GetArrayLength());
        var saved = Json(await client.CallToolAsync("save_launch_preset", new Dictionary<string, object?>
        {
            ["definition"] = new { name = "Scoped", cli = "codex", initialPrompt = "Work", projectPaths = new[] { root } }
        }, cancellationToken: Ct));
        var id = saved.GetProperty("preset").GetProperty("id").GetString()!;
        Assert.Single(saved.GetProperty("preset").GetProperty("projectPaths").EnumerateArray());
        var catalog = Json(await client.CallToolAsync("list_launch_catalog", new Dictionary<string, object?>(), cancellationToken: Ct));
        Assert.Equal(0, catalog.GetProperty("items").GetArrayLength());
        var management = Json(await client.CallToolAsync("list_launch_catalog", new Dictionary<string, object?> { ["includeAll"] = true }, cancellationToken: Ct));
        Assert.False(management.GetProperty("items")[0].GetProperty("inProject").GetBoolean());
        var rejected = await client.CallToolAsync("run_launch", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: Ct);
        Assert.True(rejected.IsError);
        Assert.Empty((await store.ListRunsAsync(LaunchService.ProjectKey(project), ct: Ct)).Runs);
    }

    [Fact]
    public async Task CatalogListsEligibilityAndRunLaunchQueuesOneDeduplicatedProjectRun()
    {
        var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
        var agent = (await service.SavePresetAsync(null, new("Reviewer", "Reads diffs", "codex", InitialPrompt: "Review the branch"), Ct)).Preset;
        var quick = (await service.SavePresetAsync(null, new("Shell helper", "", "claude"), Ct)).Preset;
        await using var client = await ConnectAsync();

        var catalog = Json(await client.CallToolAsync("list_launch_catalog", new Dictionary<string, object?>(), cancellationToken: Ct));
        var items = catalog.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!);
        Assert.Equal("agent", items[agent.Id].GetProperty("kind").GetString());
        Assert.True(items[agent.Id].GetProperty("canRunAutomatically").GetBoolean());
        Assert.Equal("quick-launch", items[quick.Id].GetProperty("kind").GetString());
        Assert.True(items[quick.Id].GetProperty("canLaunch").GetBoolean());
        Assert.False(items[quick.Id].GetProperty("canRunAutomatically").GetBoolean());

        var arguments = new Dictionary<string, object?> { ["id"] = agent.Id, ["type"] = "preset", ["occurrenceId"] = "mcp-1" };
        var run = Json(await client.CallToolAsync("run_launch", arguments, cancellationToken: Ct));
        Assert.Equal("queued", run.GetProperty("status").GetString());
        Assert.Equal(LaunchService.ProjectKey(project), run.GetProperty("projectPath").GetString());
        var context = run.GetProperty("snapshot").GetProperty("context");
        Assert.Equal("manual", context.GetProperty("triggerKind").GetString());
        Assert.Equal("manual:mcp-1", context.GetProperty("occurrenceId").GetString());
        Assert.Equal(project, context.GetProperty("workingDirectory").GetString());
        Assert.False(run.TryGetProperty("claimToken", out _), "the claim token never leaves the store");

        // The same occurrence is one delivery: a repeated call returns the original run.
        var again = Json(await client.CallToolAsync("run_launch", arguments, cancellationToken: Ct));
        Assert.Equal(run.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        Assert.Single((await store.ListRunsAsync(LaunchService.ProjectKey(project), ct: Ct)).Runs);

        var missing = await client.CallToolAsync("run_launch", new Dictionary<string, object?> { ["id"] = "preset_missing" }, cancellationToken: Ct);
        Assert.True(missing.IsError);
        Assert.Single((await store.ListRunsAsync(LaunchService.ProjectKey(project), ct: Ct)).Runs);
    }
}
