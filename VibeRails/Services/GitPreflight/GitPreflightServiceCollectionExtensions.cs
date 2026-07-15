using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public static class GitPreflightServiceCollectionExtensions
{
    public static IServiceCollection AddGitPreflight(this IServiceCollection services)
    {
        services.AddSingleton<IVcaHookValidationService, VcaRulesHookValidationService>();
        services.AddSingleton<GitStagedSnapshotProvider>();
        services.AddSingleton<IGitStagedSnapshotProvider>(provider =>
            provider.GetRequiredService<GitStagedSnapshotProvider>());
        services.AddSingleton<IGitWorkingTreeSnapshotProvider>(provider =>
            provider.GetRequiredService<GitStagedSnapshotProvider>());
        services.AddSingleton<IGitPreflightStep, VcaPreflightStep>();
        services.AddSingleton<IGitPreflightStep, MintLintPreflightStep>();
        services.AddSingleton<IGitPreflightStep, AutomatedWorkflowsPreflightStep>();
        services.AddSingleton<IGitPreflightPipeline, GitPreflightPipeline>();
        return services;
    }
}
