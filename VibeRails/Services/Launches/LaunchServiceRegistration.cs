using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.Data.Sqlite;
using VibeRails.Launches;
using VibeRails.Services.Jobs;

namespace VibeRails.Services.Launches;

public static class LaunchServiceRegistration
{
    /// <summary>Shared admission services; never starts a scheduler or adds a listener.</summary>
    public static IServiceCollection AddLaunchDefinitions(this IServiceCollection services, Func<string>? statePath = null)
    {
        if (statePath is not null) services.TryAddSingleton<ILaunchStore>(_ => SqliteStorage.CreateLaunchStore(statePath()));
        services.TryAddSingleton<IJobExecutableResolver, JobExecutableResolver>();
        services.TryAddScoped<IAutomationScriptService, AutomationScriptService>();
        services.TryAddScoped<LaunchService>();
        services.TryAddScoped<LaunchAttachments>();
        services.TryAddScoped<BoardLaunchWorkflow>();
        services.TryAddScoped<Mcp.Tools.LaunchTool>();
        return services;
    }
}
