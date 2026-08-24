using TokenSaver;

namespace VibeRails.Routes;

public static class RouteExtensions
{
    public static void MapApiEndpoints(this WebApplication app, string launchDirectory)
    {
        // Read, not re-derived. Two route groups and the DI registration all gate on this same
        // answer, and computing it per call site is how they eventually disagree about what this
        // process is — MapRegisterServices resolves it from the argv it was handed, which is not
        // the array Environment.GetCommandLineArgs returns.
        var isActiveRootBackend =
            app.Services.GetRequiredService<ProcessRole>().IsActiveRootBackend;

        AuthRoutes.Map(app);  // Must be first - no auth required for this endpoint
        ProjectRoutes.Map(app, launchDirectory);
        // Host filesystem browsing is a root-dashboard capability. Do not expose it from the
        // short-lived backend process attached to every terminal tab or `vb --env` session.
        if (isActiveRootBackend)
            FileSystemRoutes.Map(app, launchDirectory);
        EnvironmentRoutes.Map(app);
        EnvironmentStepRoutes.Map(app);
        LlmPickerRoutes.Map(app);
        AutomationNavRoutes.Map(app);
        PythonScriptRoutes.Map(app, isActiveRootBackend);
        JobRoutes.Map(app, launchDirectory);
        CliLaunchRoutes.Map(app, launchDirectory);
        SessionRoutes.Map(app);
        ChatHistoryRoutes.Map(app);
        DebugBundleRoutes.Map(app);
        LlmProxyRoutes.Map(app);
        LlmAnthropicProxyRoutes.Map(app);
        LlmZaiProxyRoutes.Map(app);
        LlmXaiProxyRoutes.Map(app);
        LlmCliChatProxyRoutes.Map(app);
        TokenSaverPauseRoutes.Map(app);
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
        HttpRelayRoutes.Map(app);
        // Active root backend only: terminal-tab children do not serve the Settings workflow.
        // More than one root backend can coexist, so DataExportService also holds an OS-backed
        // lock beside state.db across snapshot, upload, and cleanup.
        if (isActiveRootBackend)
            DataExportRoutes.Map(app);
        PinRoutes.Map(app);
        PushRoutes.Map(app);
        LifecycleRoutes.Map(app);
        app.Services.GetRequiredService<AppEventWebSocketHandler>().MapWebSocket(app);
        BertRoutes.Map(app);
        SearchRoutes.Map(app);
    }
}

