using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.Board.Tracker;

namespace VibeRails.Services.AzureDevOps;

/// <summary>
/// The Azure DevOps personal access tokens for this machine, one per connection id, in
/// <c>ado-tokens.json</c>. See <see cref="ITrackerTokenStore"/> for how a token is kept.
/// </summary>
public interface IAdoSecretStore : ITrackerTokenStore;

public sealed class AdoSecretStore : TrackerTokenStore, IAdoSecretStore
{
    public const string FileName = "ado-tokens.json";

    public AdoSecretStore() : this(DefaultPath()) { }

    public AdoSecretStore(string path) : base(path) { }

    public static string DefaultPath() => PathInDataDirectory(FileName);
}

/// <summary>The Azure DevOps <see cref="TrackerPullLock"/>: <c>.ado-pull.lock</c> beside the state database.</summary>
public sealed class AdoPullLock(string path) : TrackerPullLock(path)
{
    public const string FileName = ".ado-pull.lock";

    public static AdoPullLock BesideStateDatabase() => new(PathBesideStateDatabase(FileName));
}

/// <summary>Shared outbound Azure DevOps composition for the root and the local stdio MCP host.</summary>
public static class AdoServiceRegistration
{
    /// <summary>Registers the Azure DevOps client, token store, lock and commenting without starting a scheduler or listener.</summary>
    public static IServiceCollection AddAdoClients(this IServiceCollection services)
    {
        services.AddHttpClient(AzureDevOpsClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<IAdoSecretStore, AdoSecretStore>();
        services.AddSingleton(sp => new AzureDevOpsClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(AzureDevOpsClient.HttpClientName)));
        services.AddSingleton<IAzureDevOpsClient>(sp => sp.GetRequiredService<AzureDevOpsClient>());
        services.AddSingleton<IAdoCommentClient>(sp => sp.GetRequiredService<AzureDevOpsClient>());
        services.AddSingleton(_ => AdoPullLock.BesideStateDatabase());
        services.AddScoped<AdoCommentService>();
        return services;
    }
}
