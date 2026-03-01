using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Messaging;
using VibeRails.Services.Terminal;
using VibeRails.Services.Tracing;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using VibeRails.Utils;

namespace VibeRails
{
    public static class MapRegisterServices
    {
        public static void Register(IServiceCollection serviceCollection)
        {
            serviceCollection.AddScoped<IFileService, FileService>();
            serviceCollection.AddScoped<IDbService, DbService>();
            serviceCollection.AddScoped<IRepository>(sp =>
            {
                var connectionString = $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
                return new Repository(connectionString);
            });
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
            serviceCollection.AddScoped<ITerminalIoObserver, TraceObserver>();
            serviceCollection.AddScoped<ITerminalIoObserverService, TerminalIoObserverService>();
            serviceCollection.AddScoped<ITerminalSessionService, TerminalSessionService>();
            serviceCollection.AddSingleton<ITerminalTabHostService, TerminalTabHostService>();
            serviceCollection.AddSingleton<ILocalClientTracker, LocalClientTracker>();
            serviceCollection.AddHostedService<LocalClientLifecycleWatchdogService>();

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
