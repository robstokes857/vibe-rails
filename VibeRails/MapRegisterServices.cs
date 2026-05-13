using VibeRails.Auth;
using VibeRails.Jobs;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;
using VibeRails.Services.UserInOut;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Messaging;
using VibeRails.Services.Terminal;

using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using VibeRails.Utils;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails
{
    public static class MapRegisterServices
    {
        public static void Register(IServiceCollection serviceCollection, string[]? args = null)
        {
            var isTerminalTabChildProcess = IsTerminalTabChildProcess(args ?? Environment.GetCommandLineArgs());

            serviceCollection.AddHttpClient<ISummaryService, SummaryService>(
                x =>
                {
                    x.BaseAddress = new Uri("https://viberails.ai");
                });

            serviceCollection.AddScoped<IFileService, FileService>();

            // BERT (V2) — write path, read path, and search strategies are registered separately.
            serviceCollection.AddSingleton<IBertSettings, BertV2BgeSmallEnSettings>();
            serviceCollection.AddSingleton<IBertV2BgeEmbedder>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2BgeEmbedder(settings.ModelPath, settings.VocabPath);
            });
            serviceCollection.AddSingleton<IBertV2VectorStore>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2VectorStore(Path.Combine(settings.DataDirectory, BertSearchSchema.DatabaseFileName));
            });
            serviceCollection.AddSingleton<IBertV2SessionVectorStore>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2SessionVectorStore(Path.Combine(settings.DataDirectory, BertSearchSchema.DatabaseFileName));
            });
            serviceCollection.AddSingleton<IBertV2InputService, BertV2InputService>();
            serviceCollection.AddSingleton<IBertV2SessionEmbeddingService, BertV2SessionEmbeddingService>();
            serviceCollection.AddScoped<IBertV2InputDBService, BertV2InputDBService>();
            serviceCollection.AddSingleton<IBertSearchDbService, BertSearchDbService>();
            serviceCollection.AddSingleton<IBertDocumentResponseMapper, BertDocumentResponseMapper>();
            serviceCollection.AddSingleton<IBertCaptureQueryService, BertCaptureQueryService>();
            serviceCollection.AddSingleton<IBertSearchStrategy, SemanticBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy, TextBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy, SemanticSessionBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy, TextSessionBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchServiceV2, BertSearchServiceV2>();
            serviceCollection.AddSingleton<IUnifiedSearchService, UnifiedSearchService>();

            serviceCollection.AddSingleton<IGitDiffCaptureService, GitDiffCaptureService>();
            serviceCollection.AddSingleton<ILlmParser, LlmParser>();
            serviceCollection.AddScoped<IRepository>(sp =>
            {
                var connectionString = $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
                var gitDiff = sp.GetService<IGitDiffCaptureService>();
                var logger = sp.GetService<ILogger<Repository>>();
                return new Repository(connectionString, gitDiff, logger);
            });
            serviceCollection.AddScoped<IGetUserText, GetUserText>();
            serviceCollection.AddScoped<IProjectCache, ProjectCache>();
            serviceCollection.AddScoped<IChatHistoryService, ChatHistoryService>();
            serviceCollection.AddSingleton<ISessionOutputParser, SessionParseV4>();
            serviceCollection.AddScoped<IGitService, GitService>();

            // Rules and Agent File services
            serviceCollection.AddScoped<IRulesService, RulesService>();
            serviceCollection.AddScoped<IAgentFileService, AgentFileService>();

            // VCA Validation services (for git hooks)
            serviceCollection.AddScoped<IRuleValidationService, RuleValidationService>();
            serviceCollection.AddScoped<IHookInstallationService, HookInstallationService>();

            // NEW VCA Validation Architecture (modular)
            // Infrastructure services
            serviceCollection.AddScoped<IFileClassifier, FileClassifier>();
            serviceCollection.AddScoped<IFileReader, FileReader>();
            serviceCollection.AddScoped<IPathNormalizer, PathNormalizer>();

            // Core VCA services
            serviceCollection.AddScoped<IFileAndRuleParser, FileAndRuleParser>();
            serviceCollection.AddScoped<IValidatorList, ValidatorList>();
            serviceCollection.AddScoped<VibeRails.Services.VCA.ValidationService>();

            // Individual validators
            serviceCollection.AddScoped<LogAllFileChangesValidator>();
            serviceCollection.AddScoped<PackageChangeValidator>();

            // Shell service (cross-platform process runner)
            serviceCollection.AddScoped<IShellService, ShellService>();

            // LLM CLI Environment services
            serviceCollection.AddScoped<IClaudeLlmCliEnvironment, ClaudeLlmCliEnvironment>();
            serviceCollection.AddScoped<ICodexLlmCliEnvironment, CodexLlmCliEnvironment>();
            serviceCollection.AddScoped<IGeminiLlmCliEnvironment, GeminiLlmCliEnvironment>();
            serviceCollection.AddScoped<ICopilotLlmCliEnvironment, CopilotLlmCliEnvironment>();
            serviceCollection.AddScoped<LlmCliEnvironmentService>();

            // Sandbox service
            serviceCollection.AddScoped<ISandboxService, SandboxService>();

            // LLM CLI Launcher services
            serviceCollection.AddScoped<IClaudeLlmCliLauncher, ClaudeLlmCliLauncher>();
            serviceCollection.AddScoped<ICodexLlmCliLauncher, CodexLlmCliLauncher>();
            serviceCollection.AddScoped<IGeminiLlmCliLauncher, GeminiLlmCliLauncher>();
            serviceCollection.AddScoped<ICopilotLlmCliLauncher, CopilotLlmCliLauncher>();
            serviceCollection.AddScoped<ILaunchLLMService, LaunchLLMService>();

            // MCP Services
            serviceCollection.AddSingleton(CreateMcpSettings());

            // Claude Agent Sync Service (syncs CLAUDE.md to AGENTS.md on session lifecycle)
            serviceCollection.AddSingleton<IClaudeAgentSyncService, ClaudeAgentSyncService>();

            // Terminal Session Service (scoped to work with other scoped services)
            serviceCollection.AddScoped<ITerminalIoObserver, MyTerminalObserver>();
            serviceCollection.AddScoped<ITerminalIoObserver, SessionStateEventObserver>();
            serviceCollection.AddScoped<ITerminalIoObserver, GitDiffIdleCaptureObserver>();
            serviceCollection.AddSingleton<ITerminalIoObserver, WaitingForUserInputObserver>();

