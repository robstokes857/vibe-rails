using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.DB;
using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public static class GitPreflightServiceCollectionExtensions
{
    /// <summary>Registers snapshot readers without starting preflight, Jobs or Board services.</summary>
    public static IServiceCollection AddGitSnapshots(this IServiceCollection services)
    {
        services.TryAddSingleton<GitStagedSnapshotProvider>();
        services.TryAddSingleton<IGitStagedSnapshotProvider>(provider =>
            provider.GetRequiredService<GitStagedSnapshotProvider>());
        services.TryAddSingleton<IGitWorkingTreeSnapshotProvider>(provider =>
            provider.GetRequiredService<GitStagedSnapshotProvider>());
        return services;
    }

    public static IServiceCollection AddGitPreflight(this IServiceCollection services)
    {
        services.AddSingleton<IVcaHookValidationService, VcaRulesHookValidationService>();
        services.AddGitSnapshots();
        services.AddSingleton<VibeRails.Services.Board.BoardCheckService>();
        services.AddSingleton<IGitPreflightStep, VcaPreflightStep>();
        services.AddSingleton<IGitPreflightStep, MintLintPreflightStep>();
        services.AddSingleton<IJobStoreAccessor, ServiceProviderJobStoreAccessor>();
        services.AddSingleton<IGitPreflightStep, AutomatedWorkflowsPreflightStep>();
        services.AddSingleton<IGitPreflightPipeline, GitPreflightPipeline>();
        return services;
    }
}

public interface IJobStoreAccessor
{
    IJobStore GetRequiredStore();
}

internal sealed class ServiceProviderJobStoreAccessor(IServiceProvider services) : IJobStoreAccessor
{
    public IJobStore GetRequiredStore() => services.GetRequiredService<IJobStore>();
}
