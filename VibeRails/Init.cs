using System.Threading.Tasks;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Mcp;
using VibeRails.Services.Terminal;
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
                var connectionString = $"Data Source={Configs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
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
            serviceCollection.AddScoped<LlmCliEnvironmentService>();

            // LLM CLI Launcher services
            serviceCollection.AddScoped<IClaudeLlmCliLauncher, ClaudeLlmCliLauncher>();
            serviceCollection.AddScoped<ICodexLlmCliLauncher, CodexLlmCliLauncher>();
            serviceCollection.AddScoped<IGeminiLlmCliLauncher, GeminiLlmCliLauncher>();
            serviceCollection.AddScoped<ILaunchLLMService, LaunchLLMService>();

            // MCP Services
            serviceCollection.AddSingleton(CreateMcpSettings());

            // Claude Agent Sync Service (syncs CLAUDE.md to AGENTS.md on session lifecycle)
            serviceCollection.AddSingleton<IClaudeAgentSyncService, ClaudeAgentSyncService>();

            // Terminal Session Service (scoped to work with other scoped services)
            serviceCollection.AddScoped<ITerminalSessionService, TerminalSessionService>();

            // Update Service (singleton with HttpClient)
            serviceCollection.AddHttpClient<UpdateService>();
            serviceCollection.AddScoped<UpdateInstaller>();
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

        public static async Task StartUpChecks(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            fileService.InitGlobalSave();

            // Initialize SQLite database
            var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();
            dbService.InitializeDatabase();

            var isLocal = fileService.TryGetProjectRootPath();
            Configs.SetLocalContext(isLocal.inGet);
            Configs.SetRootPath(isLocal.projectRoot);

            //Launch for global context only
            if (!isLocal.inGet)
            {
                return;
            }

            fileService.InitLocal(isLocal.projectRoot);

            // Auto-install pre-commit hook (silent failure)
            await TryInstallPreCommitHookAsync(scope.ServiceProvider, isLocal.projectRoot);
        }

        private static async Task TryInstallPreCommitHookAsync(IServiceProvider services, string projectRoot)
        {
            try
            {
                // Load configuration to check if auto-install is enabled
                var configuration = LoadAppConfiguration();
                if (!configuration.Hooks.InstallOnStartup)
                {
                    return;
                }

                var hookService = services.GetRequiredService<IHookInstallationService>();

                // Only install if not already installed
                if (!hookService.IsHookInstalled(projectRoot))
                {
                    var result = await hookService.InstallPreCommitHookAsync(projectRoot, CancellationToken.None);
                    if (result.Success)
                    {
                        Console.WriteLine("VCA pre-commit hook installed automatically.");
                    }
                    else
                    {
                        Console.WriteLine($"Note: Could not auto-install pre-commit hook: {result.ErrorMessage}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Silent failure - just log to console
                Console.WriteLine($"Note: Could not auto-install pre-commit hook: {ex.Message}");
            }
        }

        private static AppConfiguration LoadAppConfiguration()
        {
            try
            {
                var configPath = Path.Combine(AppContext.BaseDirectory, "app_config.json");
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    return System.Text.Json.JsonSerializer.Deserialize<AppConfiguration>(json,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new AppConfiguration();
                }
            }
            catch
            {
                // If config fails to load, use defaults
            }

            return new AppConfiguration();
        }
    }
}
