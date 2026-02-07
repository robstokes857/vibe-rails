using ModelContextProtocol.Client;
using VibeRails.DTOs;
using VibeRails.Services.Mcp;

namespace VibeRails.Routes;

public static class McpRoutes
{
    public static void Map(WebApplication app)
    {
        // MCP Endpoints
        app.MapGet("/api/v1/mcp/status", (McpSettings settings) =>
        {
            var available = !string.IsNullOrEmpty(settings.ServerPath) && File.Exists(settings.ServerPath);
            var message = available
                ? "MCP server executable found"
                : "MCP server executable not found. Build MCP_Server project first.";
            return Results.Ok(new McpStatusResponse(available, settings.ServerPath, message));
        }).WithName("GetMcpStatus");

        app.MapGet("/api/v1/mcp/tools", async (McpSettings settings, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(settings.ServerPath) || !File.Exists(settings.ServerPath))
            {
                return Results.BadRequest(new ErrorResponse("MCP server executable not found. Build MCP_Server project first."));
            }

            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = settings.ServerPath
                });

                await using var client = await McpClientService.ConnectAsync(transport, cancellationToken: cancellationToken);
                var tools = await client.GetAvailableToolsAsync(cancellationToken);
                var toolInfos = tools.Select(t => new McpToolInfo(t.Name, t.Description ?? "")).ToList();
                return Results.Ok(toolInfos);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to connect to MCP server: {ex.Message}"));
            }
        }).WithName("GetMcpTools");

        app.MapPost("/api/v1/mcp/tools/{name}", async (
            McpSettings settings,
            string name,
            McpToolCallRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(settings.ServerPath) || !File.Exists(settings.ServerPath))
            {
                return Results.BadRequest(new McpToolCallResponse(false, "", "MCP server executable not found."));
            }

            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = settings.ServerPath
                });

                await using var client = await McpClientService.ConnectAsync(transport, cancellationToken: cancellationToken);
                var result = await client.CallToolAsync(name, request.Arguments, cancellationToken);
                return Results.Ok(new McpToolCallResponse(true, result));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new McpToolCallResponse(false, "", ex.Message));
            }
        }).WithName("CallMcpTool");
    }
}
