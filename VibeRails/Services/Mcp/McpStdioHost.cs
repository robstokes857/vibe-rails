using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.AgentTools;
using VibeRails.Services.BertV2;
using VibeRails.Services.Mcp.HostShell;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Mcp.WebResearch;

namespace VibeRails.Services.Mcp;

/// <summary>
/// "vb mcp" mode: speaks MCP over stdin/stdout for CLIs that spawn it
/// (e.g. <c>claude mcp add viberails -- vb mcp</c>). No web server, no port, no auth — a stdio
/// MCP server is a child process the CLI owns and talks to over pipes, so it is inherently scoped
/// to the spawning process and needs no token.
///
/// Exposes the SAME tools as the in-process HTTP server (<see cref="RulesTool"/>,
/// <see cref="SessionSearchTool"/>, and terminal controls), so the two transports stay in
/// lockstep.
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
        var builder = Host.CreateApplicationBuilder(args);

        // Strip the default console logger so nothing pollutes the stdio transport. File logging
        // still flows through the static Serilog logger configured in Program.cs.
        builder.Logging.ClearProviders();

        ConfigureServices(builder.Services);

        var host = builder.Build();
        Log.Information(
            "[MCP] Stdio server starting. processId={ProcessId} workingDirectory={WorkingDirectory}",
            Environment.ProcessId,
            Environment.CurrentDirectory);

        try
        {
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MCP] Stdio server failed. processId={ProcessId}", Environment.ProcessId);
            throw;
        }
        finally
        {
            Log.Information("[MCP] Stdio server stopped. processId={ProcessId}", Environment.ProcessId);
        }
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
        services.AddSingleton<IAgentTerminalToolGateway, HttpAgentTerminalToolGateway>();
        services.AddScoped<TerminalTools>();
        // HostShellTools (run_shell_command) and WebResearchTools (web_search/web_fetch) are
        // intentionally not exposed for now (security review 2026-07-02); mirrors MapRegisterServices.
        // Classes kept in-tree for re-add.

        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "viberails-mcp", Version = "1.0.0" };
            })
            .WithStdioServerTransport()
            .WithTools<RulesTool>()
            .WithTools<SessionSearchTool>()
            .WithTools<TerminalTools>();
    }
}
