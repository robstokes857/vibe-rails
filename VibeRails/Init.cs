using System.Threading.Tasks;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Mcp;
using VibeRails.Services.Terminal;
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
                var hookService = services.GetRequiredService<IHookInstallationService>();

                // Only install if not already installed
                if (!hookService.IsHookInstalled(projectRoot))
                {
                    var success = await hookService.InstallPreCommitHookAsync(projectRoot, CancellationToken.None);
                    if (success)
                    {
                        Console.WriteLine("VCA pre-commit hook installed automatically.");
                    }
                }
            }
            catch (Exception ex)
            {
                // Silent failure - just log to console
                Console.WriteLine($"Note: Could not auto-install pre-commit hook: {ex.Message}");
            }
        }
    }
}
