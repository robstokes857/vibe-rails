using System.Text.RegularExpressions;

namespace VibeRails.Services.AzureDevOps;

/// <summary>
/// What a pasted Azure DevOps link points at. Pure: no network call. Only Azure DevOps Services
/// on <c>https://dev.azure.com</c> is accepted (no <c>*.visualstudio.com</c>, no Server), and only a
/// team board, a team backlog or a saved query. Segments are percent-decoded, because project and
/// team names can contain spaces, and validated before they reach a REST path; the query string
/// and fragment are display state, so they are ignored.
/// </summary>
/// <param name="Link">What is saved and shown again: the canonical link rebuilt from the parsed
/// segments.</param>
/// <param name="Team">The team whose board or backlog the link names; null for a saved query.</param>
/// <param name="BacklogLevel">The board's backlog level (Stories, Backlog items, Epics, …); null for a query.</param>
/// <param name="QueryId">The saved query's id, lower-case with dashes; null for a team board.</param>
public sealed partial record AdoBoardLink(
    string Organization, string Project, string? Team, string? BacklogLevel, string? QueryId, string Link)
{
    public const string Host = "dev.azure.com";
    public const string Origin = "https://" + Host;
    public const int MaxLinkLength = 4000;
    public const int MaxNameLength = 128;

    public static AdoBoardLink Parse(string? url)
    {
        var text = url?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new AdoConfigException("Paste the link of your Azure DevOps board or saved query.");
        if (text.Length > MaxLinkLength
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new AdoConfigException(
                "Paste the https link of an Azure DevOps board, such as https://dev.azure.com/your-org/Project/_boards/board/t/Team/Stories.");
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort)
            throw new AdoConfigException(
                "Only Azure DevOps Services links on https://dev.azure.com are supported. Open the board on dev.azure.com and copy the address bar.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length < 2)
            throw new AdoConfigException("Paste a board or query link, not just the organization. Open the board in Azure DevOps and copy the address bar.");

        var organization = segments[0];
        if (!IsOrganization(organization))
            throw new AdoConfigException("That link's organization name isn't valid. Open the board in Azure DevOps and copy the address bar.");
        var project = segments[1];
        if (!IsName(project))
            throw new AdoConfigException("That link's project name isn't valid. Open the board in Azure DevOps and copy the address bar.");

        // /{org}/{project}/_boards/board/t/{team}/{backlogLevel}
        if (segments.Length == 7
            && Is(segments[2], "_boards") && Is(segments[3], "board") && Is(segments[4], "t"))
            return Board(organization, project, segments[5], segments[6]);

        // /{org}/{project}/_backlogs/backlog/{team}/{backlogLevel}
        if (segments.Length == 6 && Is(segments[2], "_backlogs") && Is(segments[3], "backlog"))
            return Board(organization, project, segments[4], segments[5]);

        // /{org}/{project}/_queries/query/{id}/ and /query-edit/{id}/
        if (segments.Length == 5 && Is(segments[2], "_queries")
            && (Is(segments[3], "query") || Is(segments[3], "query-edit")))
        {
            if (!Guid.TryParseExact(segments[4], "D", out var id))
                throw new AdoConfigException("That query link has no query id. Save the query, open it, and copy the address bar.");
            var queryId = id.ToString("D");
            return new AdoBoardLink(organization, project, null, null, queryId,
                $"{Origin}/{Escape(organization)}/{Escape(project)}/_queries/query/{queryId}/");
        }

        throw Unrecognized();
    }

    /// <summary>An organization name: letters, digits and hyphens, starting and ending with a letter or digit.</summary>
    public static bool IsOrganization(string? value) => value is not null && OrganizationPattern().IsMatch(value);

    /// <summary>
    /// A project, team or backlog level name. Anything Azure DevOps allows passes; what could change
    /// a REST path once escaped (dot segments, controls, separators) does not.
    /// </summary>
    public static bool IsName(string? value) =>
        value is { Length: > 0 and <= MaxNameLength }
        && value.Trim().Length == value.Length
        && value[0] != '.' && value[^1] != '.'
        && !value.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%');

    /// <summary>A path segment for a REST URL: validated, then escaped.</summary>
    public static string Segment(string value, string what) =>
        IsName(value) ? Escape(value) : throw new AdoConfigException($"The Azure DevOps {what} name isn't valid.");

    /// <summary>The work item page a card links to.</summary>
    public static string WorkItemUrl(string organization, string project, int workItemId) =>
        $"{Origin}/{Escape(organization)}/{Escape(project)}/_workitems/edit/{workItemId}";

    private static AdoBoardLink Board(string organization, string project, string team, string backlogLevel)
    {
        if (!IsName(team))
            throw new AdoConfigException("That link's team name isn't valid. Open the board in Azure DevOps and copy the address bar.");
        if (!IsName(backlogLevel))
            throw new AdoConfigException("That link's backlog level isn't valid. Open the board in Azure DevOps and copy the address bar.");
        return new AdoBoardLink(organization, project, team, backlogLevel, null,
            $"{Origin}/{Escape(organization)}/{Escape(project)}/_boards/board/t/{Escape(team)}/{Escape(backlogLevel)}");
    }

    private static bool Is(string segment, string expected) =>
        string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);

    private static string Escape(string segment) => Uri.EscapeDataString(segment);

    private static AdoConfigException Unrecognized() =>
        new("That link isn't an Azure DevOps team board, backlog or saved query. Open the board in Azure DevOps and copy the address bar.");

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,48}[A-Za-z0-9])?$")]
    private static partial Regex OrganizationPattern();
}

public sealed class AdoConfigException(string message) : Exception(message);
