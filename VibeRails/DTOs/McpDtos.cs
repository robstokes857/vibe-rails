using System.Text.Json;

namespace VibeRails.DTOs;

/// <summary>
/// Information about an MCP tool.
/// </summary>
public record McpToolInfo(
    string Name,
    string Description,
    string? Title = null,
    JsonElement? InputSchema = null,
    JsonElement? ReturnSchema = null,
    string? Category = null,
    string? SourceName = null);

/// <summary>
/// Request describing an MCP server to inspect.
/// </summary>
public record McpServerTargetRequest(
    string? Endpoint = null,
    Dictionary<string, string>? Headers = null);

/// <summary>
/// Response from inspecting an MCP server.
/// </summary>
public record McpInspectResponse(
    bool Success,
    bool ServerAvailable,
    string Endpoint,
    string? Message,
    List<McpToolInfo> Tools);

/// <summary>
/// Request to call an MCP tool.
/// </summary>
public record McpToolCallRequest(
    Dictionary<string, object?> Arguments,
    string? Endpoint = null,
    Dictionary<string, string>? Headers = null);

/// <summary>
/// Response from calling an MCP tool.
/// </summary>
public record McpToolCallResponse(bool Success, string Result, string? Error = null);

/// <summary>
/// MCP server status information.
/// </summary>
public record McpStatusResponse(bool ServerAvailable, string ServerPath, string? Message = null);
