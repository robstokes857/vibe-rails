using VibeRails.Services.Board;
using VibeRails.Services.Board.Tracker;

namespace VibeRails.Services.Jira;

/// <summary>The Jira fields a pull copies onto a new card at import.</summary>
public sealed record JiraMappedFields(
    string Title,
    string Description,
    string Type,
    string Priority,
    int? Points,
    IReadOnlyList<string> Tags,
    string StatusName,
    string? AssigneeDisplay);

/// <summary>
/// Pure mapping from one Jira issue onto the fields of the card a pull creates for it. A pull
/// copies them once, at import, and never again. Story points are read only when the connection
/// names the field id.
/// Points snapping, labels and lane decisions are the shared <see cref="TrackerFieldMapping"/> rules.
/// </summary>
public static class JiraFieldMapping
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = BoardCardLimits.MaxDescriptionLength;
    public const string OverflowLaneName = "Jira";

    public static JiraMappedFields Map(JiraIssue issue, string? storyPointsFieldId)
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
            MapType(issue.IssueTypeName),
            MapPriority(issue.PriorityName),
            MapPoints(issue, storyPointsFieldId),
            TrackerFieldMapping.MapLabels(issue.Labels),
            issue.StatusName.Trim(),
            string.IsNullOrWhiteSpace(issue.AssigneeDisplay) ? null : issue.AssigneeDisplay.Trim());
    }

    public static string MapType(string? issueTypeName)
    {
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
    /// Snap to the Fibonacci scale. No configured field, a missing value or an off-scale value
    /// imports the card without points.
    /// </summary>
    public static int? MapPoints(JiraIssue issue, string? storyPointsFieldId)
    {
        if (string.IsNullOrWhiteSpace(storyPointsFieldId))
            return null;
        return TrackerFieldMapping.SnapPoints(issue.StoryPoints);
    }
}
