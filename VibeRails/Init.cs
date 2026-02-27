using System.Threading.Tasks;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using System.Security.Cryptography.X509Certificates;
using VibeRails.Services.Messaging;
using VibeRails.Services.Mcp;
using VibeRails.Services.Terminal;
using VibeRails.Services.Tracing;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using VibeRails.Utils;

namespace VibeRails
{
    public static class Init
    {
        public static void RegisterServices(IServiceCollection serviceCollection)
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

        public static Task StartUpChecks(IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();

            // Initialize version info and app settings
            VersionInfo.Initialize(configuration);
            InitAppSettings(configuration);

            using var scope = serviceProvider.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            fileService.InitGlobalSave();

            // Initialize SQLite database
            var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();
            dbService.InitializeDatabase();

            var isLocal = fileService.TryGetProjectRootPath();
            ParserConfigs.SetLocalContext(isLocal.inGet);
            ParserConfigs.SetRootPath(isLocal.projectRoot);

            //Launch for global context only
            if (!isLocal.inGet)
            {
                return Task.CompletedTask;
            }

            fileService.InitLocal(isLocal.projectRoot);

            // TODO: Re-enable hook auto-install once Windows security blocking is resolved (upcoming story)
            // await TryInstallPreCommitHookAsync(scope.ServiceProvider, isLocal.projectRoot);
            return Task.CompletedTask;
        }

        public static void InitAppSettings(IConfiguration configuration)
        {
            // Load FrontendUrl from appsettings.json
            var frontendUrl = configuration["VibeRails:FrontendUrl"] ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json");
            ParserConfigs.SetFrontendUrl(frontendUrl);

            // Load ApiKey and RemoteAccess from settings.json in ~/.vibe_rails/
            var settings = Config.Load();
            ParserConfigs.SetRemoteAccess(settings.RemoteAccess);
            ParserConfigs.SetApiKey(settings.ApiKey);
        }
    }
}
