using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Connectivity-test MCP tool. Exposed over the in-process HTTP transport at /mcp.
/// MCP normalizes the method name to snake_case, so this is invoked as <c>echo</c>.
/// </summary>
[McpServerToolType]
public class EchoTool
{
    [McpServerTool]
    [Description("Echoes the provided message back. Use this to test server connectivity.")]
    public static string Echo([Description("The message to echo.")] string message)
    {
        return $"Echo: {message}";
    }
}
