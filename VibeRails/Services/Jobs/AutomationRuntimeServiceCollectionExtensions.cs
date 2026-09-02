using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.Cli;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// The shared Automation scheduling and native-terminal launch graph. Both the dashboard and the
/// lean VBD process host use this exact registration so Environment/workspace behavior cannot
/// drift between process roles.
/// </summary>
public static class AutomationRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAutomationRuntime(
        this IServiceCollection services,
        bool hostScheduler)
    {
        services.TryAddSingleton<IJobStore>(_ => new JobStore(
            $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared"));
        services.TryAddSingleton<IJobExecutableResolver, JobExecutableResolver>();
        services.TryAddSingleton<IAutomationScriptService, AutomationScriptService>();
        services.TryAddSingleton<IJobProcessLauncher, JobProcessLauncher>();
        // Shared process runner for repository-script actions and Environment Steps. It is
        // stateless, so the dashboard and lean Demon host both use one singleton.
        services.TryAddSingleton<ICliWrapper, CliWrapper>();

        services.TryAddScoped<IRepository>(serviceProvider =>
        {
            var connectionString =
                $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
            var gitDiff = serviceProvider.GetService<IGitDiffCaptureService>();
            var logger = serviceProvider.GetService<ILogger<Repository>>();
            return new Repository(connectionString, gitDiff, logger);
        });

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
