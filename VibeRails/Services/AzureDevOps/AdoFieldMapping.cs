using VibeRails.Services.Board;
using VibeRails.Services.Board.Tracker;

namespace VibeRails.Services.AzureDevOps;

/// <summary>The work item field reference names a pull reads.</summary>
public static class AdoFields
{
    public const string Id = "System.Id";
    public const string Rev = "System.Rev";
    public const string Title = "System.Title";
    public const string WorkItemType = "System.WorkItemType";
    public const string State = "System.State";
    public const string ChangedDate = "System.ChangedDate";
    public const string Description = "System.Description";
    public const string Tags = "System.Tags";
    public const string AssignedTo = "System.AssignedTo";
    public const string BoardColumn = "System.BoardColumn";
    public const string Priority = "Microsoft.VSTS.Common.Priority";
    public const string ReproSteps = "Microsoft.VSTS.TCM.ReproSteps";
    public const string AcceptanceCriteria = "Microsoft.VSTS.Common.AcceptanceCriteria";
    public const string StoryPoints = "Microsoft.VSTS.Scheduling.StoryPoints";
    public const string Effort = "Microsoft.VSTS.Scheduling.Effort";
    public const string Size = "Microsoft.VSTS.Scheduling.Size";

    /// <summary>Requested on every pull; every organization has them.</summary>
    public static readonly string[] Always = [Id, Rev, Title, WorkItemType, State, ChangedDate, Description, Tags, AssignedTo];

    /// <summary>Requested only when field discovery finds them: an unknown field fails the whole batch.</summary>
    public static readonly string[] Optional = [BoardColumn, Priority, ReproSteps, AcceptanceCriteria];

    /// <summary>The process estimate fields: Agile, Scrum and Basic, CMMI.</summary>
    public static readonly string[] Estimates = [StoryPoints, Effort, Size];

    private static readonly HashSet<string> Known = new(Always.Concat(Optional), StringComparer.OrdinalIgnoreCase);

    /// <summary>A requested field read as a number for points: anything the pull did not request for another reason.</summary>
    public static bool IsEstimate(string field) => !Known.Contains(field);
}

/// <summary>The work item fields a pull copies onto a card. Everything else on a card stays as it is.</summary>
public sealed record AdoMappedFields(
    string Title,
    string Description,
    string Type,
    string Priority,
    int? Points,
    bool ClearPoints,
    IReadOnlyList<string> Tags,
    string State,
    string? AssigneeDisplay);

/// <summary>
/// Pure mapping from one Azure DevOps work item onto the fields of the card a pull creates for it.
/// A pull copies them once, at import, and never again. Points snapping, tags and lane decisions
/// are the shared <see cref="TrackerFieldMapping"/> rules.
/// </summary>
public static class AdoFieldMapping
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = BoardCardLimits.MaxDescriptionLength;
    public const string OverflowLaneName = "Azure DevOps";
    public const string AcceptanceCriteriaHeading = "## Acceptance criteria";

    public static AdoMappedFields Map(AdoWorkItem item, string? pointsField, string? existingType)
    {
        var title = item.Title.Trim();
        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength];
        return new AdoMappedFields(
            title,
            MapDescription(item),
            MapType(item.WorkItemType, existingType),
            MapPriority(item.Priority),
            MapPoints(item, pointsField, out var clearPoints),
            clearPoints,
            TrackerFieldMapping.MapLabels(SplitTags(item.Tags)),
            item.State.Trim(),
            string.IsNullOrWhiteSpace(item.AssigneeDisplay) ? null : item.AssigneeDisplay.Trim());
    }

    /// <summary>
    /// The description as text. A bug written up only in Repro Steps uses those; acceptance
    /// criteria follow under their own heading.
    /// </summary>
    public static string MapDescription(AdoWorkItem item)
    {
        var description = AdoHtmlReader.ToText(item.DescriptionHtml);
        if (description.Length == 0 && string.Equals(item.WorkItemType.Trim(), "Bug", StringComparison.OrdinalIgnoreCase))
            description = AdoHtmlReader.ToText(item.ReproStepsHtml);
        var criteria = AdoHtmlReader.ToText(item.AcceptanceCriteriaHtml);
        if (criteria.Length > 0)
            description = (description.Length == 0 ? "" : description + "\n\n") + AcceptanceCriteriaHeading + "\n\n" + criteria;
        return description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength];
    }

    public static string MapType(string? workItemType, string? existingType)
    {
        if (string.Equals(existingType, BoardCardTypes.Chore, StringComparison.Ordinal))
            return BoardCardTypes.Chore;
        return workItemType?.Trim().ToLowerInvariant() switch
        {
            "user story" or "product backlog item" or "requirement" or "feature" or "epic" => BoardCardTypes.Feature,
            "bug" => BoardCardTypes.Bug,
            "spike" => BoardCardTypes.ResearchSpike,
            _ => BoardCardTypes.Task
        };
    }

    /// <summary>Azure DevOps priority 1 to 4; anything else, or none, is medium.</summary>
    public static string MapPriority(int? priority) => priority switch
    {
        1 => "critical",
        2 => "high",
        4 => "low",
        _ => "medium"
    };

    /// <summary>
    /// Snap to the Fibonacci scale. With a points field, a missing or off-scale value clears the
    /// points, so an edit that removes them is not left behind. No field leaves points alone.
    /// </summary>
    public static int? MapPoints(AdoWorkItem item, string? pointsField, out bool clearPoints)
    {
        clearPoints = false;
        if (string.IsNullOrWhiteSpace(pointsField))
            return null;
        var points = TrackerFieldMapping.SnapPoints(item.Estimates.TryGetValue(pointsField, out var raw) ? raw : null);
        clearPoints = points is null;
        return points;
    }

    /// <summary><c>System.Tags</c> is one string, tags separated by <c>;</c>.</summary>
    public static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The process's points field from the work item types it pulls: User Story is Agile (Story
    /// Points), Product Backlog Item is Scrum and Issue is Basic (Effort), Requirement is CMMI
    /// (Size). Null when no type says, or the organization lacks the field.
    /// </summary>
    public static string? DetectPointsField(IEnumerable<string> types, AdoFieldSet? fields)
    {
        var names = types.Select(type => type.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? field = names.Contains("User Story") ? AdoFields.StoryPoints
            : names.Contains("Product Backlog Item") || names.Contains("Issue") ? AdoFields.Effort
            : names.Contains("Requirement") ? AdoFields.Size
            : null;
        return field is not null && fields?.Has(field) == true ? field : null;
    }

    /// <summary>
    /// The points field for one work item when nothing names one for the whole pull: a saved query
    /// can span projects with different processes, and a board's types may not say. Its type's
    /// process field, else the first estimate field it has a value in (a bug carries its process's
    /// estimate). Null leaves the card's points alone.
    /// </summary>
    public static string? DetectPointsField(AdoWorkItem item, AdoFieldSet? fields) =>
        DetectPointsField([item.WorkItemType], fields)
        ?? AdoFields.Estimates.FirstOrDefault(field => item.Estimates.ContainsKey(field) && fields?.Has(field) == true);

    /// <summary>The key a card shows for a work item.</summary>
    public static string Key(int workItemId) => "#" + workItemId;
}
