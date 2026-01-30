namespace VibeRails.DTOs;

/// <summary>
/// Configuration for locating the MCP server executable.
/// </summary>
public record McpSettings(string ServerPath = "");

/// <summary>
/// Information about an MCP tool.
/// </summary>
public record McpToolInfo(string Name, string Description);

/// <summary>
/// Request to call an MCP tool.
/// </summary>
public record McpToolCallRequest(Dictionary<string, object?> Arguments);

/// <summary>
/// Response from calling an MCP tool.
/// </summary>
public record McpToolCallResponse(bool Success, string Result, string? Error = null);

/// <summary>
/// MCP server status information.
/// </summary>
public record McpStatusResponse(bool ServerAvailable, string ServerPath, string? Message = null);
