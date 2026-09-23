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
/// The shared Automation scheduling and native-terminal launch graph. The dashboard and terminal
/// process hosts use this registration so Environment/workspace behavior stays consistent.
/// </summary>
public static class AutomationRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAutomationRuntime(
        this IServiceCollection services,
        bool hostScheduler)
    {
        services.AddSqliteStateStorage(_ => new SqliteStoragePaths(ParserConfigs.GetStatePath()),
            consumeBoardEvents: hostScheduler);
        services.TryAddSingleton<IJobExecutableResolver, JobExecutableResolver>();
        services.TryAddSingleton<IAutomationScriptService, AutomationScriptService>();
        services.TryAddSingleton<IJobProcessLauncher, JobProcessLauncher>();
        // Shared process runner for repository-script actions and Environment Steps. It is
        // stateless, so each process host uses one singleton.
        services.TryAddSingleton<ICliWrapper, CliWrapper>();

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
        services.TryAddScoped<IJobLaunchService, JobLaunchService>();
        if (hostScheduler)
            services.TryAddScoped<IJobTerminalTabLauncher, JobTerminalTabLauncher>();

        services.TryAddSingleton<JobSchedulerHealth>();
        services.TryAddSingleton<JobSchedulerHostedService>();
        services.TryAddSingleton<IJobScheduler>(serviceProvider =>
            serviceProvider.GetRequiredService<JobSchedulerHostedService>());

        if (hostScheduler)
        {
            services.AddHostedService(serviceProvider =>
                serviceProvider.GetRequiredService<JobSchedulerHostedService>());
        }

        return services;
    }
}
