using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MCP_Server.Tools;

// Simple MCP Server using Stdio transport
// The server is spawned by the client and communicates via stdin/stdout

var builder = Host.CreateApplicationBuilder(args);

// Disable console logging to avoid corrupting Stdio channel
builder.Logging.ClearProviders();

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
await host.RunAsync();
