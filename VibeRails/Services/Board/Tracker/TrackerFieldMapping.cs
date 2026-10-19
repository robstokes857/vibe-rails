namespace VibeRails.Services.Board.Tracker;

public enum TrackerLaneMatch { Matched, AlreadyThere, Overflow, Unresolved }

public sealed record TrackerLaneDecision(TrackerLaneMatch Match, string? ColumnId, string? ColumnName, bool Comment);

/// <summary>
/// The field and lane rules every tracker pull shares (Jira, Azure DevOps). Each tracker maps its
/// own types, priorities and text; these are the parts that must behave the same everywhere.
/// </summary>
public static class TrackerFieldMapping
{
    public const int MaxTags = 20;
    public const int MaxTagLength = 40;

    public static readonly int[] PointScale = [1, 2, 3, 5, 8, 13];

    /// <summary>
    /// A remote estimate rounded to a whole number, kept only when it is on the Fibonacci
    /// <see cref="PointScale"/>. Missing, zero, negative or off-scale values are null.
    /// </summary>
    public static int? SnapPoints(double? raw)
    {
        if (raw is not double value || double.IsNaN(value) || value <= 0)
            return null;
        var whole = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return PointScale.Contains(whole) ? whole : null;
    }

    /// <summary>Remote labels or tags as card tags: trimmed, blanks dropped, at most 20 of 40 characters.</summary>
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
    public static TrackerLaneDecision MatchLane(
        string statusName, string? currentColumnId, IReadOnlyList<BoardColumnRecord> lanes, string? overflowColumnId)
    {
        var name = statusName.Trim();
        var matches = lanes.Where(lane => string.Equals(lane.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        BoardColumnRecord? chosen = matches.Count == 1 ? matches[0] : null;
        if (chosen is null && matches.Count == 0 && overflowColumnId is not null)
            chosen = lanes.FirstOrDefault(lane => string.Equals(lane.Id, overflowColumnId, StringComparison.Ordinal));

        if (chosen is null)
            return new TrackerLaneDecision(TrackerLaneMatch.Unresolved, null, null, false);
        if (string.Equals(chosen.Id, currentColumnId, StringComparison.Ordinal))
            return new TrackerLaneDecision(TrackerLaneMatch.AlreadyThere, chosen.Id, chosen.Name, false);
        var overflow = matches.Count == 0;
        return new TrackerLaneDecision(overflow ? TrackerLaneMatch.Overflow : TrackerLaneMatch.Matched, chosen.Id, chosen.Name, true);
    }

    /// <summary>
    /// The same decision for a lane already chosen through the tracker board's columns (VIBE-102).
    /// A null or vanished lane means overflow: the overflow lane when it exists, else unresolved.
    /// </summary>
    public static TrackerLaneDecision DecideLane(
        string? laneId, string? currentColumnId, IReadOnlyList<BoardColumnRecord> lanes, string? overflowColumnId)
    {
        var chosen = laneId is null ? null : lanes.FirstOrDefault(lane => string.Equals(lane.Id, laneId, StringComparison.Ordinal));
        var overflow = chosen is null;
        if (chosen is null && overflowColumnId is not null)
            chosen = lanes.FirstOrDefault(lane => string.Equals(lane.Id, overflowColumnId, StringComparison.Ordinal));
        if (chosen is null)
            return new TrackerLaneDecision(TrackerLaneMatch.Unresolved, null, null, false);
        if (string.Equals(chosen.Id, currentColumnId, StringComparison.Ordinal))
            return new TrackerLaneDecision(TrackerLaneMatch.AlreadyThere, chosen.Id, chosen.Name, false);
        return new TrackerLaneDecision(overflow ? TrackerLaneMatch.Overflow : TrackerLaneMatch.Matched, chosen.Id, chosen.Name, true);
    }
}