#if DEBUG
            serviceCollection.AddScoped<ITerminalIoObserver, DebugWebSocketEventObserver>();
#endif
            serviceCollection.AddScoped<ITerminalIoObserverService, TerminalIoObserverService>();
            serviceCollection.AddScoped<ITerminalStateService, TerminalStateService>();
            serviceCollection.AddScoped<ICommandService, CommandService>();
            serviceCollection.AddScoped<TerminalRunner>();
            serviceCollection.AddScoped<ITerminalSessionService, TerminalSessionService>();
            serviceCollection.AddSingleton<ITerminalTabHostService, TerminalTabHostService>();
            serviceCollection.AddSingleton<ILocalClientTracker, LocalClientTracker>();
            serviceCollection.AddHostedService<LocalClientLifecycleWatchdogService>();

            // System resource monitor — injectable as ISystemResourceService, also runs as a hosted service.
            serviceCollection.AddSingleton<SystemResourceService>();
            serviceCollection.AddSingleton<ISystemResourceService>(sp => sp.GetRequiredService<SystemResourceService>());
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<SystemResourceService>());

            if (isTerminalTabChildProcess)
            {
                Serilog.Log.Information(
                    "[Startup] Terminal tab child detected; global maintenance jobs disabled. processId={ProcessId}",
                    Environment.ProcessId);
                // Child-only: exit when the root backend is gone so an ungraceful root
                // crash doesn't leave orphan tab processes running.
                serviceCollection.AddHostedService<ChildParentWatchdogService>();
            }
            else
            {
                serviceCollection.AddHostedService<UpdateCheckJob>();
                serviceCollection.AddHostedService<StaleSessionCleanupJob>();
                serviceCollection.AddHostedService<ProjectCacheRefreshJob>();
                serviceCollection.AddHostedService<BertEmbeddingBackfillJob>();
                serviceCollection.AddHostedService<SessionAggregateEmbeddingBackfillJob>();
            }

            serviceCollection.AddScoped<ISessionTranscriptService, SessionTranscriptService>();
            serviceCollection.AddScoped<ISessionResumeService, SessionResumeService>();

#if DEBUG
            // Debug event bus — fire-and-forget publish to connected WebSocket viewers
            serviceCollection.AddSingleton<DebugEventBus>();
            serviceCollection.AddSingleton<DebugEventWebSocketHandler>();
#endif
            serviceCollection.AddSingleton<IAppEventBus, AppEventBus>();
            serviceCollection.AddSingleton<AppEventWebSocketHandler>();

            // Remote State Service (for terminal session remote registration)
            serviceCollection.AddHttpClient<IRemoteStateService, RemoteStateService>();

            // Update Service (singleton with HttpClient)
            serviceCollection.AddHttpClient<UpdateService>();

            // WebSocket Messaging Client (singleton - auto-reconnects, URL from appsettings.json)
            serviceCollection.AddSingleton<MessagingClient>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var frontendUrl = configuration["VibeRails:FrontendUrl"] ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json");
                return new MessagingClient(frontendUrl);
            });

            // Message signature validator — load public cert once, scoped service
            //var publicCert = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine("Certs", "public.pfx"), null);
            //serviceCollection.AddScoped(_ => new MessageSignatureValidator(publicCert));

            // Authentication service (singleton - one token per instance)
            serviceCollection.AddSingleton<IAuthService, AuthService>();
            serviceCollection.AddSingleton<IAuthBootstrapService, AuthBootstrapService>();
            serviceCollection.AddSingleton<IUnconsumedBootstrapCodeShutdownWatchdog, UnconsumedBootstrapCodeShutdownWatchdog>();
        }

        private static bool IsTerminalTabChildProcess(IEnumerable<string> args)
        {
            return args.Any(static arg =>
                arg.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase));
        }

        private static McpSettings CreateMcpSettings()
        {
            // Search for MCP server executable in common locations
            var possiblePaths = new[]
            {
                // Deployed alongside main app
                Path.Combine(AppContext.BaseDirectory, "MCP_Server", "MCP_Server.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "MCP_Server", "MCP_Server.exe"),
                // Development paths
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MCP_Server", "bin", "Debug", "net10.0", "MCP_Server.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MCP_Server", "bin", "Release", "net10.0", "MCP_Server.exe"),
            };

            var serverPath = possiblePaths
                .Select(Path.GetFullPath)
                .FirstOrDefault(File.Exists) ?? "";

            return new McpSettings(serverPath);
        }
    }
}


