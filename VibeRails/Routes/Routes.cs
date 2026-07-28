using TokenSaver;

namespace VibeRails.Routes;

public static class RouteExtensions
{
    public static void MapApiEndpoints(this WebApplication app, string launchDirectory)
    {
        AuthRoutes.Map(app);  // Must be first - no auth required for this endpoint
        ProjectRoutes.Map(app, launchDirectory);
        EnvironmentRoutes.Map(app);
        JobRoutes.Map(app, launchDirectory);
        CliLaunchRoutes.Map(app, launchDirectory);
        SessionRoutes.Map(app);
        ChatHistoryRoutes.Map(app);
        DebugBundleRoutes.Map(app);
        LlmProxyRoutes.Map(app);
        LlmAnthropicProxyRoutes.Map(app);
        LlmZaiProxyRoutes.Map(app);
        TokenSavingsRoutes.Map(app);
        CompressionCaptureRoutes.Map(app);
        TerminalRoutes.Map(app, launchDirectory);
        TerminalTabsRoutes.Map(app);
        AgentToolRoutes.Map(app);
        SandboxRoutes.Map(app, launchDirectory);
        McpRoutes.Map(app);
        AgentRoutes.Map(app);
        RulesRoutes.Map(app);
        HookRoutes.Map(app);
        LlmSettingsRoutes.Map(app);
        UpdateRoutes.Map(app);
        AppSettingsRoutes.Map(app);
        PinRoutes.Map(app);
        PushRoutes.Map(app);
        LifecycleRoutes.Map(app);
        app.Services.GetRequiredService<AppEventWebSocketHandler>().MapWebSocket(app);
        BertRoutes.Map(app);
        SearchRoutes.Map(app);
    }
}

