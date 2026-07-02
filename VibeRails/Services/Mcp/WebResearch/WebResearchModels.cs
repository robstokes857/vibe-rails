namespace VibeRails.Services.Mcp.WebResearch;

public sealed record WebSearchResult(
    string Title,
    string Url,
    string Snippet);

public sealed record WebPageFetchResult(
    string Url,
    string Title,
    string Text,
    IReadOnlyList<WebSearchResult> Links);

