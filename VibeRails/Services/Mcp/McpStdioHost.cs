using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Utils;

namespace VibeRails.Services.Mcp;

/// <summary>
/// "vb mcp" mode: speaks MCP over stdin/stdout for CLIs that spawn it
/// (e.g. <c>claude mcp add viberails -- vb mcp</c>). No web server, no port, no auth — a stdio
/// MCP server is a child process the CLI owns and talks to over pipes, so it is inherently scoped
/// to the spawning process and needs no token.
///
/// Exposes the SAME tools as the in-process HTTP server (<see cref="RulesTool"/>,
/// <see cref="SessionSearchTool"/>), so the two transports stay in lockstep.
///
/// CRITICAL: nothing may be written to stdout except MCP protocol frames. Default host console
/// logging is cleared; the static Serilog logger (configured in Program.cs) writes to file only,
/// and ONNX/diagnostic output goes to stderr — none of which corrupts the stdout JSON-RPC stream.
/// </summary>
public static class McpStdioHost
{
    /// <summary>True when invoked as <c>vb mcp</c>.</summary>
    public static bool IsRequested(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase);

    public static async Task RunAsync(string[] args)
    {
        // Opt-out security gate. MCP is off by default; a stale `mcp add` registration in a CLI can
        // keep spawning `vb mcp` after the user disables MCP, so refuse to expose any tools in that
        // case rather than relying solely on best-effort `mcp remove` cleanup. Read fresh from disk:
        // this is a newly spawned process and the flag may have changed since the registration was
        // created. Fail closed — if we can't confirm opt-in (settings locked mid-write, corrupt), do
        // not serve; the next spawn re-reads. All notices go to stderr — stdout must stay a clean
        // JSON-RPC stream.
        bool mcpEnabled;
        try
        {
            mcpEnabled = Config.LoadFresh().McpEnabled;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"viberails-mcp: could not read VibeRails settings ({ex.GetType().Name}); not starting.");
            return;
        }

        if (!mcpEnabled)
        {
            await Console.Error.WriteLineAsync(
                "viberails-mcp: MCP is disabled in VibeRails settings; not starting. Enable it under Settings → Enable MCP Server.");
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Strip the default console logger so nothing pollutes the stdio transport. File logging
        // still flows through the static Serilog logger configured in Program.cs.
        builder.Logging.ClearProviders();

        ConfigureServices(builder.Services);

        var host = builder.Build();
        await host.RunAsync();
    }

    /// <summary>
    /// Registers the MCP stdio server, its tools, and the minimal BERT read-path that
    /// <see cref="SessionSearchTool"/> needs for <c>search_history</c>. Split out so it can be
    /// unit-tested without standing up the host or reading stdin.
    /// </summary>
    public static void ConfigureServices(IServiceCollection services)
    {
        // Minimal BERT read-path for the real semantic search (mirrors MapRegisterServices, read
        // side only — no write-path stores, no background jobs). The model is loaded lazily on the
        // first search_history call; validate_vca never touches it.
        services.AddSingleton<IBertSettings, BertV2BgeSmallEnSettings>();
        services.AddSingleton<IBertV2BgeEmbedder>(sp =>
        {
            var settings = sp.GetRequiredService<IBertSettings>();
            return new BertV2BgeEmbedder(settings.ModelPath, settings.VocabPath);
        });
        services.AddSingleton<IBertSearchDbService, BertSearchDbService>();
        services.AddSingleton<IBertDocumentResponseMapper, BertDocumentResponseMapper>();
        services.AddSingleton<IUnifiedSearchService, UnifiedSearchService>();
        services.AddScoped<SessionSearchTool>();

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "viberails-mcp", Version = "1.0.0" };
            })
            .WithStdioServerTransport()
            .WithTools<RulesTool>()
            .WithTools<SessionSearchTool>();
    }
}
