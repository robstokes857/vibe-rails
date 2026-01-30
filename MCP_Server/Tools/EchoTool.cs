using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MCP_Server.Tools;

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
