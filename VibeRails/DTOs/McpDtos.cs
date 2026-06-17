namespace VibeRails.DTOs;

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
