namespace VibeRails.Services.Mcp.WebResearch;

public interface IWebResearchService
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);
    Task<WebPageFetchResult> FetchAsync(string url, int maxChars = 12000, CancellationToken cancellationToken = default);
}

