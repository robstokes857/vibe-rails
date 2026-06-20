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
    /// Calls a specific tool with the provided arguments. The returned
    /// <see cref="McpToolCallOutcome"/> carries both the tool's text output and whether the tool
    /// reported a tool-level error — MCP surfaces those as a result with <c>isError=true</c>
    /// rather than throwing, so callers must inspect the flag instead of treating the text as output.
    /// </summary>
    Task<McpToolCallOutcome> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pings the MCP server to check connectivity.
    /// </summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an MCP tool call. <see cref="IsError"/> reflects the MCP <c>isError</c> flag — a tool
/// that fails returns a normal result with this set, not a thrown exception — and <see cref="Text"/>
/// is the first text content block (the output on success, or the error message when
/// <see cref="IsError"/> is true).
/// </summary>
public readonly record struct McpToolCallOutcome(bool IsError, string Text);
