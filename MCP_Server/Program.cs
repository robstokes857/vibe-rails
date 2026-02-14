using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MCP_Server.Tools;
using Serilog;

// Configure Serilog file logger - must not write to stdout/stderr (stdio MCP transport)
var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".vibe_rails", "mcp-server.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.File(
        logPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);

// Replace default providers with Serilog file-only provider
// This prevents any console/debug output that would corrupt the stdio MCP channel
builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

// Add MCP Server with Stdio transport
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "vibecontrol-mcp-server", Version = "1.0.0" };
})
.WithTools<EchoTool>()
.WithTools<RulesTool>()
.WithTools<VectorSearchTool>()
.WithStdioServerTransport();

var host = builder.Build();

try
{
    Log.Warning("MCP Server starting");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "MCP Server terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
