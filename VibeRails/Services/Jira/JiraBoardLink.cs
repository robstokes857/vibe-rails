using System.Text.RegularExpressions;

namespace VibeRails.Services.Jira;

/// <summary>
/// What a pasted Jira Cloud link points at (VIBE-102). Pure: no network call. The origin follows
/// <see cref="JiraSite"/>'s rule (https, a host, no credentials). A board id is digits only and a
/// project key is <c>[A-Z][A-Z0-9_]+</c> before either reaches a REST path or a JQL string. A
/// board link's query string (<c>filter=</c>, <c>groupBy=</c>, <c>atlOrigin=</c>) is display state
/// and link tracking, so it is ignored.
/// </summary>
/// <param name="Link">What is saved and shown again: the origin and path, plus only the query
/// parameter that chose the issues (<c>rapidView</c>, <c>jql</c> or <c>filter</c>).</param>
public sealed partial record JiraBoardLink(
    string Origin, string Link, string? BoardId, string? ProjectKey, string? Jql, string? FilterId)
{
    public const int MaxLinkLength = 4000;

    /// <summary>
    /// The JQL that selects the same issues without the board: the link's own JQL, its saved
    /// filter, or its project. Written to the connection's Jql so an older VibeRails, which only
    /// reads Jql, keeps pulling. Null for a legacy board link that names no project.
    /// </summary>
    public string? SourceJql =>
        Jql ?? (FilterId is not null ? $"filter = {FilterId}" : ProjectJql(ProjectKey));

    public static string? ProjectJql(string? projectKey) =>
        projectKey is not null && ProjectKeyPattern().IsMatch(projectKey) ? $"project = \"{projectKey}\"" : null;

    public static bool IsBoardId(string? value) => value is not null && BoardIdPattern().IsMatch(value);

    public static JiraBoardLink Parse(string? url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length > MaxLinkLength
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new JiraConfigException(
                "Paste the https link of a Jira Cloud board, such as https://your-site.atlassian.net/jira/software/projects/KEY/boards/1.");

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 0)
            throw new JiraConfigException("Paste a board link, not just the site. Open the board in Jira and copy the address bar.");
        var query = ReadQuery(uri.Query);
        var path = origin + uri.AbsolutePath.TrimEnd('/');

        // /secure/RapidBoard.jspa?rapidView=ID[&projectKey=KEY]: the legacy board link.
        if (segments.Length == 2
            && segments[0].Equals("secure", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("RapidBoard.jspa", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsBoardId(query.GetValueOrDefault("rapidView")))
                throw new JiraConfigException("That legacy board link has no board number (rapidView). Open the board in Jira and copy the address bar.");
            var rapidView = query["rapidView"];
            return new JiraBoardLink(origin, path + "?rapidView=" + rapidView, rapidView,
                ProjectKeyOrNull(query.GetValueOrDefault("projectKey")), null, null);
        }

        // /browse/KEY-123 or /browse/KEY: the issue's or project's project.
        if (segments[0].Equals("browse", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
        {
            var match = IssueKeyPattern().Match(segments[1].ToUpperInvariant());
            var key = match.Success ? match.Groups[1].Value : ProjectKeyOrNull(segments[1]);
            if (key is null)
                throw Unrecognized();
            return new JiraBoardLink(origin, origin + "/browse/" + key, null, key, null, null);
        }

        // An issue search: /issues/?jql=…, /issues/?filter=ID, /jira/…/issues?jql=….
        var issueSearch = segments[^1].Equals("issues", StringComparison.OrdinalIgnoreCase);
        if (issueSearch && query.GetValueOrDefault("jql") is { } jqlText && jqlText.Trim().Length > 0)
        {
            var jql = jqlText.Trim();
            return new JiraBoardLink(origin, path + "?jql=" + Uri.EscapeDataString(jql), null, ProjectKeyFromPath(segments), jql, null);
        }
        if (issueSearch && query.GetValueOrDefault("filter") is { } filter && FilterIdPattern().IsMatch(filter))
            return new JiraBoardLink(origin, path + "?filter=" + filter, null, ProjectKeyFromPath(segments), null, filter);

        // /jira/software/projects/KEY/boards/ID[/backlog…], /jira/software/c/projects/KEY/boards/ID,
        // and any other /jira/… path that names a board or a project.
        if (segments[0].Equals("jira", StringComparison.OrdinalIgnoreCase))
        {
            var projectKey = ProjectKeyFromPath(segments);
            var boards = Array.FindIndex(segments, segment => segment.Equals("boards", StringComparison.OrdinalIgnoreCase));
            if (boards >= 0 && (boards + 1 >= segments.Length || !IsBoardId(segments[boards + 1])))
                throw new JiraConfigException("That board link has no board number after /boards/. Open the board in Jira and copy the address bar.");
            if (boards >= 0)
            {
                var boardPath = origin + "/" + string.Join('/', segments.Take(boards + 2).Select(Uri.EscapeDataString));
                return new JiraBoardLink(origin, boardPath, segments[boards + 1], projectKey, null, null);
            }
            if (projectKey is not null)
                return new JiraBoardLink(origin, path, null, projectKey, null, null);
        }

        throw Unrecognized();
    }

    private static JiraConfigException Unrecognized() =>
        new("That link isn't a Jira board, project or issue search. Open the board in Jira and copy the address bar.");

    private static string? ProjectKeyFromPath(string[] segments)
    {
        var projects = Array.FindIndex(segments, segment => segment.Equals("projects", StringComparison.OrdinalIgnoreCase));
        return projects >= 0 && projects + 1 < segments.Length ? ProjectKeyOrNull(segments[projects + 1]) : null;
    }

    private static string? ProjectKeyOrNull(string? value)
    {
        var key = value?.Trim().ToUpperInvariant();
        return key is not null && ProjectKeyPattern().IsMatch(key) ? key : null;
    }

    private static Dictionary<string, string> ReadQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            values.TryAdd(name, value);
        }
        return values;
    }

    [GeneratedRegex(@"^[0-9]{1,18}$")]
    private static partial Regex BoardIdPattern();

    [GeneratedRegex(@"^[0-9]{1,18}$")]
    private static partial Regex FilterIdPattern();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{1,49}$")]
    private static partial Regex ProjectKeyPattern();

    [GeneratedRegex(@"^([A-Z][A-Z0-9_]{1,49})-[0-9]+$")]
    private static partial Regex IssueKeyPattern();
}
