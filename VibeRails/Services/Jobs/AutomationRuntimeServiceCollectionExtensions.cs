using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.Data.Sqlite;
using VibeRails.Interfaces;
using VibeRails.Services.Cli;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// The shared single-launch scheduling and native-terminal launch graph. The dashboard and terminal
/// process hosts use this registration so provider and workspace preparation stays consistent.
/// </summary>
public static class AutomationRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAutomationRuntime(
        this IServiceCollection services,
        bool hostScheduler)
    {
        services.AddSqliteStateStorage(_ => new SqliteStoragePaths(ParserConfigs.GetStatePath()),
            consumeBoardEvents: false);
        services.TryAddScoped<VibeRails.Services.Board.ReviewRoutingService>();
        services.TryAddSingleton<IJobExecutableResolver, JobExecutableResolver>();
        services.TryAddSingleton<IAutomationScriptService, AutomationScriptService>();
        // Shared process runner for repository-script Actions. It is
        // stateless, so each process host uses one singleton.
        services.TryAddSingleton<ICliWrapper, CliWrapper>();
        services.TryAddSingleton<IJobProcessLauncher, JobProcessLauncher>();

        services.TryAddScoped<UserInputRecordingService>();
        services.TryAddScoped<ISandboxService, SandboxService>();
        services.TryAddScoped<IRunWorkspaceService, RunWorkspaceService>();

        services.TryAddScoped<IClaudeLlmCliLauncher, ClaudeLlmCliLauncher>();
        services.TryAddScoped<ICodexLlmCliLauncher, CodexLlmCliLauncher>();
        services.TryAddScoped<IAntigravityLlmCliLauncher, AntigravityLlmCliLauncher>();
        services.TryAddScoped<ICopilotLlmCliLauncher, CopilotLlmCliLauncher>();
        services.TryAddScoped<IOpencodeLlmCliLauncher, OpencodeLlmCliLauncher>();
        services.TryAddScoped<IGrokLlmCliLauncher, GrokLlmCliLauncher>();
        services.TryAddScoped<ILaunchLLMService, LaunchLLMService>();
        services.TryAddScoped<IEnvironmentLaunchService, EnvironmentLaunchService>();
        if (hostScheduler)
        {
            services.TryAddScoped<VibeRails.Services.Launches.LaunchService>();
            services.TryAddScoped<VibeRails.Services.Launches.LaunchDispatcher>();
            services.AddHostedService<VibeRails.Services.Launches.LaunchScheduler>();
        }

        return services;
    }
}
