using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using VibeRails.DTOs;
using VibeRails.Services.Mcp;

namespace VibeRails.Routes;

/// <summary>
/// MCP Explorer API. The MCP server itself is hosted in-process and exposed over HTTP at
/// <c>/mcp</c> (see <c>app.MapMcp</c> in Program.cs). These endpoints are a thin convenience
/// layer for the dashboard's MCP Explorer: they connect to that same in-process endpoint over
/// loopback HTTP — exercising the exact Streamable-HTTP path an external CLI would use — and
/// surface the tool list / tool-call results to the UI.
/// </summary>
public static class McpRoutes
{
    private const string SessionCookieName = "viberails_session";

    public static void Map(WebApplication app)
    {
        // Reports that the in-process MCP server is reachable and at what loopback URL.
        app.MapGet("/api/v1/mcp/status", (HttpContext ctx) =>
        {
            var endpoint = BuildLoopbackEndpoint(ctx);
            return Results.Ok(new McpStatusResponse(
                ServerAvailable: true,
                ServerPath: endpoint.ToString(),
                Message: "In-process MCP server hosted at /mcp"));
        }).WithName("GetMcpStatus");

        // Lists the tools the in-process MCP server exposes.
        app.MapGet("/api/v1/mcp/tools", async (HttpContext ctx, CancellationToken cancellationToken) =>
        {
            try
            {
                await using var client = await ConnectLoopbackAsync(ctx, cancellationToken);
                var tools = await client.GetAvailableToolsAsync(cancellationToken);
                var toolInfos = tools.Select(t => new McpToolInfo(t.Name, t.Description ?? "")).ToList();
                return Results.Ok(toolInfos);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to connect to MCP server: {ex.Message}"));
            }
        }).WithName("GetMcpTools");

        // Calls a single tool by name with caller-supplied JSON arguments.
        app.MapPost("/api/v1/mcp/tools/{name}", async (
            HttpContext ctx,
            string name,
            McpToolCallRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await using var client = await ConnectLoopbackAsync(ctx, cancellationToken);
                var outcome = await client.CallToolAsync(name, request.Arguments, cancellationToken);

                // A tool-level error (isError=true) is a well-formed response, not a transport
                // failure: return 200 with Success=false so the Explorer shows "Call failed" plus
                // the tool's own message. The catch below stays for actual connection faults.
                return outcome.IsError
                    ? Results.Ok(new McpToolCallResponse(false, "", outcome.Text))
                    : Results.Ok(new McpToolCallResponse(true, outcome.Text));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new McpToolCallResponse(false, "", ex.Message));
            }
        }).WithName("CallMcpTool");
    }

    /// <summary>
    /// Connects to the in-process /mcp endpoint over loopback HTTP, forwarding the caller's
    /// session token so the request clears CookieAuthMiddleware. Kestrel serves the loopback
    /// request on a separate connection, so there is no self-deadlock.
    /// </summary>
    private static async Task<McpClientService> ConnectLoopbackAsync(HttpContext ctx, CancellationToken cancellationToken)
    {
        var sessionToken = ctx.Request.Cookies[SessionCookieName]
            ?? ctx.Request.Headers[SessionCookieName].FirstOrDefault()
            ?? string.Empty;

        var options = new HttpClientTransportOptions
        {
            Endpoint = BuildLoopbackEndpoint(ctx),
            Name = "viberails-mcp",
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                [SessionCookieName] = sessionToken
            }
        };

        var transport = new HttpClientTransport(options, NullLoggerFactory.Instance);
        return await McpClientService.ConnectAsync(transport, cancellationToken: cancellationToken);
    }

    private static Uri BuildLoopbackEndpoint(HttpContext ctx)
    {
        // The server binds localhost; reach /mcp on the same port via the loopback address.
        var port = ctx.Request.Host.Port ?? 80;
        return new Uri($"http://127.0.0.1:{port}/mcp");
    }
}
