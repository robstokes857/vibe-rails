using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
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
        .WithTools<AgentSessionTool>(SerializerOptions)
        .WithTools<SessionSharingTool>(SerializerOptions)
        .WithTools<BoardTool>(SerializerOptions);

    /// <summary>
    /// The root backend's Streamable HTTP transport. SDK 2.x serves every request statelessly by default, which drops the
    /// clientInfo an <c>initialize</c>-handshake client (protocol 2025-11-25 and earlier) sends only once, so its Board
    /// writes would have no agent name and be refused (VIBE-61). StatefulForInitializeClients keeps a session for those
    /// clients only; 2026-07-28 clients repeat clientInfo on every request and stay stateless. CookieAuthMiddleware gates
    /// every /mcp method with both credentials in either mode.
    /// </summary>
    public static IMcpServerBuilder WithVibeRailsHttpTransport(this IMcpServerBuilder builder) =>
        builder.WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

    /// <summary>Local control credentials must never follow redirects to another destination.</summary>
    public static IServiceCollection AddAgentSessionMcp(this IServiceCollection services)
    {
        services.AddHttpClient(AgentSessionTool.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        services.AddScoped<AgentSessionTool>();
        return services;
    }

    /// <summary>Calls only the inherited root API, without redirects, cookies or system proxies.</summary>
    public static IServiceCollection AddSessionSharingMcp(this IServiceCollection services)
    {
        services.AddHttpClient(SessionSharingTool.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false
            });
        services.AddScoped<SessionSharingTool>();
        return services;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolverChain.Add(AppJsonSerializerContext.Default);
        options.MakeReadOnly();
        return options;
    }
}
