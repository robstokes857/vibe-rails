using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using System.Net;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Mcp;
using VibeRails.Services.PythonScripts;

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
    private const string TabHeaderName = "viberails_tab";
    private const int MaxCustomHeaders = 20;

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
        app.MapGet("/api/v1/mcp/tools", async (
            HttpContext ctx,
            IPythonScriptMcpService pythonScriptMcpService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await using var client = await ConnectAsync(ctx, null, null, cancellationToken);
                var tools = await client.GetAvailableToolsAsync(cancellationToken);
                var pythonToolSources = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pythonTool in await pythonScriptMcpService.ListToolsAsync(cancellationToken))
                {
                    var sourceName = pythonTool.Meta?["scriptName"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(sourceName))
                    {
                        pythonToolSources.TryAdd(pythonTool.Name, sourceName);
                    }
                }
                var toolInfos = tools.Select(tool =>
                {
                    pythonToolSources.TryGetValue(tool.Name, out var sourceName);
                    return ToToolInfo(tool, sourceName);
                }).ToList();
                return Results.Ok(toolInfos);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to connect to MCP server: {ex.Message}"));
            }
        }).WithName("GetMcpTools");

        // Inspects any Streamable HTTP MCP endpoint supplied by the dashboard Explorer.
        app.MapPost("/api/v1/mcp/inspect", async (
            HttpContext ctx,
            McpServerTargetRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var endpoint = ResolveEndpoint(ctx, request.Endpoint);
                await using var client = await ConnectAsync(ctx, endpoint, request.Headers, cancellationToken);
                var tools = await client.GetAvailableToolsAsync(cancellationToken);
                return Results.Ok(new McpInspectResponse(
                    Success: true,
                    ServerAvailable: true,
                    Endpoint: endpoint.ToString(),
                    Message: "Connected",
                    Tools: tools.Select(tool => ToToolInfo(tool)).ToList()));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to inspect MCP server: {ex.Message}"));
            }
        }).WithName("InspectMcpServer");

        // Calls a single tool by name with caller-supplied JSON arguments.
        app.MapPost("/api/v1/mcp/tools/{name}", async (
            HttpContext ctx,
            string name,
            McpToolCallRequest request,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await using var client = await ConnectAsync(ctx, ResolveEndpoint(ctx, request.Endpoint), request.Headers, cancellationToken);
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
    private static async Task<McpClientService> ConnectAsync(
        HttpContext ctx,
        Uri? endpoint,
        Dictionary<string, string>? customHeaders,
        CancellationToken cancellationToken)
    {
        endpoint ??= BuildLoopbackEndpoint(ctx);
        var headers = BuildHeaders(ctx, endpoint, customHeaders);

        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = IsLoopbackMcpEndpoint(ctx, endpoint) ? "viberails-mcp" : "mcp-explorer",
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers
        };

        var transport = new HttpClientTransport(options, NullLoggerFactory.Instance);
        return await McpClientService.ConnectAsync(transport, cancellationToken: cancellationToken);
    }

    private static Dictionary<string, string> BuildHeaders(
        HttpContext ctx,
        Uri endpoint,
        Dictionary<string, string>? customHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (customHeaders is { Count: > 0 })
        {
            if (customHeaders.Count > MaxCustomHeaders)
            {
                throw new ArgumentException($"At most {MaxCustomHeaders} custom headers are allowed.");
            }

            foreach (var (name, value) in customHeaders)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                {
                    throw new ArgumentException("Custom header names cannot be empty or contain control characters.");
                }

                if (value.Any(char.IsControl))
                {
                    throw new ArgumentException($"Custom header '{name}' contains control characters.");
                }

                headers[name.Trim()] = value;
            }
        }

        if (IsLoopbackMcpEndpoint(ctx, endpoint))
        {
            var sessionToken = ctx.Request.Cookies[SessionCookieName]
                ?? ctx.Request.Headers[SessionCookieName].FirstOrDefault()
                ?? string.Empty;
            headers[SessionCookieName] = sessionToken;
            // /mcp requires the per-tab token too (its tools can reach a host shell, so a leaked
            // session token alone must not clear the gate). These handlers sit behind full auth,
            // so the caller always has one to forward.
            headers[TabHeaderName] = ctx.Request.Headers[TabHeaderName].FirstOrDefault() ?? string.Empty;
        }

        return headers;
    }

    private static McpToolInfo ToToolInfo(
        McpClientTool tool,
        string? pythonScriptName = null)
    {
        return new McpToolInfo(
            Name: tool.Name,
            Description: tool.Description ?? "",
            Title: tool.Title,
            InputSchema: CloneIfDefined(tool.JsonSchema),
            ReturnSchema: CloneIfDefined(tool.ReturnJsonSchema),
            Category: pythonScriptName is null ? "built-in" : "python-script",
            SourceName: pythonScriptName);
    }

    private static JsonElement? CloneIfDefined(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Undefined ? null : element.Clone();
    }

    private static JsonElement? CloneIfDefined(JsonElement? element)
    {
        return element.HasValue ? CloneIfDefined(element.Value) : null;
    }

    private static Uri ResolveEndpoint(HttpContext ctx, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return BuildLoopbackEndpoint(ctx);
        }

        endpoint = endpoint.Trim();
        if (endpoint.StartsWith("/", StringComparison.Ordinal))
        {
            var port = ctx.Request.Host.Port ?? 80;
            return new Uri($"http://127.0.0.1:{port}{endpoint}");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    private static Uri BuildLoopbackEndpoint(HttpContext ctx)
    {
        // The server binds localhost; reach /mcp on the same port via the loopback address.
        var port = ctx.Request.Host.Port ?? 80;
        return new Uri($"http://127.0.0.1:{port}/mcp");
    }

    private static bool IsLoopbackMcpEndpoint(HttpContext ctx, Uri endpoint)
    {
        var port = ctx.Request.Host.Port ?? 80;
        var path = endpoint.AbsolutePath.TrimEnd('/');
        var isMcpPath = string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase);
        var isSamePort = endpoint.Port == port;
        var isLoopbackHost = string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(endpoint.Host, out var address) && IPAddress.IsLoopback(address));

        return isMcpPath && isSamePort && isLoopbackHost;
    }
}
