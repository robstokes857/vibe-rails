using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VibeRails.Services.Jira;

public sealed record JiraIssue(
    string Id,
    string Key,
    DateTime Updated,
    string Summary,
    string DescriptionText,
    string StatusName,
    string IssueTypeName,
    string? PriorityName,
    IReadOnlyList<string> Labels,
    string? AssigneeDisplay,
    double? StoryPoints,
    string? StatusId = null);

/// <summary>
/// Forbidden and NotFound come only from the board calls (VIBE-102): a board the account can't
/// see, or a configuration Jira won't share. Search still reports 403 as Unauthorized.
/// </summary>
public enum JiraCallOutcome { Ok, Unauthorized, RateLimited, BadJql, Failed, Forbidden, NotFound }

public sealed record JiraSearchPage(IReadOnlyList<JiraIssue> Issues, string? NextPageToken);

public sealed record JiraCallResult<T>(JiraCallOutcome Outcome, T? Value, string? Detail, TimeSpan? RetryAfter);

/// <summary>A Jira Software board (<c>GET /rest/agile/1.0/board/{id}</c>).</summary>
public sealed record JiraBoard(string Id, string Name, string? Type, string? ProjectKey);

/// <summary>One board column and the status ids mapped to it, in the board's column order.</summary>
public sealed record JiraBoardColumn(string Name, IReadOnlyList<string> StatusIds);

/// <summary>
/// <c>GET /rest/agile/1.0/board/{id}/configuration</c>: the board's filter, kanban sub-query,
/// columns and (scrum) estimation field. <see cref="EstimationFieldId"/> is set only when the
/// board estimates with a <c>customfield_</c> field.
/// </summary>
public sealed record JiraBoardConfiguration(
    string? FilterId,
    string? SubQuery,
    IReadOnlyList<JiraBoardColumn> Columns,
    string? EstimationFieldId,
    string? EstimationFieldName);

public sealed record JiraIssueCount(int Count);

/// <summary>
/// Jira Cloud REST over HTTP basic (email + API token), always to the saved site origin. Search is
/// <c>GET /rest/api/3/search/jql</c> with <c>nextPageToken</c>; the removed
/// <c>GET /rest/api/3/search</c> endpoint is never called. A board's issues come from the enhanced
/// <c>GET /rest/software/1.0/board/{id}/issue</c>; the deprecated
/// <c>GET /rest/agile/1.0/board/{id}/issue</c> is never called.
/// </summary>
public interface IJiraCloudClient
{
    Task<JiraCallResult<JiraSearchPage>> SearchAsync(
        string siteUrl, string email, string apiToken, string jql, string? nextPageToken,
        string? storyPointsFieldId, CancellationToken cancellationToken);

    Task<JiraCallResult<string>> TestAsync(string siteUrl, string email, string apiToken, CancellationToken cancellationToken);

    Task<JiraCallResult<JiraBoard>> GetBoardAsync(
        string siteUrl, string email, string apiToken, string boardId, CancellationToken cancellationToken);

    Task<JiraCallResult<JiraBoardConfiguration>> GetBoardConfigurationAsync(
        string siteUrl, string email, string apiToken, string boardId, CancellationToken cancellationToken);

    /// <summary>One page of the board's issues in rank order, narrowed by <paramref name="jql"/> when given.</summary>
    Task<JiraCallResult<JiraSearchPage>> SearchBoardAsync(
        string siteUrl, string email, string apiToken, string boardId, string? jql, string? nextPageToken,
        string? storyPointsFieldId, CancellationToken cancellationToken);

    Task<JiraCallResult<JiraIssueCount>> CountBoardIssuesAsync(
        string siteUrl, string email, string apiToken, string boardId, string? jql, CancellationToken cancellationToken);

    /// <summary>The saved filter's JQL (<c>GET /rest/api/3/filter/{id}</c>).</summary>
    Task<JiraCallResult<string>> GetFilterJqlAsync(
        string siteUrl, string email, string apiToken, string filterId, CancellationToken cancellationToken);
}

public sealed class JiraCloudClient(HttpClient http) : IJiraCloudClient
{
    public const string HttpClientName = "jira-cloud";
    public const int PageSize = 50;

    private static readonly string[] BaseFields =
        ["summary", "description", "status", "issuetype", "priority", "labels", "assignee", "parent", "updated"];

