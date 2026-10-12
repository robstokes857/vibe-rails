namespace VibeRails.Services.Jira;

/// <summary>
/// What the last pull applied to a linked issue, stored on <see cref="BoardJiraLinkRecord.Mapping"/>
/// as <c>points:{field};status:{name}</c>: the story points field it read (a new field re-reads an
/// unchanged issue) and the Jira status it saw (a change is noted in a comment; the card is never
/// moved). Links written before 1.12 hold <c>lane:{target};points:{field}</c> or null; the lane
/// part is ignored and the status is unknown, so the first status seen after the upgrade is
/// recorded silently.
/// </summary>
public static class JiraLinkMapping
{
    private const string PointsPrefix = "points:";
    private const string StatusPrefix = "status:";

    public static string Serialize(string? pointsField, string status) =>
        (string.IsNullOrEmpty(pointsField) ? string.Empty : PointsPrefix + pointsField + ";") + StatusPrefix + status;

    /// <summary>The points field and status a mapping names; null for each that it does not.</summary>
    public static (string? PointsField, string? Status) Parse(string? mapping)
    {
        if (string.IsNullOrEmpty(mapping))
            return (null, null);
        // The status is last and may contain anything, so it is read from its first marker on.
        var statusAt = mapping.IndexOf(StatusPrefix, StringComparison.Ordinal);
        var head = statusAt < 0 ? mapping : mapping[..statusAt];
        var status = statusAt < 0 ? null : mapping[(statusAt + StatusPrefix.Length)..];
        string? points = null;
        foreach (var segment in head.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith(PointsPrefix, StringComparison.Ordinal))
                points = segment[PointsPrefix.Length..] is { Length: > 0 } field ? field : null;
        }
        return (points, status);
    }
}
