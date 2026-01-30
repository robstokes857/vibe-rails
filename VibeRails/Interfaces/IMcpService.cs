using ModelContextProtocol.Client;

namespace VibeRails.Interfaces;

/// <summary>
/// A portable SDK interface for interacting with MCP Servers.
/// </summary>
public interface IMcpService : IAsyncDisposable
{
    /// <summary>
    /// Gets all tools available from the MCP server.
    /// </summary>
    Task<IEnumerable<McpClientTool>> GetAvailableToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls a specific tool with the provided arguments.
    /// </summary>
    Task<string> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pings the MCP server to check connectivity.
    /// </summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
