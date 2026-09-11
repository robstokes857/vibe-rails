using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using VibeRails.Services.BertBaseClasses;
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
/// Exposes the SAME tools as the in-process HTTP server (rules, session search, token saver,
/// Python scripts, and the kanban <see cref="BoardTool"/>), so the two transports stay in lockstep.
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
        // Content root pinned to the install directory (not the CLI's cwd, which this child inherits)
        // so appsettings.json is found and VibeRails:InstallDirName is honoured — the same shape as
        // JobDaemonProcessHost. Without this the host only ever found state.db through the
        // PathConstants fallback, and the board store below needs the real state path.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Strip the default console logger so nothing pollutes the stdio transport. File logging
        // still flows through the static Serilog logger configured in Program.cs.
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            optional: true,
            reloadOnChange: false);
        InitializeRuntimePaths(builder.Configuration["VibeRails:InstallDirName"]);

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
    /// Makes <c>ParserConfigs.GetStatePath()</c> answer in this process. Idempotent: a host that
    /// already initialised (tests, or a future embedding) keeps its state path.
    /// </summary>
    internal static void InitializeRuntimePaths(string? installDirectoryName)
    {
        if (!string.IsNullOrWhiteSpace(VibeRails.Utils.ParserConfigs.GetStatePath()))
            return;
        VibeRails.Utils.GlobalRuntimePaths.Initialize(
            string.IsNullOrWhiteSpace(installDirectoryName)
                ? VibeRails.Utils.PathConstants.DEFAULT_INSTALL_DIR_NAME
                : installDirectoryName);
    }

    /// <summary>State.db for this process: the configured path, else the default install directory.</summary>
    internal static string ResolveStatePath()
    {
        var configured = VibeRails.Utils.ParserConfigs.GetStatePath();
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(VibeRails.Utils.PathConstants.GetInstallDirPath(), VibeRails.Utils.PathConstants.STATE_FILENAME)
            : configured;
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
        // The token-saver control tools. This is the transport that matters for them: the CLI
        // spawns this process and hands it the environment naming the proxy to call, so this child
        // can pause the exact tab whose output it is reading. Named client, short timeout — the
        // call is a loopback hop, so slow means broken.
        services.AddHttpClient(TokenSaverTool.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<TokenSaverTool>();
        // Signed-Python-script guidance. File-based state only (no DB, no interpreter
        // probe on construction), so it is safe in this stdio child process too.
        services.AddSingleton<VibeRails.Services.PythonScripts.IPythonScriptService>(sp =>
            new VibeRails.Services.PythonScripts.PythonScriptService(
                mcpConfigurationStore: sp.GetRequiredService<
                    VibeRails.Services.PythonScripts.IPythonScriptMcpConfigurationStore>(),
                pythonRunnerProvider: () => new PyBridge.PythonRunner(
                    PyBridge.PythonRunnerOptions.Discover())));
        services.AddSingleton<VibeRails.Services.PythonScripts.IPythonScriptMcpConfigurationStore,
            VibeRails.Services.PythonScripts.PythonScriptMcpConfigurationStore>();
        services.AddSingleton<VibeRails.Services.PythonScripts.IPythonScriptMcpService,
            VibeRails.Services.PythonScripts.PythonScriptMcpService>();
        services.AddScoped<PythonScriptTool>();
        // Kanban board tools. Backed by the board's own SQLite store (it owns its schema, so no
        // Repository migration pass runs in this short-lived child) and scoped to the project by
        // the CLI's inherited cwd / the launching session — see BoardProjectResolver. This is what
        // lets an LLM pick up a card from ANY terminal, not only a VibeRails tab. No live tab host
        // here, so linked sessions never report as open from this transport.
        services.AddSingleton<VibeRails.Services.Board.IBoardStore>(_ => new VibeRails.Services.Board.BoardStore(
            $"Data Source={ResolveStatePath()};Mode=ReadWriteCreate;Cache=Shared"));
        services.AddSingleton<VibeRails.Services.Board.IBoardProjectResolver, VibeRails.Services.Board.BoardProjectResolver>();
        services.AddSingleton<VibeRails.Services.Board.IBoardCommitService, VibeRails.Services.Board.BoardCommitService>();
        services.AddSingleton<VibeRails.Services.Board.IBoardLiveSessionProbe, VibeRails.Services.Board.NullBoardLiveSessionProbe>();
        services.AddScoped<VibeRails.Services.Board.IBoardService, VibeRails.Services.Board.BoardService>();
        services.AddScoped<BoardTool>();
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
            .WithTools<TokenSaverTool>()
            .WithTools<PythonScriptTool>()
            .WithTools<BoardTool>()
            .WithPythonScriptTools();
    }
}
