namespace VibeRails.Routes;

public static class RouteExtensions
{
    public static void MapApiEndpoints(this WebApplication app, string launchDirectory)
    {
        AuthRoutes.Map(app);  // Must be first - no auth required for this endpoint
        ProjectRoutes.Map(app, launchDirectory);
        EnvironmentRoutes.Map(app);
        CliLaunchRoutes.Map(app, launchDirectory);
        SessionRoutes.Map(app);
        ChatHistoryRoutes.Map(app);
        TerminalRoutes.Map(app, launchDirectory);
        TerminalTabsRoutes.Map(app);
        SandboxRoutes.Map(app, launchDirectory);
        McpRoutes.Map(app);
        AgentRoutes.Map(app);
        RulesRoutes.Map(app);
        HookRoutes.Map(app);
        LlmSettingsRoutes.Map(app);
        ClaudePlanRoutes.Map(app);
        SwarmRoutes.Map(app);
        UpdateRoutes.Map(app);
        AppSettingsRoutes.Map(app);
        PinRoutes.Map(app);
        LifecycleRoutes.Map(app);
        TraceRoutes.Map(app);
        AppNotificationRoutes.Map(app);
        BertRoutes.Map(app);

        // Debug routes - only map if in debug mode
#if DEBUG
        EventRoutes.Map(app);
#endif
    }
}

