using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.Services.Cli;

namespace VibeRails.Services.PrReviews;

public static class PrReviewServiceCollectionExtensions
{
    /// <summary>
    /// The PR review service and its read-only GitHub client, for the root backend and the stdio
    /// MCP host alike. Storage is registered by the host (<c>AddSqliteStateStorage</c> or
    /// <c>AddSqlitePrReviewStorage</c>); both use the same dedicated <c>pr_reviews.db</c> and
    /// automatically adopt legacy history through <c>IPrReviewStore</c>.
    /// </summary>
    public static IServiceCollection AddPrReviews(this IServiceCollection services)
    {
        services.TryAddSingleton<ICliWrapper, CliWrapper>();
        services.TryAddSingleton<IGitHubPullRequestClient, GitHubPullRequestClient>();
        services.TryAddSingleton<IPrReviewRepositoryResolver, PrReviewRepositoryResolver>();
        services.TryAddScoped<PrReviewService>();
        return services;
    }
}
