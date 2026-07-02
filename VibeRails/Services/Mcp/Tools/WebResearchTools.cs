using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.Mcp.WebResearch;

namespace VibeRails.Services.Mcp.Tools;

[McpServerToolType]
public sealed class WebResearchTools
{
    private readonly IWebResearchService _web;

    public WebResearchTools(IWebResearchService web)
    {
        _web = web;
    }

    [McpServerTool]
    [Description("Search the web through VibeRails' backend HttpClientFactory pipeline and a no-key HTML search provider. Result URLs are not security-filtered; callers are trusted local agents.")]
    public async Task<string> WebSearch(
        [Description("Search query.")] string query,
        [Description("Maximum results to return, 1-10.")] int maxResults = 5)
    {
        try
        {
            var results = await _web.SearchAsync(query, maxResults);
            if (results.Count == 0)
            {
                return $"No web results for \"{query}\".";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Top {results.Count} web result(s) for \"{query}\":");
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                sb.AppendLine($"{i + 1}. {result.Title}");
                sb.AppendLine($"   {result.Url}");
                if (!string.IsNullOrWhiteSpace(result.Snippet))
                {
                    sb.AppendLine($"   {result.Snippet}");
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Fetch any absolute HTTP/HTTPS URL as the local VibeRails process and return cleaned title, text, and a small link list. No localhost/private-network blocking is applied; use only from trusted local agents.")]
    public async Task<string> WebFetch(
        [Description("Absolute public HTTP or HTTPS URL to fetch.")] string url,
        [Description("Maximum text characters to return, 1000-50000.")] int maxChars = 12000)
    {
        try
        {
            var page = await _web.FetchAsync(url, maxChars);
            var sb = new StringBuilder();
            sb.AppendLine($"URL: {page.Url}");
            if (!string.IsNullOrWhiteSpace(page.Title))
            {
                sb.AppendLine($"Title: {page.Title}");
            }

            sb.AppendLine();
            sb.AppendLine("Text:");
            sb.AppendLine(page.Text);

            if (page.Links.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Links:");
                foreach (var link in page.Links)
                {
                    sb.AppendLine($"- {link.Title}: {link.Url}");
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"FAIL: {ex.Message}";
        }
    }
}
