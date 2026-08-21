using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VibeRails.Services.PythonScripts;

namespace VibeRails.Services.Mcp;

/// <summary>
/// Adds file-backed Python tools alongside the server's compile-time tool collection.
/// The handlers read configuration on every list/call request, so the dashboard's HTTP
/// explorer sees changes immediately and each newly spawned stdio server sees the same list.
/// </summary>
public static class PythonScriptMcpServerExtensions
{
    public static IMcpServerBuilder WithPythonScriptTools(this IMcpServerBuilder builder)
    {
        return builder
            .WithListToolsHandler(async (request, cancellationToken) =>
            {
                var service = (request.Services
                    ?? throw new InvalidOperationException("MCP request services are unavailable."))
                    .GetRequiredService<IPythonScriptMcpService>();
                return new ListToolsResult
                {
                    Tools = await service.ListToolsAsync(cancellationToken)
                };
            })
            .WithCallToolHandler(async (request, cancellationToken) =>
            {
                var service = (request.Services
                    ?? throw new InvalidOperationException("MCP request services are unavailable."))
                    .GetRequiredService<IPythonScriptMcpService>();
                return await service.CallAsync(
                    request.Params?.Name,
                    request.Params?.Arguments,
                    cancellationToken);
            });
    }
}
