using VibeRails.Services.Board;
using VibeRails.Services.Board.Tracker;

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

/// <summary>
/// Pure mapping from one Jira issue onto the card fields a pull is allowed to change.
/// Jira wins on those fields. Story points are read only when the connection names the field id.
/// Points snapping, labels and lane decisions are the shared <see cref="TrackerFieldMapping"/> rules.
/// </summary>
public static class JiraFieldMapping
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = BoardCardLimits.MaxDescriptionLength;
    public const string OverflowLaneName = "Jira";

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
            TrackerFieldMapping.MapLabels(issue.Labels),
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
    /// the pull does not touch points at all. <paramref name="clearPoints"/> is set only when there
    /// is no value to write: the card patch treats it as "set to null" and it wins over a value.
    /// </summary>
    public static int? MapPoints(JiraIssue issue, string? storyPointsFieldId, out bool clearPoints)
    {
        clearPoints = false;
        if (string.IsNullOrWhiteSpace(storyPointsFieldId))
            return null;
        var points = TrackerFieldMapping.SnapPoints(issue.StoryPoints);
        clearPoints = points is null;
        return points;
    }
}
