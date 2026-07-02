using Microsoft.Extensions.DependencyInjection;

namespace VibeRails.Services.Mcp.WebResearch;

public static class WebResearchServiceCollectionExtensions
{
    /// <summary>
    /// Single source of truth for the web-research <see cref="HttpClient"/> so the in-process
    /// HTTP transport (MapRegisterServices) and the stdio transport (McpStdioHost) stay in
    /// lockstep — same User-Agent and same timeout. Keeping it here avoids the two registrations
    /// silently diverging (which previously left the HTTP path on the 100s default timeout).
    /// </summary>
    public static IHttpClientBuilder AddWebResearchClient(this IServiceCollection services) =>
        services.AddHttpClient<IWebResearchService, WebResearchService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VibeRails-MCP-WebResearch/1.0");
        });
}
