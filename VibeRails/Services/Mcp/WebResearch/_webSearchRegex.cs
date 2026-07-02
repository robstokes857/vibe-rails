
using System.Text.RegularExpressions;

namespace VibeRails.Services.Mcp.WebResearch;
public partial class WebResearchService : IWebResearchService
{
     private static readonly Regex TitleRegex = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptStyleRegex = new(
        @"<(script|style|svg|noscript)[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);
    private static readonly Regex AnchorRegex = new(
        @"<a\b[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DuckResultRegex = new(
        @"<a\b[^>]*class\s*=\s*[""'][^""']*result__a[^""']*[""'][^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DuckSnippetRegex = new(
        @"<a\b[^>]*class\s*=\s*[""'][^""']*result__snippet[^""']*[""'][^>]*>(?<snippet>.*?)</a>|<div\b[^>]*class\s*=\s*[""'][^""']*result__snippet[^""']*[""'][^>]*>(?<snippet>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
}