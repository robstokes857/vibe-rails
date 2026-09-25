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
    double? StoryPoints);

public enum JiraCallOutcome { Ok, Unauthorized, RateLimited, BadJql, Failed }

public sealed record JiraSearchPage(IReadOnlyList<JiraIssue> Issues, string? NextPageToken);

public sealed record JiraCallResult<T>(JiraCallOutcome Outcome, T? Value, string? Detail, TimeSpan? RetryAfter);

/// <summary>
/// Jira Cloud REST v3 over HTTP basic (email + API token). Search is
/// <c>GET /rest/api/3/search/jql</c> with <c>nextPageToken</c>. The removed
/// <c>GET /rest/api/3/search</c> endpoint is never called.
/// </summary>
public interface IJiraCloudClient
{
    Task<JiraCallResult<JiraSearchPage>> SearchAsync(
        string siteUrl, string email, string apiToken, string jql, string? nextPageToken,
        string? storyPointsFieldId, CancellationToken cancellationToken);

    Task<JiraCallResult<string>> TestAsync(string siteUrl, string email, string apiToken, CancellationToken cancellationToken);
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
        var fields = storyPointsFieldId is { Length: > 0 } field
            ? string.Join(',', BaseFields.Append(field))
            : string.Join(',', BaseFields);
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
        var root = document.RootElement;
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
        if (string.IsNullOrEmpty(token))
            token = null;
        return new(JiraCallOutcome.Ok, new JiraSearchPage(issues, token), null, null);
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
            points);
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
}

public sealed class JiraConfigException(string message) : Exception(message);