    public async Task<JiraCallResult<string>> TestAsync(string siteUrl, string email, string apiToken, CancellationToken cancellationToken)
    {
        var site = JiraSite.Parse(siteUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, site.Api("myself"));
        Authorize(request, email, apiToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(JiraCallOutcome.Unauthorized, null, "Jira refused the email and API token.", null);
        if (!response.IsSuccessStatusCode)
            return new(JiraCallOutcome.Failed, null, $"Jira returned {(int)response.StatusCode} from /myself.", null);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var name = ReadString(document.RootElement, "displayName") ?? ReadString(document.RootElement, "emailAddress");
        return new(JiraCallOutcome.Ok, name ?? "connected", null, null);
    }

    public async Task<JiraCallResult<JiraSearchPage>> SearchAsync(
        string siteUrl, string email, string apiToken, string jql, string? nextPageToken,
        string? storyPointsFieldId, CancellationToken cancellationToken)
    {
        var site = JiraSite.Parse(siteUrl);
        var fields = Fields(storyPointsFieldId);
        var query = new StringBuilder();
        query.Append("jql=").Append(Uri.EscapeDataString(jql));
        query.Append("&maxResults=").Append(PageSize.ToString(CultureInfo.InvariantCulture));
        query.Append("&fields=").Append(Uri.EscapeDataString(fields));
        if (!string.IsNullOrEmpty(nextPageToken))
            query.Append("&nextPageToken=").Append(Uri.EscapeDataString(nextPageToken));

        using var request = new HttpRequestMessage(HttpMethod.Get, site.Api("search/jql") + "?" + query);
        Authorize(request, email, apiToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(JiraCallOutcome.Unauthorized, null, "Jira refused the email and API token.", null);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return new(JiraCallOutcome.RateLimited, null, "Jira rate limited the pull.", ReadRetryAfter(response));
        if (response.StatusCode == HttpStatusCode.BadRequest)
            return new(JiraCallOutcome.BadJql, null, await ReadErrorAsync(response, cancellationToken), null);
        if (!response.IsSuccessStatusCode)
            return new(JiraCallOutcome.Failed, null, $"Jira search returned {(int)response.StatusCode}.", null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new(JiraCallOutcome.Ok, ReadPage(document.RootElement, storyPointsFieldId), null, null);
    }

    public Task<JiraCallResult<JiraBoard>> GetBoardAsync(
        string siteUrl, string email, string apiToken, string boardId, CancellationToken cancellationToken) =>
        GetJsonAsync(JiraSite.Parse(siteUrl).Agile("board/" + RequireBoardId(boardId)), email, apiToken,
            $"board {boardId}", root => ReadBoard(root, boardId), cancellationToken);

    public Task<JiraCallResult<JiraBoardConfiguration>> GetBoardConfigurationAsync(
        string siteUrl, string email, string apiToken, string boardId, CancellationToken cancellationToken) =>
        GetJsonAsync(JiraSite.Parse(siteUrl).Agile("board/" + RequireBoardId(boardId) + "/configuration"), email, apiToken,
            $"configuration of board {boardId}", ReadConfiguration, cancellationToken);

    public async Task<JiraCallResult<JiraSearchPage>> SearchBoardAsync(
        string siteUrl, string email, string apiToken, string boardId, string? jql, string? nextPageToken,
        string? storyPointsFieldId, CancellationToken cancellationToken)
    {
        var query = new StringBuilder();
        query.Append("maxResults=").Append(PageSize.ToString(CultureInfo.InvariantCulture));
        query.Append("&fields=").Append(Uri.EscapeDataString(Fields(storyPointsFieldId)));
        if (!string.IsNullOrWhiteSpace(jql))
            query.Append("&jql=").Append(Uri.EscapeDataString(jql));
        if (!string.IsNullOrEmpty(nextPageToken))
            query.Append("&nextPageToken=").Append(Uri.EscapeDataString(nextPageToken));
        var url = JiraSite.Parse(siteUrl).Software("board/" + RequireBoardId(boardId) + "/issue") + "?" + query;
        var page = await GetJsonAsync(url, email, apiToken, $"board {boardId}",
            root => ReadPage(root, storyPointsFieldId), cancellationToken);
        // A board the account can't see is a failed pull, not an expired token.
        return page.Outcome is JiraCallOutcome.Forbidden or JiraCallOutcome.NotFound
            ? page with { Outcome = JiraCallOutcome.Failed }
            : page;
    }

    public Task<JiraCallResult<JiraIssueCount>> CountBoardIssuesAsync(
        string siteUrl, string email, string apiToken, string boardId, string? jql, CancellationToken cancellationToken)
    {
        var url = JiraSite.Parse(siteUrl).Software("board/" + RequireBoardId(boardId) + "/issue/approximate-count");
        if (!string.IsNullOrWhiteSpace(jql))
            url += "?jql=" + Uri.EscapeDataString(jql);
        return GetJsonAsync(url, email, apiToken, $"issue count of board {boardId}",
            root => root.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value)
                ? new JiraIssueCount(value)
                : null,
            cancellationToken);
    }

    public Task<JiraCallResult<string>> GetFilterJqlAsync(
        string siteUrl, string email, string apiToken, string filterId, CancellationToken cancellationToken)
    {
        if (!JiraBoardLink.IsBoardId(filterId))
            throw new JiraConfigException("A Jira filter id is a number.");
        return GetJsonAsync(JiraSite.Parse(siteUrl).Api("filter/" + filterId), email, apiToken, $"filter {filterId}",
            root => ReadString(root, "jql") is { Length: > 0 } jql ? jql : null, cancellationToken);
    }

    /// <summary>
    /// One GET of a JSON body. Redirects are off in the registered handler, so a 3xx is a failure
    /// and the token never reaches another host.
    /// </summary>
    private async Task<JiraCallResult<T>> GetJsonAsync<T>(
        string url, string email, string apiToken, string what, Func<JsonElement, T?> read,
        CancellationToken cancellationToken) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request, email, apiToken);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                return new(JiraCallOutcome.Unauthorized, null, "Jira refused the email and API token.", null);
            case HttpStatusCode.Forbidden:
                return new(JiraCallOutcome.Forbidden, null, $"Jira did not let this account read the {what}.", null);
            case HttpStatusCode.NotFound:
                return new(JiraCallOutcome.NotFound, null, $"Jira has no {what} that this account can see.", null);
            case HttpStatusCode.TooManyRequests:
                return new(JiraCallOutcome.RateLimited, null, "Jira rate limited the request.", ReadRetryAfter(response));
            case HttpStatusCode.BadRequest:
                return new(JiraCallOutcome.BadJql, null, await ReadErrorAsync(response, cancellationToken), null);
        }
        if (!response.IsSuccessStatusCode)
            return new(JiraCallOutcome.Failed, null, $"Jira returned {(int)response.StatusCode} for the {what}.", null);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var value = read(document.RootElement);
        return value is null
            ? new(JiraCallOutcome.Failed, null, $"Jira's answer for the {what} could not be read.", null)
            : new(JiraCallOutcome.Ok, value, null, null);
    }

    private static string RequireBoardId(string boardId) =>
        JiraBoardLink.IsBoardId(boardId) ? boardId : throw new JiraConfigException("A Jira board id is a number.");

    private static string Fields(string? storyPointsFieldId) => storyPointsFieldId is { Length: > 0 } field
        ? string.Join(',', BaseFields.Append(field))
        : string.Join(',', BaseFields);

    internal static JiraSearchPage ReadPage(JsonElement root, string? storyPointsFieldId)
    {
        var issues = new List<JiraIssue>();
        if (root.TryGetProperty("issues", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (TryReadIssue(element, storyPointsFieldId, out var issue))
                    issues.Add(issue);
            }
        }
        var token = root.TryGetProperty("nextPageToken", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
        // The board endpoint also says isLast; either signal ends the paging.
        if (string.IsNullOrEmpty(token)
            || (root.TryGetProperty("isLast", out var last) && last.ValueKind == JsonValueKind.True))
            token = null;
        return new JiraSearchPage(issues, token);
    }

    internal static JiraBoard? ReadBoard(JsonElement root, string boardId)
    {
        var name = ReadString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var location = root.TryGetProperty("location", out var locationElement) ? locationElement : default;
        return new JiraBoard(boardId, name.Trim(), ReadString(root, "type"), ReadString(location, "projectKey"));
    }

    internal static JiraBoardConfiguration? ReadConfiguration(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var filterId = root.TryGetProperty("filter", out var filter) ? ReadId(filter) : null;
        var subQuery = root.TryGetProperty("subQuery", out var sub) ? ReadString(sub, "query") : null;
        var columns = new List<JiraBoardColumn>();
        if (root.TryGetProperty("columnConfig", out var columnConfig)
            && columnConfig.ValueKind == JsonValueKind.Object
            && columnConfig.TryGetProperty("columns", out var columnArray)
            && columnArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var column in columnArray.EnumerateArray())
            {
                var name = ReadString(column, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var statuses = new List<string>();
                if (column.TryGetProperty("statuses", out var statusArray) && statusArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var status in statusArray.EnumerateArray())
                    {
                        if (ReadId(status) is { } id)
                            statuses.Add(id);
                    }
                }
                columns.Add(new JiraBoardColumn(name.Trim(), statuses));
            }
        }
        string? fieldId = null;
        string? fieldName = null;
        if (root.TryGetProperty("estimation", out var estimation)
            && ReadString(estimation, "type") == "field"
            && estimation.TryGetProperty("field", out var field)
            && ReadString(field, "fieldId") is { } estimationFieldId
            && IsCustomFieldId(estimationFieldId))
        {
            fieldId = estimationFieldId;
            fieldName = ReadString(field, "displayName");
        }
        return new JiraBoardConfiguration(
            filterId is not null && JiraBoardLink.IsBoardId(filterId) ? filterId : null,
            string.IsNullOrWhiteSpace(subQuery) ? null : subQuery.Trim(),
            columns, fieldId, fieldName);
    }

    /// <summary><c>customfield_</c> followed by digits: the only story points field id a pull reads.</summary>
    public static bool IsCustomFieldId(string value) =>
        value.Length is > 12 and <= 40
        && value.StartsWith("customfield_", StringComparison.Ordinal)
        && value.AsSpan(12).IndexOfAnyExceptInRange('0', '9') < 0;

    // Jira writes ids as strings in some payloads and as numbers in others.
    private static string? ReadId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var id))
            return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };
    }

    internal static bool TryReadIssue(JsonElement element, string? storyPointsFieldId, out JiraIssue issue)
    {
        issue = null!;
        var id = ReadString(element, "id");
        var key = ReadString(element, "key");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key) || !element.TryGetProperty("fields", out var fields))
            return false;
        var updatedText = ReadString(fields, "updated");
        if (!DateTime.TryParse(updatedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated))
            return false;

        var description = fields.TryGetProperty("description", out var descriptionElement)
            ? AdfReader.ToText(descriptionElement)
            : string.Empty;
        double? points = null;
        if (!string.IsNullOrEmpty(storyPointsFieldId)
            && fields.TryGetProperty(storyPointsFieldId, out var pointsElement)
            && pointsElement.ValueKind == JsonValueKind.Number
            && pointsElement.TryGetDouble(out var raw))
            points = raw;

        issue = new JiraIssue(
            id, key, updated.ToUniversalTime(),
            ReadString(fields, "summary") ?? string.Empty,
            description,
            NestedName(fields, "status"),
            NestedName(fields, "issuetype"),
            fields.TryGetProperty("priority", out _) ? NestedName(fields, "priority") : null,
            ReadLabels(fields),
            Assignee(fields),
            points,
            fields.TryGetProperty("status", out var status) ? ReadId(status) : null);
        return true;
    }

    private static IReadOnlyList<string> ReadLabels(JsonElement fields)
    {
        if (!fields.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var label in labels.EnumerateArray())
        {
            if (label.ValueKind == JsonValueKind.String && label.GetString() is { Length: > 0 } text)
                list.Add(text);
        }
        return list;
    }

    private static string? Assignee(JsonElement fields)
    {
        if (!fields.TryGetProperty("assignee", out var assignee) || assignee.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(assignee, "displayName");
    }

    private static string NestedName(JsonElement fields, string property) =>
        fields.TryGetProperty(property, out var value) ? ReadString(value, "name") ?? string.Empty : string.Empty;

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Authorize(HttpRequestMessage request, string email, string apiToken)
    {
        var raw = Encoding.UTF8.GetBytes(email + ":" + apiToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
            return delta;
        if (header?.Date is DateTimeOffset date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errorMessages", out var messages)
                && messages.ValueKind == JsonValueKind.Array
                && messages.GetArrayLength() > 0
                && messages[0].ValueKind == JsonValueKind.String)
                return messages[0].GetString() ?? "Jira rejected the JQL.";
        }
        catch (JsonException)
        {
            // The status code already says the JQL was rejected.
        }
        return "Jira rejected the JQL.";
    }
}

/// <summary>A Jira Cloud site URL. Only https hosts are accepted; no path, query, or credentials.</summary>
public sealed record JiraSite(string Origin)
{
    public static JiraSite Parse(string? siteUrl)
    {
        var text = siteUrl?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath is not "/" and not ""
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new JiraConfigException("Site URL must be an https Jira Cloud address such as https://your-site.atlassian.net.");
        return new JiraSite(uri.GetLeftPart(UriPartial.Authority));
    }

    public string Api(string relative) => Origin + "/rest/api/3/" + relative;

    public string Agile(string relative) => Origin + "/rest/agile/1.0/" + relative;

    public string Software(string relative) => Origin + "/rest/software/1.0/" + relative;
}

public sealed class JiraConfigException(string message) : Exception(message);
