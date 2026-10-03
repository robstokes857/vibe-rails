using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using VibeRails.DTOs;
using VibeRails.Services.Mcp.Tools;

namespace VibeRails.Services.Mcp;

/// <summary>
/// The tool set both MCP transports expose (HTTP in the root backend, stdio in <see cref="McpStdioHost"/>) and the
/// JSON options they share. The SDK's own options know only protocol and primitive types, so a tool taking or
/// returning an application record (<c>save_board_handoff</c>'s <see cref="Services.Board.BoardHandoff"/>) failed
/// with "JsonTypeInfo metadata ... was not provided" the first time the tools were resolved, taking every tool
/// down with it. <see cref="SerializerOptions"/> adds this application's source-generated metadata behind the
/// SDK's, which keeps the registration Native AOT clean.
/// </summary>
public static class McpToolRegistration
{
    /// <summary>MCP defaults plus <see cref="AppJsonSerializerContext"/>, for tool schemas, arguments and results.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>Registers every VibeRails tool class with <see cref="SerializerOptions"/>. Add new tool classes here only.</summary>
    public static IMcpServerBuilder WithVibeRailsTools(this IMcpServerBuilder builder) => builder
        .WithTools<RulesTool>(SerializerOptions)
        .WithTools<SessionSearchTool>(SerializerOptions)
        .WithTools<TokenSaverTool>(SerializerOptions)
        .WithTools<BoardTool>(SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Add(AppJsonSerializerContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
