using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VibeRails.Services.AzureDevOps;

/// <summary>
/// Forbidden and NotFound let the board calls fall back (a board configuration the token may not
/// read). BadQuery comes only from the WIQL calls: Azure DevOps rejected the query itself.
/// </summary>
public enum AdoCallOutcome { Ok, Unauthorized, RateLimited, BadQuery, Failed, Forbidden, NotFound }

public sealed record AdoCallResult<T>(AdoCallOutcome Outcome, T? Value, string? Detail, TimeSpan? RetryAfter);

/// <summary><c>GET {org}/_apis/projects/{project}</c>: the project the token can read.</summary>
public sealed record AdoProject(string Id, string Name);

/// <summary>
/// One board column in board order. <see cref="ColumnType"/> is <c>incoming</c>, <c>inProgress</c>
/// or <c>outgoing</c>; <see cref="StateMappings"/> names the state each work item type has in it.
/// </summary>
public sealed record AdoBoardColumn(
    string Name, string ColumnType, IReadOnlyDictionary<string, string> StateMappings, bool IsSplit)
{
    public bool IsOutgoing => string.Equals(ColumnType, "outgoing", StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>GET {org}/{project}/{team}/_apis/work/boards/{backlogLevel}</c>.</summary>
public sealed record AdoBoardConfiguration(string Name, IReadOnlyList<AdoBoardColumn> Columns)
{
    /// <summary>Every work item type the board's columns map, in first-seen order.</summary>
    public IReadOnlyList<string> Types =>
        Columns.SelectMany(column => column.StateMappings.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public sealed record AdoTeamFieldValue(string Value, bool IncludeChildren);

/// <summary><c>GET {org}/{project}/{team}/_apis/work/teamsettings/teamfieldvalues</c>: the field
/// (usually <c>System.AreaPath</c>) and values that put a work item on the team's board.</summary>
public sealed record AdoTeamFieldValues(string Field, IReadOnlyList<AdoTeamFieldValue> Values);

/// <summary>The field reference names this organization has (<c>GET {org}/_apis/wit/fields</c>).</summary>
public sealed record AdoFieldSet(IReadOnlySet<string> ReferenceNames)
{
    public bool Has(string referenceName) => ReferenceNames.Contains(referenceName);
}

/// <summary>A saved query (<c>GET {org}/{project}/_apis/wit/queries/{id}</c>).</summary>
public sealed record AdoQuery(string Id, string Name, string? QueryType, bool IsFolder);

/// <summary>Work item ids in the order the query returned them, without repeats.</summary>
public sealed record AdoWorkItemIds(IReadOnlyList<int> Ids);

/// <summary>
/// One work item as a pull reads it. Rich text fields are the raw HTML; <see cref="Estimates"/>
/// holds every requested numeric points field that has a value, by reference name.
/// </summary>
public sealed record AdoWorkItem(
    int Id,
    int Rev,
    DateTime ChangedDate,
    string Title,
    string WorkItemType,
    string State,
    string? DescriptionHtml,
    string? ReproStepsHtml,
    string? AcceptanceCriteriaHtml,
    int? Priority,
    string? Tags,
    string? AssigneeDisplay,
    string? BoardColumn,
    IReadOnlyDictionary<string, double> Estimates);

/// <summary>
/// Azure DevOps Services REST (api-version 7.1) over HTTP basic with an empty user and the
/// personal access token, always to <c>https://dev.azure.com</c>. Every path segment is validated
/// and escaped here as well as in <see cref="AdoBoardLink"/>.
/// </summary>
public interface IAzureDevOpsClient
{
    Task<AdoCallResult<AdoProject>> TestAsync(string organization, string project, string token, CancellationToken cancellationToken);

    Task<AdoCallResult<AdoBoardConfiguration>> GetBoardAsync(
        string organization, string project, string team, string backlogLevel, string token, CancellationToken cancellationToken);

    Task<AdoCallResult<AdoTeamFieldValues>> GetTeamFieldValuesAsync(
        string organization, string project, string team, string token, CancellationToken cancellationToken);

    Task<AdoCallResult<AdoFieldSet>> GetFieldsAsync(string organization, string token, CancellationToken cancellationToken);

    Task<AdoCallResult<AdoQuery>> GetQueryAsync(
        string organization, string project, string queryId, string token, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="wiql"/> in the team's context, returning at most <see cref="AzureDevOpsClient.MaxItems"/> ids.</summary>
    Task<AdoCallResult<AdoWorkItemIds>> QueryIdsAsync(
        string organization, string project, string team, string wiql, string token, CancellationToken cancellationToken);

    /// <summary>Runs a saved query by id. Flat queries list work items; tree and one-hop queries list link targets.</summary>
    Task<AdoCallResult<AdoWorkItemIds>> RunSavedQueryAsync(
        string organization, string project, string queryId, string token, CancellationToken cancellationToken);

    /// <summary>At most <see cref="AzureDevOpsClient.BatchSize"/> work items, with only <paramref name="fields"/>.
    /// Items that were deleted or can't be read are left out.</summary>
    Task<AdoCallResult<IReadOnlyList<AdoWorkItem>>> GetWorkItemsAsync(
        string organization, string project, IReadOnlyList<int> ids, IReadOnlyList<string> fields, string token,
        CancellationToken cancellationToken);
}

/// <param name="requestTimeout">One deadline for each request, its headers and its whole body;
/// 30 seconds unless a test sets it.</param>
public sealed partial class AzureDevOpsClient(HttpClient http, TimeSpan? requestTimeout = null) : IAzureDevOpsClient, IAdoCommentClient
{
    public const string HttpClientName = "azure-devops";
    public const string ApiVersion = "7.1";
    public const int BatchSize = 200;
    public const int MaxBatches = 10;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = requestTimeout ?? RequestTimeout;

    /// <summary>The most work items one pull reads: 10 batches of 200, the same as Jira's 40 pages of 50.</summary>
    public const int MaxItems = BatchSize * MaxBatches;

    public Task<AdoCallResult<AdoProject>> TestAsync(string organization, string project, string token, CancellationToken cancellationToken) =>
        SendAsync(Get(Org(organization) + "/_apis/projects/" + AdoBoardLink.Segment(project, "project"), token),
            $"project {project}", ReadProject, badRequestIsQuery: false, cancellationToken);

    public Task<AdoCallResult<AdoBoardConfiguration>> GetBoardAsync(
        string organization, string project, string team, string backlogLevel, string token, CancellationToken cancellationToken) =>
        SendAsync(Get(Team(organization, project, team) + "/_apis/work/boards/" + AdoBoardLink.Segment(backlogLevel, "backlog level"), token),
            $"{backlogLevel} board of team {team}", ReadBoard, badRequestIsQuery: false, cancellationToken);

    public Task<AdoCallResult<AdoTeamFieldValues>> GetTeamFieldValuesAsync(
        string organization, string project, string team, string token, CancellationToken cancellationToken) =>
        SendAsync(Get(Team(organization, project, team) + "/_apis/work/teamsettings/teamfieldvalues", token),
            $"area paths of team {team}", ReadTeamFieldValues, badRequestIsQuery: false, cancellationToken);

    public Task<AdoCallResult<AdoFieldSet>> GetFieldsAsync(string organization, string token, CancellationToken cancellationToken) =>
        SendAsync(Get(Org(organization) + "/_apis/wit/fields", token), "work item fields", ReadFields,
            badRequestIsQuery: false, cancellationToken);

    public Task<AdoCallResult<AdoQuery>> GetQueryAsync(
        string organization, string project, string queryId, string token, CancellationToken cancellationToken) =>
        SendAsync(Get(Project(organization, project) + "/_apis/wit/queries/" + RequireQueryId(queryId), token),
            $"saved query {queryId}", ReadQuery, badRequestIsQuery: false, cancellationToken);

    public Task<AdoCallResult<AdoWorkItemIds>> QueryIdsAsync(
        string organization, string project, string team, string wiql, string token, CancellationToken cancellationToken)
    {
        var request = Request(HttpMethod.Post,
            Team(organization, project, team) + "/_apis/wit/wiql", token, "$top=" + MaxItems.ToString(CultureInfo.InvariantCulture));
        request.Content = Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("query", wiql);
            writer.WriteEndObject();
        });
        return SendAsync(request, "board query", ReadIds, badRequestIsQuery: true, cancellationToken);
    }

    public Task<AdoCallResult<AdoWorkItemIds>> RunSavedQueryAsync(
        string organization, string project, string queryId, string token, CancellationToken cancellationToken) =>
        SendAsync(Request(HttpMethod.Get, Project(organization, project) + "/_apis/wit/wiql/" + RequireQueryId(queryId), token,
                "$top=" + MaxItems.ToString(CultureInfo.InvariantCulture)),
            $"saved query {queryId}", ReadIds, badRequestIsQuery: true, cancellationToken);

    public Task<AdoCallResult<IReadOnlyList<AdoWorkItem>>> GetWorkItemsAsync(
        string organization, string project, IReadOnlyList<int> ids, IReadOnlyList<string> fields, string token,
        CancellationToken cancellationToken)
    {
        if (ids.Count is 0 or > BatchSize)
            throw new ArgumentOutOfRangeException(nameof(ids), $"A work item batch has 1 to {BatchSize} ids.");
        if (fields.Any(field => !AdoWiql.IsFieldReference(field)))
            throw new AdoConfigException("A work item field name isn't valid.");
        var request = Request(HttpMethod.Post, Project(organization, project) + "/_apis/wit/workitemsbatch", token);
        request.Content = Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("ids");
            foreach (var id in ids)
                writer.WriteNumberValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray("fields");
            foreach (var field in fields)
                writer.WriteStringValue(field);
            writer.WriteEndArray();
            // A work item deleted since the query ran comes back as null instead of failing the batch.
            writer.WriteString("errorPolicy", "omit");
            writer.WriteEndObject();
        });
        var estimates = fields.Where(AdoFields.IsEstimate).ToList();
        return SendAsync<IReadOnlyList<AdoWorkItem>>(request, "work items", root => ReadWorkItems(root, estimates),
            badRequestIsQuery: false, cancellationToken);
    }

    /// <summary>
    /// One request and its JSON answer. Redirects are off in the registered handler, so the token
    /// never reaches another host. A bad token is not always a 401: Azure DevOps may answer 203 or
    /// 302 with its HTML sign-in page, so those, and any 2xx that isn't JSON, are Unauthorized too.
    /// One deadline covers the send and every body read: <see cref="HttpClient.Timeout"/> stops at
    /// the headers here, and a body that stalls after them would otherwise hold the pull lock
    /// until the host stops. A missed deadline is a failed call, so the next connection still runs.
    /// </summary>
    private async Task<AdoCallResult<T>> SendAsync<T>(
        HttpRequestMessage request, string what, Func<JsonElement, T?> read, bool badRequestIsQuery,
        CancellationToken cancellationToken) where T : class
    {
        using (request)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                return await ReceiveAsync(request, what, read, badRequestIsQuery, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(AdoCallOutcome.Failed, null,
                    $"Azure DevOps did not answer within {_timeout.TotalSeconds:0} seconds for the {what}.", null);
            }
            catch (HttpRequestException)
            {
                return new(AdoCallOutcome.Failed, null, $"Azure DevOps could not be reached for the {what}.", null);
            }
        }
    }

    private async Task<AdoCallResult<T>> ReceiveAsync<T>(
        HttpRequestMessage request, string what, Func<JsonElement, T?> read, bool badRequestIsQuery,
        CancellationToken cancellationToken) where T : class
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized or HttpStatusCode.NonAuthoritativeInformation or HttpStatusCode.Redirect:
                return new(AdoCallOutcome.Unauthorized, null, Refused, null);
            case HttpStatusCode.Forbidden:
                return new(AdoCallOutcome.Forbidden, null, $"Azure DevOps did not let this token read the {what}.", null);
            case HttpStatusCode.NotFound:
                return new(AdoCallOutcome.NotFound, null, $"Azure DevOps has no {what} that this token can see.", null);
            case HttpStatusCode.TooManyRequests:
                return new(AdoCallOutcome.RateLimited, null, "Azure DevOps rate limited the request.", ReadRetryAfter(response));
            case HttpStatusCode.BadRequest:
                var message = await ReadErrorAsync(response, cancellationToken);
                return badRequestIsQuery
                    ? new(AdoCallOutcome.BadQuery, null, message ?? "Azure DevOps rejected the query.", null)
                    : new(AdoCallOutcome.Failed, null, message ?? $"Azure DevOps rejected the request for the {what}.", null);
        }
        if (!response.IsSuccessStatusCode)
            return new(AdoCallOutcome.Failed, null, $"Azure DevOps returned {(int)response.StatusCode} for the {what}.", null);
        if (!IsJson(response))
            return new(AdoCallOutcome.Unauthorized, null, Refused, null);

        JsonDocument document;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return new(AdoCallOutcome.Failed, null, $"Azure DevOps's answer for the {what} could not be read.", null);
        }
        using (document)
        {
            var value = read(document.RootElement);
            return value is null
                ? new(AdoCallOutcome.Failed, null, $"Azure DevOps's answer for the {what} could not be read.", null)
                : new(AdoCallOutcome.Ok, value, null, null);
        }
    }

    private const string Refused = "Azure DevOps refused the personal access token. It may have expired or been revoked.";

    private static bool IsJson(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType is { } media
        && (media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static HttpRequestMessage Get(string url, string token) => Request(HttpMethod.Get, url, token);

    private static HttpRequestMessage Request(HttpMethod method, string url, string token, string? query = null, string apiVersion = ApiVersion)
    {
        var request = new HttpRequestMessage(method, url + "?" + (query is null ? "" : query + "&") + "api-version=" + apiVersion);
        var raw = Encoding.UTF8.GetBytes(":" + token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Ask for a 401 instead of a redirect to the sign-in page when the token is refused.
        request.Headers.TryAddWithoutValidation("X-TFS-FedAuthRedirect", "Suppress");
        return request;
    }

    private static StringContent Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return new StringContent(Encoding.UTF8.GetString(buffer.WrittenSpan), Encoding.UTF8, "application/json");
    }

    private static string Org(string organization) =>
        AdoBoardLink.IsOrganization(organization)
            ? AdoBoardLink.Origin + "/" + organization
            : throw new AdoConfigException("The Azure DevOps organization name isn't valid.");

    private static string Project(string organization, string project) =>
        Org(organization) + "/" + AdoBoardLink.Segment(project, "project");

    private static string Team(string organization, string project, string team) =>
        Project(organization, project) + "/" + AdoBoardLink.Segment(team, "team");

    private static string RequireQueryId(string queryId) =>
        Guid.TryParseExact(queryId, "D", out var id) ? id.ToString("D") : throw new AdoConfigException("A saved query id is a GUID.");

    internal static AdoProject? ReadProject(JsonElement root) =>
        ReadString(root, "id") is { Length: > 0 } id && ReadString(root, "name") is { Length: > 0 } name
            ? new AdoProject(id, name)
            : null;

    internal static AdoBoardConfiguration? ReadBoard(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("columns", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;
        var columns = new List<AdoBoardColumn>();
        foreach (var column in array.EnumerateArray())
        {
            var name = ReadString(column, "name")?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (column.TryGetProperty("stateMappings", out var stateMappings) && stateMappings.ValueKind == JsonValueKind.Object)
            {
                foreach (var mapping in stateMappings.EnumerateObject())
                {
                    if (mapping.Value.ValueKind == JsonValueKind.String && mapping.Value.GetString() is { Length: > 0 } state
                        && mapping.Name.Trim().Length > 0)
                        mappings.TryAdd(mapping.Name.Trim(), state.Trim());
                }
            }
            var split = column.TryGetProperty("isSplit", out var isSplit) && isSplit.ValueKind == JsonValueKind.True;
            columns.Add(new AdoBoardColumn(name, ReadString(column, "columnType") ?? string.Empty, mappings, split));
        }
        return new AdoBoardConfiguration(ReadString(root, "name")?.Trim() ?? string.Empty, columns);
    }

    internal static AdoTeamFieldValues? ReadTeamFieldValues(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("field", out var field)
            || ReadString(field, "referenceName") is not { Length: > 0 } referenceName)
            return null;
        var values = new List<AdoTeamFieldValue>();
        if (root.TryGetProperty("values", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in array.EnumerateArray())
            {
                if (ReadString(value, "value") is { Length: > 0 } text)
                    values.Add(new AdoTeamFieldValue(text,
                        value.TryGetProperty("includeChildren", out var children) && children.ValueKind == JsonValueKind.True));
            }
        }
        return new AdoTeamFieldValues(referenceName, values);
    }

    internal static AdoFieldSet? ReadFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in array.EnumerateArray())
        {
            if (ReadString(field, "referenceName") is { Length: > 0 } name)
                names.Add(name);
        }
        return new AdoFieldSet(names);
    }

    internal static AdoQuery? ReadQuery(JsonElement root) =>
        ReadString(root, "id") is { Length: > 0 } id && ReadString(root, "name") is { Length: > 0 } name
            ? new AdoQuery(id, name.Trim(), ReadString(root, "queryType"),
                root.TryGetProperty("isFolder", out var folder) && folder.ValueKind == JsonValueKind.True)
            : null;

    internal static AdoWorkItemIds? ReadIds(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var ids = new List<int>();
        var seen = new HashSet<int>();
        if (root.TryGetProperty("workItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                Add(item);
        }
        // Tree and one-hop queries list links; every work item appears as some link's target.
        else if (root.TryGetProperty("workItemRelations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in relations.EnumerateArray())
            {
                if (relation.ValueKind == JsonValueKind.Object && relation.TryGetProperty("target", out var target))
                    Add(target);
            }
        }
        else
            return null;
        return new AdoWorkItemIds(ids.Take(MaxItems).ToList());

        void Add(JsonElement element)
        {
            if (ReadInt(element, "id") is int id and > 0 && seen.Add(id))
                ids.Add(id);
        }
    }

    internal static IReadOnlyList<AdoWorkItem>? ReadWorkItems(JsonElement root, IReadOnlyList<string> estimateFields)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var array) || array.ValueKind != JsonValueKind.Array)
            return null;
        var items = new List<AdoWorkItem>();
        foreach (var element in array.EnumerateArray())
        {
            if (TryReadWorkItem(element, estimateFields, out var item))
                items.Add(item);
        }
        return items;
    }

    internal static bool TryReadWorkItem(JsonElement element, IReadOnlyList<string> estimateFields, out AdoWorkItem item)
    {
        item = null!;
        if (ReadInt(element, "id") is not int id || id <= 0
            || !element.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
            return false;
        var rev = ReadInt(element, "rev") ?? ReadInt(fields, AdoFields.Rev);
        if (rev is null
            || !DateTime.TryParse(ReadString(fields, AdoFields.ChangedDate), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var changed))
            return false;

        var estimates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in estimateFields)
        {
            if (fields.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                estimates[field] = number;
        }
        item = new AdoWorkItem(
            id, rev.Value, DateTime.SpecifyKind(changed, DateTimeKind.Utc),
            ReadString(fields, AdoFields.Title) ?? string.Empty,
            ReadString(fields, AdoFields.WorkItemType) ?? string.Empty,
            ReadString(fields, AdoFields.State) ?? string.Empty,
            ReadString(fields, AdoFields.Description),
            ReadString(fields, AdoFields.ReproSteps),
            ReadString(fields, AdoFields.AcceptanceCriteria),
            ReadInt(fields, AdoFields.Priority),
            ReadString(fields, AdoFields.Tags),
            Assignee(fields),
            ReadString(fields, AdoFields.BoardColumn),
            estimates);
        return true;
    }

    // An identity object in current API versions; "Name <email>" text in older ones.
    private static string? Assignee(JsonElement fields)
    {
        if (!fields.TryGetProperty(AdoFields.AssignedTo, out var assigned))
            return null;
        if (assigned.ValueKind == JsonValueKind.Object)
            return ReadString(assigned, "displayName");
        if (assigned.ValueKind != JsonValueKind.String || assigned.GetString() is not { Length: > 0 } text)
            return null;
        var angle = text.IndexOf('<');
        return angle > 0 ? text[..angle].Trim() : text;
    }

    private static int? ReadInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

    // Azure DevOps errors are {"message": "TF51005: ..."}; anything else keeps the generic text.
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var message = ReadString(document.RootElement, "message")?.Trim();
            return string.IsNullOrEmpty(message) ? null : message.Length <= 500 ? message : message[..500];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
