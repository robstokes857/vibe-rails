using VibeRails.Services.Board;

namespace VibeRails.Services.Jira;

/// <summary>The Jira fields phase 1 copies onto a card. Everything else on a card stays as it is.</summary>
public sealed record JiraMappedFields(
    string Title,
    string Description,
    string Type,
    string Priority,
    int? Points,
    bool ClearPoints,
    IReadOnlyList<string> Tags,
    string StatusName,
    string? AssigneeDisplay);

public enum JiraLaneMatch { Matched, AlreadyThere, Overflow, Unresolved }

public sealed record JiraLaneDecision(JiraLaneMatch Match, string? ColumnId, string? ColumnName, bool Comment);

/// <summary>
/// Pure mapping from one Jira issue onto the card fields a pull is allowed to change.
/// Jira wins on those fields. Story points are read only when the connection names the field id.
/// </summary>
public static class JiraFieldMapping
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = BoardCardLimits.MaxDescriptionLength;
    public const int MaxTags = 20;
    public const int MaxTagLength = 40;
    public const string OverflowLaneName = "Jira";

    public static readonly int[] PointScale = [1, 2, 3, 5, 8, 13];

    public static JiraMappedFields Map(JiraIssue issue, string? storyPointsFieldId, string? existingType)
    {
        var title = issue.Summary.Trim();
        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength];

        var description = issue.DescriptionText.Replace("\r\n", "\n").Trim();
        if (description.Length > MaxDescriptionLength)
            description = description[..MaxDescriptionLength];

        return new JiraMappedFields(
            title,
            description,
            MapType(issue.IssueTypeName, existingType),
            MapPriority(issue.PriorityName),
            MapPoints(issue, storyPointsFieldId, out var clearPoints),
            clearPoints,
            MapLabels(issue.Labels),
            issue.StatusName.Trim(),
            string.IsNullOrWhiteSpace(issue.AssigneeDisplay) ? null : issue.AssigneeDisplay.Trim());
    }

    public static string MapType(string? issueTypeName, string? existingType)
    {
        if (string.Equals(existingType, BoardCardTypes.Chore, StringComparison.Ordinal))
            return BoardCardTypes.Chore;
        return issueTypeName?.Trim().ToLowerInvariant() switch
        {
            "bug" => BoardCardTypes.Bug,
            "story" or "feature" or "new feature" => BoardCardTypes.Feature,
            "spike" => BoardCardTypes.ResearchSpike,
            _ => BoardCardTypes.Task
        };
    }

    public static string MapPriority(string? priorityName) => priorityName?.Trim().ToLowerInvariant() switch
    {
        "highest" or "critical" or "blocker" => "critical",
        "high" => "high",
        "low" or "lowest" => "low",
        _ => "medium"
    };

    /// <summary>
    /// Snap to the Fibonacci scale. A configured field whose value is missing or not on the scale
    /// clears the points, so a Jira edit that removes them is not left behind. No field id means
    /// the pull does not touch points at all.
    /// </summary>
    public static int? MapPoints(JiraIssue issue, string? storyPointsFieldId, out bool clearPoints)
    {
        clearPoints = false;
        if (string.IsNullOrWhiteSpace(storyPointsFieldId))
            return null;
        clearPoints = true;
        if (issue.StoryPoints is not double raw || double.IsNaN(raw) || raw <= 0)
            return null;
        var whole = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        return PointScale.Contains(whole) ? whole : null;
    }

    public static IReadOnlyList<string> MapLabels(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0)
            return [];
        var tags = new List<string>(Math.Min(labels.Count, MaxTags));
        foreach (var label in labels)
        {
            if (tags.Count == MaxTags)
                break;
            var tag = label.Trim();
            if (tag.Length == 0)
                continue;
            if (tag.Length > MaxTagLength)
                tag = tag[..MaxTagLength];
            tags.Add(tag);
        }
        return tags;
    }

    /// <summary>
    /// Case-insensitive lane name. Two lanes with the same name are unresolved (no guess).
    /// An unknown status uses the single overflow lane when one exists.
    /// </summary>
    public static JiraLaneDecision MatchLane(
        string statusName, string? currentColumnId, IReadOnlyList<BoardColumnRecord> lanes, string? overflowColumnId)
    {
        var name = statusName.Trim();
        var matches = lanes.Where(lane => string.Equals(lane.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        BoardColumnRecord? chosen = matches.Count == 1 ? matches[0] : null;
        if (chosen is null && matches.Count == 0 && overflowColumnId is not null)
            chosen = lanes.FirstOrDefault(lane => string.Equals(lane.Id, overflowColumnId, StringComparison.Ordinal));

        if (chosen is null)
            return new JiraLaneDecision(matches.Count > 1 ? JiraLaneMatch.Unresolved : JiraLaneMatch.Unresolved, null, null, false);
        if (string.Equals(chosen.Id, currentColumnId, StringComparison.Ordinal))
            return new JiraLaneDecision(JiraLaneMatch.AlreadyThere, chosen.Id, chosen.Name, false);
        var overflow = matches.Count == 0;
        return new JiraLaneDecision(overflow ? JiraLaneMatch.Overflow : JiraLaneMatch.Matched, chosen.Id, chosen.Name, true);
    }
}
